#!/usr/bin/env python3
"""Unpack and exercise the shipped sidecar, without a .NET runtime or network feeds."""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(port, path, body=None, authenticated=True):
    headers = {"X-SB-Api-Key": "archive-smoke-test"} if authenticated else {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=json.dumps(body).encode() if body is not None else None,
        headers=headers,
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve()
    with tempfile.TemporaryDirectory(prefix="stylobot-sidecar-smoke-") as directory:
        root = Path(directory).resolve()
        if zipfile.is_zipfile(archive):
            with zipfile.ZipFile(archive) as packed:
                for name in packed.namelist():
                    if not (root / name).resolve().is_relative_to(root):
                        raise RuntimeError(f"Unsafe archive member: {name}")
                packed.extractall(root)
        else:
            with tarfile.open(archive) as packed:
                for member in packed.getmembers():
                    if member.issym() or member.islnk() or not (root / member.name).resolve().is_relative_to(root):
                        raise RuntimeError(f"Unsafe archive member: {member.name}")
                packed.extractall(root)
        executable = root / ("stylobot-sidecar.exe" if os.name == "nt" else "stylobot-sidecar")
        grpc_port, rest_port = free_port(), free_port()
        while rest_port == grpc_port:
            rest_port = free_port()
        env = os.environ.copy()
        env.update({
            "STYLOBOT_PORT": str(grpc_port),
            "STYLOBOT_REST_PORT": str(rest_port),
            "STYLOBOT_GRPC_ONLY": "false",
            "STYLOBOT_BIND": "loopback",
            "BotDetection__DatabasePath": str(root / "data" / "botdetection.db"),
            "BotDetection__ApiKeys__archive-smoke-test__Name": "archive-smoke-test",
            "BotDetection__EnableBackgroundUpdates": "false",
        })
        # Keep the smoke independent of third-party feed availability, while
        # still loading every embedded YAML manifest and real SQLite store.
        manifests = Path(__file__).resolve().parent.parent / "src/Mostlylucid.BotDetection/Data/Sources"
        for manifest in manifests.glob("*.source.yaml"):
            match = re.search(r"^id:\s*(\w+)", manifest.read_text(), re.MULTILINE)
            if match:
                env[f"BotDetection__DataSources__{match[1]}__Enabled"] = "false"
        log_path = root / "sidecar.log"
        try:
            with log_path.open("w") as log:
                process = subprocess.Popen([str(executable)], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + 120
                    while True:
                        if process.poll() is not None:
                            raise RuntimeError(f"Sidecar exited during startup: {process.returncode}")
                        try:
                            status, health = request(rest_port, "/health")
                            if status == 200 and health["status"] == "healthy":
                                break
                        except (OSError, ValueError):
                            pass
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Sidecar health endpoint did not become ready")
                        time.sleep(0.2)
                    status, _ = request(rest_port, "/api/v1/summary", authenticated=False)
                    assert status == 401, f"Unauthenticated API returned {status}"
                    status, _ = request(rest_port, "/api/v1/summary")
                    assert status == 200, f"Authenticated summary returned {status}"
                    status, detection = request(rest_port, "/api/v1/detect", {
                        "method": "GET", "path": "/smoke", "remoteIp": "192.0.2.42",
                        "headers": {"User-Agent": "curl/8.10.1", "Accept": "*/*"},
                    })
                    assert status == 200, f"Detection returned {status}: {detection}"
                    assert detection["meta"]["detectorsRun"] > 0, detection
                    assert detection["reasons"], detection
                    grpc_client = Path(__file__).resolve().parent / "sidecar-grpc-smoke" / "SidecarGrpcSmoke.csproj"
                    subprocess.run([
                        "dotnet", "run", "--project", str(grpc_client),
                        "--configuration", "Release", "--", str(grpc_port),
                    ], check=True, timeout=60, stdout=log, stderr=subprocess.STDOUT)
                    assert process.poll() is None, "Sidecar died after detection"
                finally:
                    shutdown_requested = process.poll() is None
                    if shutdown_requested:
                        if os.name == "nt":
                            process.terminate()
                        else:
                            process.send_signal(signal.SIGINT)
                        try:
                            process.wait(timeout=30)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()
                            raise RuntimeError("Sidecar did not stop within 30 seconds")
                    if shutdown_requested and os.name != "nt" and process.returncode != 0:
                        raise RuntimeError(f"Sidecar shutdown returned {process.returncode}")
            logs = log_path.read_text(errors="replace")
            if re.search(r"\b(?:ERR|FTL)\]", logs):
                raise RuntimeError("Sidecar logged errors during startup/detection/shutdown")
            print(f"PASS {archive.name}: startup, SQLite/API reads, REST + gRPC authentication/detection, shutdown")
        except BaseException:
            print(log_path.read_text(errors="replace")[-24000:] if log_path.exists() else "No sidecar log")
            raise


if __name__ == "__main__":
    main()
