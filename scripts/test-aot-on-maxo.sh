#!/usr/bin/env bash
# test-aot-on-maxo.sh
# Build + run the stylobot AOT Console on a Windows x86_64 test box in
# production mode with SQLite, then drive k6 against it from this machine.
#
# Why standalone-AOT-on-Windows: the SQLite-decoupling work landed yesterday and
# we want a real production-mode soak/load on the standalone AOT binary (NOT the
# docker gateway stack) to see memory stability + storage growth on a long run.
#
# Subcommands:
#   build     update C:\build\stylobot to origin/main, AOT-publish win-x64
#   start     stop any prior run, start an upstream stub + the AOT gateway
#   stop      stop both
#   status    show what's listening + the gateway PID
#   stats     dump process memory + SQLite .db file sizes
#   load      run scripts/k6/k6-detection-only.js against the gateway
#   soak      run scripts/soak/k6-plateau.js against the gateway
#   deploy    build + start (one shot)
#   logs      tail the gateway log
#
# Required env:
#   HOST          test box address (e.g. 192.168.0.15)
#   SSH_USER      SSH account on $HOST
#   SSH_PASS      password (or omit and place it in ~/.config/stylobot/$HOST.pw)
#
# Optional env:
#   KNOWN_HOSTS_FILE  pin host key via this file (else default known_hosts; no -o StrictHostKeyChecking=no)
#   PORT              gateway listen port (default 5080)
#   UPSTREAM_PORT     stub upstream port  (default 9999)
#   MAX_RPS           soak ceiling        (default 300)

set -euo pipefail

: "${HOST:?Set HOST to the test box (e.g. HOST=192.168.0.15)}"
: "${SSH_USER:?Set SSH_USER to the SSH account on \$HOST}"

# SSH_PASS: prefer the env var (so secrets stay out of shell history); fall back
# to a file outside the repo (~/.config/stylobot/<host>.pw). Never hard-code.
if [ -z "${SSH_PASS:-}" ]; then
    PW_FILE="${HOME}/.config/stylobot/${HOST}.pw"
    if [ -f "$PW_FILE" ]; then
        SSH_PASS="$(cat "$PW_FILE")"
    else
        echo "error: SSH_PASS not set and $PW_FILE missing" >&2
        echo "       export SSH_PASS=... before running, or write the password to $PW_FILE" >&2
        exit 1
    fi
fi

# Pin host key via KNOWN_HOSTS_FILE if set, else fall back to the user's default.
SSHOPT="-o ConnectTimeout=10 -o PreferredAuthentications=password -o PubkeyAuthentication=no"
if [ -n "${KNOWN_HOSTS_FILE:-}" ]; then
    SSHOPT="$SSHOPT -o UserKnownHostsFile=$KNOWN_HOSTS_FILE -o StrictHostKeyChecking=yes"
fi
SSH="sshpass -p $SSH_PASS ssh $SSHOPT $SSH_USER@$HOST"
PSH() { $SSH "powershell -NoProfile -Command \"$1\""; }

PORT=${PORT:-5080}
UPSTREAM_PORT=${UPSTREAM_PORT:-9999}
MAX_RPS=${MAX_RPS:-300}
RID=win-x64

REPO='C:\build\stylobot'
RUN_DIR='C:\build\stylobot-aot-run'
BIN_SRC="$REPO\\src\\Mostlylucid.BotDetection.Console\\bin\\Release\\net10.0\\$RID\\publish\\stylobot.exe"
BIN_RUN="$RUN_DIR\\stylobot.exe"
LOG_OUT="$RUN_DIR\\stylobot.out.log"
LOG_ERR="$RUN_DIR\\stylobot.err.log"
PID_FILE="$RUN_DIR\\stylobot.pid"
UPSTREAM_PID_FILE="$RUN_DIR\\upstream.pid"

URL="http://$HOST:$PORT"

cmd=${1:-help}
case "$cmd" in

build)
    echo "=== Stop gateway first so its loaded DLLs (e_sqlite3.dll etc.) release ==="
    PSH "if (Test-Path '$PID_FILE') { Get-Content '$PID_FILE' | ForEach-Object { Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }; Remove-Item '$PID_FILE' -ErrorAction SilentlyContinue }; Get-Process stylobot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500"

    echo "=== Update C:\\build\\stylobot to origin/main ==="
    PSH "git -C $REPO fetch origin main 2>&1 | Out-String; git -C $REPO reset --hard origin/main; git -C $REPO log --oneline -1"

    echo ""
    echo "=== AOT-publish Console for $RID (takes ~3-5 min) ==="
    PSH "cd $REPO; dotnet publish src\\Mostlylucid.BotDetection.Console\\Mostlylucid.BotDetection.Console.csproj -c Release -r $RID --self-contained --nologo /clp:ErrorsOnly 2>&1 | Select-Object -Last 20"

    echo ""
    echo "=== Copy publish output to $RUN_DIR ==="
    PSH "if (-not (Test-Path '$RUN_DIR')) { New-Item -ItemType Directory -Path '$RUN_DIR' | Out-Null }; Copy-Item -Force -Recurse '$REPO\\src\\Mostlylucid.BotDetection.Console\\bin\\Release\\net10.0\\$RID\\publish\\*' '$RUN_DIR\\'; Get-Item '$BIN_RUN' | Select-Object FullName, Length, LastWriteTime | Format-List"
    ;;

start)
    echo "=== Stop any prior gateway + upstream ==="
    PSH "Get-Process stylobot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; if (Test-Path '$UPSTREAM_PID_FILE') { Get-Content '$UPSTREAM_PID_FILE' | ForEach-Object { Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }; Remove-Item '$UPSTREAM_PID_FILE' -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 500"

    echo "=== Wipe SQLite DBs (clean baseline for storage growth measurement) ==="
    PSH "Get-ChildItem '$RUN_DIR' -Filter *.db* -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue"

    echo "=== Start upstream stub on :$UPSTREAM_PORT (returns 'ok' to everything) ==="
    PSH "\$ups = Start-Process powershell -ArgumentList '-NoProfile','-WindowStyle','Hidden','-Command','\$l = New-Object System.Net.HttpListener; \$l.Prefixes.Add(\\\"http://+:$UPSTREAM_PORT/\\\"); \$l.Start(); while (\$true) { try { \$c = \$l.GetContext(); \$b = [System.Text.Encoding]::UTF8.GetBytes(\\\"ok\\\"); \$c.Response.OutputStream.Write(\$b, 0, \$b.Length); \$c.Response.Close() } catch {} }' -WindowStyle Hidden -PassThru; \$ups.Id | Out-File -Encoding ascii '$UPSTREAM_PID_FILE'; Start-Sleep -Milliseconds 800; Write-Host 'Upstream PID:' \$ups.Id"

    echo "=== Start stylobot AOT in production mode on :$PORT ==="
    PSH "\$key = & '$BIN_RUN' genkey; Write-Host 'Generated HMAC key (truncated):' \$key.Substring(0,12) '...'; \$psi = New-Object System.Diagnostics.ProcessStartInfo; \$psi.FileName = '$BIN_RUN'; \$psi.Arguments = '$PORT http://localhost:$UPSTREAM_PORT --mode production --verbose'; \$psi.WorkingDirectory = '$RUN_DIR'; \$psi.UseShellExecute = \$false; \$psi.RedirectStandardOutput = \$true; \$psi.RedirectStandardError = \$true; \$psi.EnvironmentVariables['SignatureLogging__SignatureHashKey'] = \$key; \$proc = [System.Diagnostics.Process]::Start(\$psi); \$proc.Id | Out-File -Encoding ascii '$PID_FILE'; Start-Job -ArgumentList \$proc,'$LOG_OUT' -ScriptBlock { param(\$p,\$f) \$p.StandardOutput.ReadToEnd() | Out-File -Append \$f } | Out-Null; Start-Job -ArgumentList \$proc,'$LOG_ERR' -ScriptBlock { param(\$p,\$f) \$p.StandardError.ReadToEnd() | Out-File -Append \$f } | Out-Null; Write-Host 'Gateway PID:' \$proc.Id"

    echo ""
    echo "=== Health check on $URL/health ==="
    for i in 1 2 3 4 5 6 7 8; do
        code=$(curl -s -o /dev/null -w "%{http_code}" -m 4 "$URL/health" 2>/dev/null || echo "000")
        if [ "$code" = "200" ]; then
            echo "  attempt $i: 200 OK"
            echo ""
            echo "Ready. Gateway URL: $URL"
            exit 0
        fi
        echo "  attempt $i: $code, sleeping 2s..."
        sleep 2
    done
    echo "Health check never returned 200. Check 'stylobot.err.log'."
    exit 1
    ;;

stop)
    echo "=== Stop gateway + upstream ==="
    PSH "if (Test-Path '$PID_FILE') { Get-Content '$PID_FILE' | ForEach-Object { Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }; Remove-Item '$PID_FILE' -ErrorAction SilentlyContinue }; if (Test-Path '$UPSTREAM_PID_FILE') { Get-Content '$UPSTREAM_PID_FILE' | ForEach-Object { Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }; Remove-Item '$UPSTREAM_PID_FILE' -ErrorAction SilentlyContinue }; Get-Process stylobot -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Write-Host 'Stopped.'"
    ;;

status)
    echo "=== Listening ports of interest ==="
    PSH "Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { \$_.LocalPort -in @($PORT,$UPSTREAM_PORT) } | Select-Object LocalPort, LocalAddress, OwningProcess | Format-Table -AutoSize"
    echo "=== Gateway process ==="
    PSH "if (Test-Path '$PID_FILE') { \$pid = (Get-Content '$PID_FILE').Trim(); Get-Process -Id \$pid -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, @{N='RSS_MB';E={[math]::Round(\$_.WorkingSet64/1MB,1)}}, @{N='CPU';E={\$_.CPU}}, StartTime | Format-Table -AutoSize } else { Write-Host '(no pid file)' }"
    ;;

stats)
    echo "=== Gateway memory + SQLite size on Maxo ==="
    PSH "if (Test-Path '$PID_FILE') { \$pid = (Get-Content '$PID_FILE').Trim(); \$p = Get-Process -Id \$pid -ErrorAction SilentlyContinue; if (\$p) { Write-Host ('Gateway: pid={0} rss={1} MB cpu={2}s up={3}' -f \$p.Id, [math]::Round(\$p.WorkingSet64/1MB,1), [math]::Round(\$p.CPU,1), ((Get-Date) - \$p.StartTime)) } else { Write-Host 'Gateway process not running.' } }; Write-Host ''; Write-Host '--- SQLite files ---'; Get-ChildItem '$RUN_DIR' -Filter *.db* -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object Name, @{N='KB';E={[math]::Round(\$_.Length/1KB,1)}}, LastWriteTime | Format-Table -AutoSize"
    ;;

logs)
    PSH "if (Test-Path '$LOG_OUT') { Get-Content '$LOG_OUT' -Tail 60 } else { Write-Host '(no stdout log yet)' }; Write-Host '--- stderr ---'; if (Test-Path '$LOG_ERR') { Get-Content '$LOG_ERR' -Tail 40 } else { Write-Host '(no stderr log)' }"
    ;;

load)
    echo "=== k6 detection-only load against $URL (~2 min) ==="
    k6 run scripts/k6/k6-detection-only.js --env BASE_URL="$URL"
    ;;

soak)
    echo "=== k6 plateau soak against $URL (MAX_RPS=$MAX_RPS, ~14 min) ==="
    k6 run scripts/soak/k6-plateau.js --env TARGET="$URL" --env MAX_RPS="$MAX_RPS"
    ;;

deploy)
    "$0" build
    "$0" start
    ;;

help|*)
    sed -n '2,30p' "$0"
    ;;
esac
