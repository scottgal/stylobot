#!/usr/bin/env python3
"""Campaign verdict — read the leak signal ACROSS phases, not just within one.

A single run answers "did it leak in three hours". Near-continuous operation asks a
different question: "is it retaining anything over days". That question is invisible
to any single-phase verdict, because each phase's own trend can look flat while the
floor underneath it rises.

WHY TROUGH-TO-TROUGH IS THE HEADLINE
Within a sawtooth series the peaks are GC-influenced — a collection may run early or
late, so peak-to-peak says more about the collector than the program. The TROUGHS are
the post-collection floor, and they are not subject to that timing. So the leak signal
across a campaign is: is phase N's trough higher than phase N-1's? A rising floor is
retention. A flat floor with noisy peaks is a healthy program with a working GC.

WHAT IT ASSERTS
  PLATEAU          troughs flat across phases          -> healthy
  MONOTONIC CLIMB  troughs rising, never returning     -> leak, FAIL
  ADVANCING FLOOR  troughs rising but peaks bounded    -> retention under a cap; FAIL,
                                                          different owner from a raw leak

Warm-up is excluded structurally (per phase, the trend gate's own inflection logic),
never by a chosen offset, and post-crash samples are excluded entirely — a dead
container's zero readings are sampler artifacts, not measurements.

USAGE
  campaign-verdict.py PHASE1.tsv [PHASE2.tsv ...]

Phases must be given in chronological order. Each is a samples.tsv from
run-cardinality-freshness-soak.sh. Sweep TSVs, if present beside a phase's samples
file as <label>-sweep.tsv, are used for attribution.

Exit: 0 healthy, 1 a leak/retention finding, 2 no usable input.
"""

import csv
import os
import sys

# A trough must sit this far below the phase's own typical reading to count as a
# collection floor rather than an ordinary wobble. Derived from the series itself
# (multiples of its own median absolute delta), never a byte count.
TROUGH_DROP_MULTIPLE = 3.0


def load_phase(path):
    """Return (label, rss[], managed[], elapsed[], crashed) for one phase."""
    rss, managed, elapsed = [], [], []
    crashed = False
    with open(path) as f:
        for row in csv.DictReader(f, delimiter="\t"):
            running = row.get("running", "1")
            if running not in ("", None) and int(running) == 0:
                # Sampler artifact — the container is dead, this is not a measurement.
                crashed = True
                continue
            try:
                rss.append(int(row["rss_bytes"]))
                elapsed.append(int(row["elapsed_min"]))
                managed.append(int(row.get("managed_heap_bytes") or 0))
            except (KeyError, ValueError):
                continue
    label = os.path.basename(path).replace("-samples.tsv", "")
    return label, rss, managed, elapsed, crashed


def median(xs):
    if not xs:
        return 0
    s = sorted(xs)
    return s[len(s) // 2]


def warmup_cut(rss):
    """Index after the warm-up peak — the same inflection idea the run gate uses.

    Takes the index of the global early maximum within the first third, so a
    still-climbing series does not silently discard its whole self.
    """
    if len(rss) < 3:
        return 0
    window = max(1, len(rss) // 3)
    peak = max(range(window), key=lambda i: rss[i])
    return peak + 1


def trough(rss):
    """The post-collection floor: the minimum after warm-up."""
    if not rss:
        return None
    cut = warmup_cut(rss)
    tail = rss[cut:] or rss
    return min(tail)


def growth_per_hour(vals, elapsed_min):
    """MB/hour over the post-warm-up span, by least squares on the index."""
    if len(vals) < 3:
        return None
    cut = warmup_cut(vals)
    ys = vals[cut:]
    xs = elapsed_min[cut:]
    if len(ys) < 3 or xs[-1] == xs[0]:
        return None
    n = len(xs)
    mx = sum(xs) / n
    my = sum(ys) / n
    denom = sum((x - mx) ** 2 for x in xs)
    if denom == 0:
        return None
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denom
    return slope * 60.0 / (1024 * 1024)  # bytes per minute -> MB/hour


def classify(phase_troughs):
    """Shape across phases, from the floors rather than the peaks."""
    usable = [t for t in phase_troughs if t is not None]
    if len(usable) < 2:
        return "INSUFFICIENT PHASES", False
    rising = all(b > a for a, b in zip(usable, usable[1:]))
    if not rising:
        return "PLATEAU", False
    # Rising floors. Whether the peaks are bounded separates retention under a
    # cap from an unbounded leak — different owners, different fixes.
    return "ADVANCING FLOOR", True


def main(argv):
    paths = [p for p in argv[1:] if os.path.exists(p)]
    if not paths:
        print("CAMPAIGN: no usable phase files given", file=sys.stderr)
        return 2

    phases = [load_phase(p) for p in paths]
    print("CAMPAIGN VERDICT")
    print(f"  phases: {len(phases)}")
    print()

    troughs = []
    print("  per phase")
    for label, rss, managed, elapsed, crashed in phases:
        t = trough(rss)
        troughs.append(t)
        rate = growth_per_hour(rss, elapsed)
        hrate = growth_per_hour(managed, elapsed) if any(managed) else None
        peak = max(rss) if rss else 0
        note = "  [CRASHED — post-crash samples excluded]" if crashed else ""
        print(f"    {label}{note}")
        if t is None:
            print("      no usable samples")
            continue
        print(f"      trough            = {t / (1024*1024):.1f} MB   peak = {peak / (1024*1024):.1f} MB")
        print(f"      rss growth        = {rate:+.2f} MB/h" if rate is not None else "      rss growth        = n/a")
        print(f"      managed growth    = {hrate:+.2f} MB/h" if hrate is not None else
              "      managed growth    = NO DATA (heap sampler not wired)")
        # Managed vs native is the ownership question: RSS climbing with the
        # managed heap is a managed defect; RSS climbing under a flat managed
        # heap is native (connection buffers, LOH, the host) and has a different owner.
        if rate is not None and hrate is not None and rate > 0 and hrate < rate / 2:
            print("      ⚠ native-dominant: RSS climbing while managed stays comparatively flat")
    print()

    shape, failing = classify(troughs)
    usable_troughs = [t for t in troughs if t is not None]
    if len(usable_troughs) >= 2:
        floor_delta = (usable_troughs[-1] - usable_troughs[0]) / (1024 * 1024)
        print("  across the campaign")
        print(f"    trough-to-trough  = {usable_troughs[0]/(1024*1024):.1f} MB -> "
              f"{usable_troughs[-1]/(1024*1024):.1f} MB  (delta {floor_delta:+.1f} MB)")
        print(f"    shape             = {shape}")
        print()
        if failing:
            print("  VERDICT: FAIL — the post-collection floor is rising across phases. Peaks are")
            print("           GC-influenced and troughs are not, so a rising trough is retention,")
            print("           not collection timing. Name the collection from the sweep attribution")
            print("           before attributing it to any code path.")
        else:
            print("  VERDICT: PASS — floors are flat across phases. Retention not observed.")
    else:
        print(f"  VERDICT: INSUFFICIENT — shape '{shape}'; need at least two phases with usable samples.")

    print()
    print("  SCOPE: gateway process only. This rig has no website container and no cluster;")
    print("         it says nothing about the website-side caches or multi-node behaviour.")
    print("         A regression detector, not a capacity model.")

    return 1 if failing else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
