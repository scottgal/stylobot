/**
 * StyloBot proof-of-work challenge solver. Served as a static file via
 * /bot-detection/challenge.js -- the HTML page emitted by
 * ChallengeActionPolicy.GenerateProofOfWorkHtml publishes a small
 * `window.MLChallenge = { id, seeds, requiredZeros, verifyUrl, returnUrl }`
 * config and then loads this file.
 *
 * Same architecture move as botdetection.js: ONE source of truth for the JS
 * (this file), versioned via the endpoint's ETag, reviewable as an artifact
 * instead of as a string embedded in C#. CSP-friendly: the host page emits a
 * nonce on the bootstrap; with `script-src 'self' 'strict-dynamic'` that
 * nonce propagates to the loaded static script.
 *
 * The solver spins up to `navigator.hardwareConcurrency` Workers (max 8),
 * each running SHA-256 puzzles until the hex hash starts with N zeros. As
 * each puzzle solves, progress increments + the next puzzle is dispatched
 * to the freed Worker. When all are solved, POSTs the solutions + timing
 * metadata to the verifyUrl. On 2xx, navigates to the returnUrl.
 */
(function () {
    'use strict';

    var cfg = window.MLChallenge || {};
    if (!cfg.id || !cfg.seeds || !cfg.verifyUrl) return;

    var challengeId = cfg.id;
    var seeds = cfg.seeds;
    var requiredZeros = cfg.requiredZeros | 0;
    var verifyUrl = cfg.verifyUrl;
    var returnUrl = cfg.returnUrl || '/';
    var puzzleCount = seeds.length;

    var startTime = performance.now();
    var puzzleTimings = [];
    var solutions = [];
    var solved = 0;

    function setStatus(text, cls) {
        var el = document.getElementById('status');
        if (!el) return;
        el.textContent = text;
        if (cls !== undefined) el.className = cls;
    }
    function setProgress(value) {
        var el = document.getElementById('progress');
        if (el) el.value = value;
    }

    // Worker source: tight SHA-256 puzzle loop. Inlined as a string so we
    // can wrap it in a Blob; the host page's CSP must permit
    // worker-src 'self' blob: for this to load. (If a stricter CSP is in
    // play, the operator can serve a separate /bot-detection/challenge-worker.js
    // and update this loader.)
    var workerCode =
        'self.onmessage = async function(e) {' +
        '  var seedB64 = e.data.seedB64;' +
        '  var seedIndex = e.data.seedIndex;' +
        '  var requiredZeros = e.data.requiredZeros;' +
        '  var seedBytes = Uint8Array.from(atob(seedB64), function(c){return c.charCodeAt(0);});' +
        '  var target = "0".repeat(requiredZeros);' +
        '  var encoder = new TextEncoder();' +
        '  var startMs = performance.now();' +
        '  var nonce = 0;' +
        '  while (true) {' +
        '    var nb = encoder.encode(nonce.toString());' +
        '    var input = new Uint8Array(seedBytes.length + nb.length);' +
        '    input.set(seedBytes, 0);' +
        '    input.set(nb, seedBytes.length);' +
        '    var hash = await crypto.subtle.digest("SHA-256", input);' +
        '    var arr = new Uint8Array(hash);' +
        '    var hex = "";' +
        '    for (var i = 0; i < arr.length; i++) {' +
        '      var b = arr[i].toString(16);' +
        '      if (b.length < 2) hex += "0";' +
        '      hex += b;' +
        '    }' +
        '    if (hex.indexOf(target) === 0) {' +
        '      self.postMessage({seedIndex: seedIndex, nonce: nonce, solveMs: performance.now() - startMs});' +
        '      return;' +
        '    }' +
        '    nonce++;' +
        '    if (nonce % 5000 === 0) await new Promise(function(r){setTimeout(r, 0);});' +
        '  }' +
        '};';

    var blob = new Blob([workerCode], { type: 'application/javascript' });
    var workerUrl = URL.createObjectURL(blob);

    var maxWorkers = Math.min(navigator.hardwareConcurrency || 2, 8);
    var workerCount = Math.min(maxWorkers, seeds.length);

    var nextPuzzle = 0;
    function launchNext(worker) {
        if (nextPuzzle >= seeds.length) { worker.terminate(); return; }
        var s = seeds[nextPuzzle++];
        worker.postMessage({ seedB64: s.seed, seedIndex: s.index, requiredZeros: requiredZeros });
    }

    for (var w = 0; w < workerCount; w++) {
        var worker = new Worker(workerUrl);
        worker.onmessage = (function (workerRef) {
            return function (e) {
                solutions.push({ seedIndex: e.data.seedIndex, nonce: e.data.nonce });
                puzzleTimings.push(e.data.solveMs);
                solved++;
                setProgress(solved);
                setStatus('Solving ' + solved + ' / ' + puzzleCount + ' puzzles...');
                if (solved === seeds.length) {
                    submitSolutions();
                } else {
                    launchNext(workerRef);
                }
            };
        })(worker);
        launchNext(worker);
    }

    async function submitSolutions() {
        setStatus('Verified!', 'success');
        var totalTimeMs = performance.now() - startTime;
        var payload = {
            challengeId: challengeId,
            solutions: solutions.sort(function (a, b) { return a.seedIndex - b.seedIndex; }),
            metadata: {
                workerCount: workerCount,
                totalTimeMs: totalTimeMs,
                puzzleTimingsMs: puzzleTimings,
            },
            returnUrl: decodeURIComponent(returnUrl),
        };
        try {
            var resp = await fetch(verifyUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
                credentials: 'same-origin',
            });
            if (resp.ok) {
                var data = await resp.json();
                window.location.href = data.returnUrl || '/';
            } else {
                setStatus('Verification failed. Please refresh.', '');
            }
        } catch (err) {
            setStatus('Network error. Please refresh.', '');
        }
    }
})();
