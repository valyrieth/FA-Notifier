#!/usr/bin/env python3
# /// script
# requires-python = ">=3.9"
# dependencies = ["rich"]
# ///
"""Validate the code, then tag and push a release (the Docker workflow publishes it).

    uv run scripts/release.py              # interactive
    uv run scripts/release.py --dry-run    # run every check, change nothing
    uv run scripts/release.py --docker     # also build the Docker image locally
"""
import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

try:
    from rich.console import Console
    from rich.panel import Panel
    from rich.prompt import Confirm, Prompt
    from rich.table import Table
except ImportError:
    sys.exit(
        "This script needs the rich package. Either run it with uv (installs it for you):\n"
        "  uv run scripts/release.py\n"
        "or install it in a virtual environment first:\n"
        "  python3 -m venv .venv && .venv/bin/pip install -r scripts/requirements.txt\n"
        "  .venv/bin/python scripts/release.py"
    )

console = Console()
SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$")
ICONS = {"ok": "[green]✔[/]", "warn": "[yellow]![/]", "fail": "[red]✘[/]"}


def run(*cmd):
    result = subprocess.run(cmd, capture_output=True, text=True)
    return result.returncode, (result.stdout + result.stderr).strip()


def git(*args):
    code, output = run("git", *args)
    if code != 0:
        raise RuntimeError(output or f"git {' '.join(args)} failed")
    return output


def tail(output, lines=12):
    return "\n".join(output.splitlines()[-lines:])


def repo_slug():
    match = re.search(r"github\.com[:/](.+?)(?:\.git)?$", git("remote", "get-url", "origin"))
    return match.group(1) if match else None


# Each check returns (status, detail); status is "ok", "warn" or "fail".
def check_clean():
    changes = git("status", "--porcelain")
    return ("fail", f"{len(changes.splitlines())} uncommitted change(s)") if changes else ("ok", "working tree clean")


def check_branch():
    branch = git("branch", "--show-current")
    return ("ok", "on main") if branch == "main" else ("fail", f"on '{branch}', releases are cut from main")


def check_synced():
    code, output = run("git", "fetch", "origin", "main", "--tags", "--quiet")
    if code != 0:
        return "fail", f"git fetch failed: {tail(output, 3)}"
    behind, ahead = git("rev-list", "--left-right", "--count", "origin/main...HEAD").split()
    if behind != "0":
        return "fail", f"{behind} commit(s) behind origin/main, run git pull"
    if ahead != "0":
        return "fail", f"{ahead} commit(s) not pushed yet"
    return "ok", "in sync with origin/main"


# Fixers ask first, act, and return True if they changed anything worth re-checking.
def fix_clean():
    console.print(Panel(git("status", "--short"), title="Uncommitted changes", border_style="yellow"))
    choice = Prompt.ask("What should happen to them?", choices=["commit", "stash", "abort"], default="stash")
    if choice == "abort":
        return False
    if choice == "commit":
        message = Prompt.ask("Commit message")
        git("add", "-A")
        git("commit", "-m", message)
        console.print("[green]Committed on", git("branch", "--show-current") + ".[/]")
    else:
        git("stash", "push", "--include-untracked", "-m", "release.py auto-stash")
        console.print("[green]Stashed.[/] Restore later with [bold]git stash pop[/].")
    return True


def fix_branch():
    if not Confirm.ask("Switch to main and update it from origin?", default=True):
        return False
    git("checkout", "main")
    git("pull", "--ff-only", "origin", "main")
    console.print("[green]On main and up to date.[/]")
    return True


def fix_synced():
    behind, ahead = (int(n) for n in git("rev-list", "--left-right", "--count", "origin/main...HEAD").split())
    if behind and not ahead:
        if not Confirm.ask(f"Pull {behind} new commit(s) from origin/main?", default=True):
            return False
        git("pull", "--ff-only", "origin", "main")
        return True
    if ahead and not behind:
        if not Confirm.ask(f"Push {ahead} local commit(s) to origin/main?", default=False):
            return False
        git("push", "origin", "main")
        return True
    console.print("[red]main has diverged from origin/main; resolve it by hand.[/]")
    return False


FIXERS = {"Working tree": fix_clean, "Branch": fix_branch, "Up to date": fix_synced}


def check_ci():
    slug = repo_slug()
    if not slug:
        return "warn", "origin is not a GitHub repo, skipped"
    sha = git("rev-parse", "HEAD")
    url = f"https://api.github.com/repos/{slug}/commits/{sha}/check-runs"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "release-script"}), timeout=15) as response:
            runs = json.load(response)["check_runs"]
    except (urllib.error.URLError, OSError, ValueError, KeyError) as error:
        return "warn", f"could not read GitHub checks ({error}), skipped"
    if not runs:
        return "warn", "GitHub reports no checks for this commit yet"
    pending = [r["name"] for r in runs if r["status"] != "completed"]
    failed = [r["name"] for r in runs if r["status"] == "completed" and r["conclusion"] not in ("success", "skipped", "neutral")]
    if failed:
        return "fail", "failing on GitHub: " + ", ".join(failed)
    if pending:
        return "warn", "still running on GitHub: " + ", ".join(pending)
    return "ok", f"{len(runs)} GitHub check(s) green for {sha[:7]}"


def command_check(label, *cmd, fail_if=None):
    def check():
        code, output = run(*cmd)
        failed = code != 0 or (fail_if is not None and fail_if in output)
        return ("fail", tail(output)) if failed else ("ok", label)

    return check


def build_checks(include_docker):
    checks = [
        ("Working tree", check_clean),
        ("Branch", check_branch),
        ("Up to date", check_synced),
        ("GitHub CI", check_ci),
        ("Restore", command_check("packages restored", "dotnet", "restore")),
        ("Format", command_check("code style clean", "dotnet", "format", "--verify-no-changes", "--no-restore")),
        (
            "Build",
            command_check(
                "release build, warnings as errors",
                "dotnet", "build", "-c", "Release", "--no-restore", "-warnaserror",
                "-p:EnforceCodeStyleInBuild=true", "-p:AnalysisLevel=latest",
            ),
        ),
        (
            "Vulnerabilities",
            command_check(
                "no vulnerable NuGet packages",
                "dotnet", "list", "package", "--vulnerable", "--include-transitive",
                fail_if="has the following vulnerable packages",
            ),
        ),
    ]
    if include_docker:
        checks.append((
            "Docker image",
            command_check(
                "image builds",
                "docker", "build", "-t", "fa-notifier:release-check", "--build-arg", "VERSION=0.0.0-release-check", ".",
            ),
        ))
    return checks


def run_checks(checks, fixers):
    failures = 0
    warnings = 0
    for name, check in checks:
        status, detail = evaluate(name, check)
        if status == "fail" and name in fixers:
            console.print(ICONS[status], f"[bold]{name}[/]", detail, highlight=False)
            try:
                fixed = fixers[name]()
            except RuntimeError as error:
                console.print(f"[red]Fix failed:[/] {error}")
                fixed = False
            if fixed:
                status, detail = evaluate(name, check)
            else:
                return failures + 1, warnings
        failures += status == "fail"
        warnings += status == "warn"
        console.print(ICONS[status], f"[bold]{name}[/]", f"[dim]{detail}[/]" if status == "ok" else detail, highlight=False)
        if status == "fail" and name in fixers:
            return failures, warnings
    return failures, warnings


def evaluate(name, check):
    with console.status(f"[cyan]{name}…[/]", spinner="dots"):
        try:
            return check()
        except RuntimeError as error:
            return "fail", str(error)


def latest_tag():
    code, output = run("git", "describe", "--tags", "--abbrev=0", "--match", "v*")
    return output if code == 0 else None


def suggest_versions(last):
    match = SEMVER.match(last[1:]) if last else None
    if not match:
        return {"first": "1.0.0"}
    major, minor, patch = (int(part) for part in match.groups()[:3])
    return {
        "patch": f"{major}.{minor}.{patch + 1}",
        "minor": f"{major}.{minor + 1}.0",
        "major": f"{major + 1}.0.0",
        "rc": f"{major}.{minor}.{patch + 1}-rc.1",
    }


def show_changes(last):
    log_range = f"{last}..HEAD" if last else "HEAD"
    commits = git("log", log_range, "--pretty=format:%h\t%an\t%s", "-n", "15").splitlines()
    if not commits:
        console.print("[yellow]No commits since the last tag.[/]")
        return 0
    table = Table(title=f"Changes since {last or 'the beginning'}", title_justify="left", header_style="bold")
    table.add_column("Commit", style="cyan", no_wrap=True)
    table.add_column("Author")
    table.add_column("Message")
    for line in commits:
        table.add_row(*line.split("\t", 2))
    console.print(table)
    return len(commits)


def choose_version(last):
    options = suggest_versions(last)
    console.print("\n[bold]Pick the version[/]")
    for key, version in options.items():
        console.print(f"  [cyan]{key:<6}[/] v{version}")
    console.print("  [cyan]custom[/] type your own\n")
    choice = Prompt.ask("Release", choices=[*options, "custom"], default=next(iter(options)))
    version = Prompt.ask("Version (no leading v)") if choice == "custom" else options[choice]
    return version.removeprefix("v")


def tag_available(tag):
    if run("git", "rev-parse", "-q", "--verify", f"refs/tags/{tag}")[0] == 0:
        return "tag already exists locally"
    code, output = run("git", "ls-remote", "--tags", "origin", f"refs/tags/{tag}")
    if code == 0 and output:
        return "tag already exists on origin"
    return None


def sign_tags():
    _, value = run("git", "config", "--get", "--type=bool", "tag.gpgsign")
    return value == "true"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="run the checks and show what would happen, but do not tag or push")
    parser.add_argument("--docker", action="store_true", help="also build the Docker image locally (slower)")
    args = parser.parse_args()

    console.print(Panel.fit("[bold magenta]FA Notify[/] release", border_style="magenta"))
    if run("git", "rev-parse", "--show-toplevel")[0] != 0:
        sys.exit("Run this from inside the repository.")
    os.chdir(git("rev-parse", "--show-toplevel"))

    console.rule("[bold]Validation")
    failures, warnings = run_checks(build_checks(args.docker), {} if args.dry_run else FIXERS)
    if failures:
        console.print(f"\n[red bold]{failures} check(s) failed.[/] Fix them and run this again.")
        return 1
    if warnings and not Confirm.ask(f"\n[yellow]{warnings} warning(s).[/] Continue anyway?", default=False):
        return 1

    console.rule("[bold]Release")
    last = latest_tag()
    if show_changes(last) == 0 and not Confirm.ask("Release anyway?", default=False):
        return 1

    while True:
        version = choose_version(last)
        if not SEMVER.match(version):
            console.print(f"[red]'{version}' is not a valid version.[/] Use MAJOR.MINOR.PATCH, optionally with -suffix.")
            continue
        problem = tag_available(f"v{version}")
        if problem:
            console.print(f"[red]v{version}: {problem}.[/]")
            continue
        break

    prerelease = "-" in version
    major_minor = ".".join(version.split("-")[0].split(".")[:2])
    images = [version] if prerelease else [version, major_minor, "latest"]
    signed = sign_tags()
    summary = Table.grid(padding=(0, 2))
    summary.add_row("[bold]Tag[/]", f"v{version}" + (" [yellow](pre-release)[/]" if prerelease else ""))
    summary.add_row("[bold]Commit[/]", git("log", "-1", "--pretty=%h %s"))
    summary.add_row("[bold]Tag type[/]", "signed" if signed else "annotated, unsigned")
    summary.add_row("[bold]Image tags[/]", ", ".join(images))
    console.print(Panel(summary, title="Ready to release", border_style="green"))

    if args.dry_run:
        console.print("[cyan]Dry run:[/] would run [bold]git tag " + ("-s" if signed else "-a") + f" v{version}[/] and [bold]git push origin v{version}[/].")
        return 0
    if not Confirm.ask("Create the tag and push it?", default=False):
        console.print("Cancelled, nothing was changed.")
        return 1

    git("tag", "-s" if signed else "-a", f"v{version}", "-m", f"v{version}")
    try:
        git("push", "origin", f"v{version}")
    except RuntimeError as error:
        git("tag", "-d", f"v{version}")
        console.print(f"[red]Push failed, local tag removed:[/] {error}")
        return 1

    slug = repo_slug()
    links = f"https://github.com/{slug}/actions\nhttps://github.com/{slug}/releases" if slug else "Check the Actions tab."
    console.print(Panel(f"[bold green]v{version} pushed.[/] The workflow is building and publishing it.\n\n{links}", border_style="green"))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (KeyboardInterrupt, EOFError):
        console.print("\nCancelled.")
        sys.exit(130)
    except RuntimeError as error:
        console.print(f"[red]{error}[/]")
        sys.exit(1)
