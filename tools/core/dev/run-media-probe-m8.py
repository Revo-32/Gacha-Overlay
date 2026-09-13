"""Synthetic media worker checks only. No Production config, secrets or network.

Publish directory is uploaded separately to the explicit dev location. Test
inputs and disposable derivatives live outside /srv/data and operational backups.
"""
import json
import os
from pathlib import Path
import subprocess
import uuid

ROOT = Path("/srv/apps/lsoverlay-core-dev/m4")
RESULTS = Path("/srv/cache/lsoverlay-core-dev/media-validation")
RUNTIME = "sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19"
PRODUCTION = ("lsoverlay-backend-1", "lsoverlay-status-1")


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], check=True, capture_output=True, **kwargs)


def stamps():
    # Never request/log environment values.
    result = docker("inspect", "--format", "{{.Id}} {{.Image}} {{.State.StartedAt}} {{.State.Status}} {{.State.Health.Status}}", *PRODUCTION)
    lines = result.stdout.decode().splitlines()
    if len(lines) != 2 or any(not line.endswith(" running healthy") for line in lines):
        raise RuntimeError("Production health precondition changed")
    return lines


def main():
    if os.getuid() != 1000 or not (ROOT / "probe/LSOverlay.CoreMediaProbe.dll").is_file():
        raise RuntimeError("Explicit dev worker/user required")
    for item in (ROOT, ROOT / "probe", RESULTS, *RESULTS.parents):
        if item.is_symlink():
            raise RuntimeError("Dev symlink rejected")
    RESULTS.mkdir(mode=0o700, parents=True, exist_ok=True)
    if RESULTS.stat().st_mode & 0o077:
        raise RuntimeError("Private test artifact root required")
    before = stamps()
    run = uuid.uuid4().hex
    name = "lsoverlay-core-media-test-" + run
    try:
        result = docker("run", "--rm", "--name", name, "--label", "ls-overlay.media-test=" + run,
                        "--network", "none", "--read-only", "--user", "1000:1000",
                        "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                        "--memory", "512m", "--cpus", "0.5", "--pids-limit", "64",
                        "--log-driver", "none", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                        "--mount", f"type=bind,src={ROOT / 'probe'},dst=/probe,readonly",
                        "--mount", f"type=bind,src={RESULTS},dst=/validation",
                        "--env", "DOTNET_EnableDiagnostics=0", "--workdir", "/probe", "--entrypoint", "dotnet",
                        RUNTIME, "/probe/LSOverlay.CoreMediaProbe.dll", "--verify", "/validation/" + run,
                        timeout=45)
        summary = json.loads(result.stdout)
        if summary.get("status") != "PASS":
            raise RuntimeError("Synthetic media tests failed")
        print(json.dumps({"summary": summary, "resultDirectory": str(RESULTS / run), "network": "none"}))
    except subprocess.CalledProcessError as error:
        # These fixtures contain no real data; only bounded diagnostic text.
        print(error.stderr.decode(errors="replace")[-2000:])
        raise RuntimeError("Synthetic media worker failed") from None
    finally:
        check = subprocess.run(["docker", "inspect", name], capture_output=True)
        if check.returncode == 0:
            own = json.loads(check.stdout)[0]
            if own["Image"] != RUNTIME or own["Config"].get("Labels", {}).get("ls-overlay.media-test") != run:
                raise RuntimeError("Refusing cleanup of unrelated container")
            docker("rm", "-f", own["Id"])
        if stamps() != before:
            raise RuntimeError("Production identity/start/health changed")
        print("Production identities, starts and health unchanged.")


if __name__ == "__main__":
    main()
