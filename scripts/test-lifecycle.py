#!/usr/bin/env python3
"""Run startup-wiring regressions and real-process shutdown/schema smoke tests."""
import argparse
import pathlib
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", default="Debug", choices=("Debug", "Release"))
    parser.add_argument("--no-build", action="store_true", help="Use already-built unit tests (the small process harness is still built)")
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    project = root / "scripts/lifecycle-harness/LifecycleHarness.csproj"

    with tempfile.TemporaryDirectory(prefix="stylobot-lifecycle-") as temporary:
        work = pathlib.Path(temporary)
        tests = [
            "dotnet", "test", "src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj",
            "--configuration", args.configuration, "--no-restore",
            "--filter", "FullyQualifiedName~Scheduling|FullyQualifiedName~Storage|FullyQualifiedName~WaveformHistoryStore|FullyQualifiedName~SqlitePoolCollisionStore|FullyQualifiedName~CentroidWriteBehindStore|FullyQualifiedName~SessionPersistenceServiceTickTests|FullyQualifiedName~DetectionHttpContextAccessorTests|FullyQualifiedName~DetectResponseClassificationTests",
            "--logger", "trx;LogFileName=lifecycle.trx", "--results-directory", str(work),
            "--verbosity", "quiet",
        ]
        if args.no_build:
            tests.append("--no-build")
        subprocess.run(tests, cwd=root, check=True, timeout=600)
        counters = ET.parse(work / "lifecycle.trx").find(".//{*}Counters")
        if counters is None or int(counters.attrib["executed"]) == 0:
            raise RuntimeError("Lifecycle test filter executed no tests")

        subprocess.run(["dotnet", "build", str(project), "--configuration", args.configuration,
                        "--verbosity", "quiet"], cwd=root, check=True, timeout=600)
        assembly = project.parent / "bin" / args.configuration / "net10.0/LifecycleHarness.dll"
        for scenario, expected in (("schema", 0), ("schema-restart", 0), ("silent", 1), ("storm", 1), ("healthy", 0), ("disabled", 0)):
            database = work / ("schema" if scenario == "schema-restart" else scenario)
            database.mkdir(exist_ok=True)
            result = subprocess.run(["dotnet", str(assembly), scenario, str(database)],
                                    cwd=root, capture_output=True, text=True, timeout=20)
            marker = f"Lifecycle harness completed: {scenario}"
            if result.returncode != expected or marker not in result.stdout:
                raise RuntimeError(f"{scenario}: expected exit {expected}, got {result.returncode}\n"
                                   f"{result.stdout}\n{result.stderr}")
            print(f"PASS {scenario}: process exit {result.returncode}", flush=True)


if __name__ == "__main__":
    main()
