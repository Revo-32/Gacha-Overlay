"""One-shot SSH operator probe. No secrets on command lines, disk, or stdout.

Run as the existing M8 uid 1000. Only the already-approved bot token is read.
Production data/OAuth secret/credential registry are not copied or mounted.
"""
import json
import os
import pathlib
import subprocess
import sys
import uuid

ROOT = pathlib.Path("/srv/apps/lsoverlay-core-read-probe")
IMAGE = "sha256:b92549a8741a7ef16b3d9621488accf42a601e675cc4950cd2775d9d9f778b19"
PRODUCTION = ("lsoverlay-backend-1", "lsoverlay-status-1")
NETWORK = "lsoverlay-core-read-probe"


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], check=True, capture_output=True, **kwargs)


def inspect(name):
    return json.loads(docker("inspect", name).stdout)[0]


def stamp(container):
    return (container["Id"], container["Image"], container["State"]["StartedAt"],
            container["State"]["Status"], container["State"].get("Health", {}).get("Status"))


def main():
    if len(sys.argv) != 2 or not sys.argv[1].isascii() or not sys.argv[1].isdigit() or not 0 < int(sys.argv[1]) < 2 ** 64:
        raise RuntimeError("Explicit existing selected channel ID required")
    if os.getuid() != 1000 or ROOT.resolve() != ROOT or not (ROOT / "publish/LSOverlay.CoreReadProbe.dll").is_file():
        raise RuntimeError("Probe location/user precondition failed")
    if any(p.is_symlink() for p in (ROOT, ROOT / "publish", ROOT / "results")):
        raise RuntimeError("Probe symlink rejected")
    containers = [inspect(name) for name in PRODUCTION]
    before = [stamp(item) for item in containers]
    if any(item["State"]["Status"] != "running" or item["State"].get("Health", {}).get("Status") != "healthy" for item in containers):
        raise RuntimeError("Production health precondition failed")
    if containers[0]["Image"] != IMAGE:
        raise RuntimeError("Production baseline changed")
    # Docker's inspect response is kept only in process memory; never printed.
    env = dict(value.split("=", 1) for value in containers[0]["Config"]["Env"])
    config = {"GuildId": int(env["LSO_DISCORD_GUILD_ID"]),
              "ApplicationId": int(env["LSO_DISCORD_OAUTH_CLIENT_ID"]),
              "ChatChannelId": int(sys.argv[1]),
              "BotToken": env["LSO_DISCORD_BOT_TOKEN"]}
    del env, containers
    results = ROOT / "results"
    results.mkdir(mode=0o700, exist_ok=True)
    if results.stat().st_mode & 0o077:
        raise RuntimeError("Private result directory required")
    run_id = uuid.uuid4().hex
    # No pre-existing network is repurposed. This command fails if it exists.
    docker("network", "create", "--label", "ls-overlay.scope=core-readonly-probe", NETWORK)
    try:
        result = docker("run", "--rm", "-i", "--name", "lsoverlay-core-read-probe-" + run_id,
                        "--label", "ls-overlay.probe-run=" + run_id,
                        "--network", NETWORK, "--read-only", "--user", "1000:1000",
                        "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                        "--memory", "256m", "--cpus", "0.5", "--pids-limit", "64",
                        "--log-driver", "none", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                        "--mount", f"type=bind,src={ROOT / 'publish'},dst=/probe,readonly",
                        "--mount", f"type=bind,src={results},dst=/capture",
                        "--env", "DOTNET_EnableDiagnostics=0", "--workdir", "/probe",
                        "--entrypoint", "dotnet", IMAGE, "/probe/LSOverlay.CoreReadProbe.dll",
                        "--operator-readonly", "/capture/" + run_id,
                        input=json.dumps(config).encode(), timeout=80)
        config["BotToken"] = ""
        summary = json.loads(result.stdout)
        if summary.get("mode") != "operator-readonly-one-shot":
            raise RuntimeError("Unexpected probe output")
        print(json.dumps({"resultDirectory": str(results / run_id), "summary": summary}))
    except subprocess.CalledProcessError as error:
        # The probe emits only a fixed stage/type failure, not exception messages.
        diagnostic = error.stderr.decode(errors="replace").strip()
        if diagnostic.startswith("Core read-only probe stopped:") and len(diagnostic) < 250:
            print(diagnostic, file=sys.stderr)
        raise RuntimeError("Read-only probe failed; no automatic retry") from None
    finally:
        config["BotToken"] = ""
        # Timeout can terminate the Docker CLI before its child exits. Resolve
        # and remove ONLY this invocation's labeled ephemeral container.
        remaining = subprocess.run(["docker", "inspect", "lsoverlay-core-read-probe-" + run_id], capture_output=True)
        if remaining.returncode == 0:
            own = json.loads(remaining.stdout)[0]
            if own["Image"] != IMAGE or own["Config"].get("Labels", {}).get("ls-overlay.probe-run") != run_id:
                raise RuntimeError("Unexpected container ownership; cleanup refused")
            docker("rm", "--force", own["Id"])
        after = [stamp(inspect(name)) for name in PRODUCTION]
        network = json.loads(docker("network", "inspect", NETWORK).stdout)[0]
        if not network["Containers"]:
            docker("network", "rm", NETWORK)
        if after != before:
            raise RuntimeError("Production baseline/health changed during probe")
        print("Production container identity/start/image/health unchanged.", file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Operator probe stopped: " + type(error).__name__ + ". No secret-bearing diagnostics printed.", file=sys.stderr)
        sys.exit(1)
