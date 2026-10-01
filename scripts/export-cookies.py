#!/usr/bin/env python3
# /// script
# requires-python = ">=3.9"
# dependencies = ["browser-cookie3"]
# ///
"""Export your Fur Affinity login cookies from a browser to a Netscape cookies.txt.

Run this on the computer where you are signed in to Fur Affinity, then copy the
resulting file next to docker-compose.yml. Only furaffinity.net cookies are written.

    uv run scripts/export-cookies.py firefox
    pipx run scripts/export-cookies.py          # tries every supported browser
    pip install -r scripts/requirements.txt && python scripts/export-cookies.py chrome
"""
import argparse
import datetime
import os
import sys

try:
    import browser_cookie3
except ImportError:
    sys.exit(
        "This script needs the browser-cookie3 package. Either run it with uv (installs it for you):\n"
        "  uv run scripts/export-cookies.py\n"
        "or install it in a virtual environment first:\n"
        "  python3 -m venv .venv && .venv/bin/pip install -r scripts/requirements.txt\n"
        "  .venv/bin/python scripts/export-cookies.py\n"
        "(on Windows use .venv\\Scripts\\pip and .venv\\Scripts\\python)"
    )

DOMAIN = "furaffinity.net"
SESSION_COOKIES = {"a", "b"}
BROWSERS = ["firefox", "librewolf", "chrome", "chromium", "edge", "brave", "opera", "opera_gx", "vivaldi", "arc", "safari"]


def is_fur_affinity(cookie):
    domain = cookie.domain.lstrip(".").lower()
    return domain == DOMAIN or domain.endswith("." + DOMAIN)


def to_netscape(cookie):
    http_only = cookie.has_nonstandard_attr("HTTPOnly")
    include_subdomains = "TRUE" if cookie.domain.startswith(".") else "FALSE"
    fields = [
        ("#HttpOnly_" if http_only else "") + cookie.domain,
        include_subdomains,
        cookie.path,
        "TRUE" if cookie.secure else "FALSE",
        str(int(cookie.expires or 0)),
        cookie.name,
        cookie.value,
    ]
    return "\t".join(fields)


def write_private(path, text):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as file:
        file.write(text)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass  # Windows has no POSIX modes.


def find_login(browsers):
    """Return (browser, cookies, problems) for the first browser that has a Fur Affinity login."""
    problems = []
    for name in browsers:
        try:
            jar = getattr(browser_cookie3, name)(domain_name=DOMAIN)
            cookies = [cookie for cookie in jar if is_fur_affinity(cookie)]
        except Exception as error:  # Each browser fails differently: no profile, locked database, no keyring...
            reason = str(error) if isinstance(error, browser_cookie3.BrowserCookieError) else f"{type(error).__name__}: {error}"
            problems.append(f"{name}: {reason}")
            continue
        if SESSION_COOKIES <= {cookie.name for cookie in cookies}:
            return name, cookies, problems
        problems.append(f"{name}: no Fur Affinity login found")
    return None, [], problems


def main():
    available = [name for name in BROWSERS if hasattr(browser_cookie3, name)]
    parser = argparse.ArgumentParser(description="Export Fur Affinity cookies from a browser to cookies.txt.")
    parser.add_argument("browser", nargs="?", choices=available, help="browser to read; omit to try all of them")
    parser.add_argument("-o", "--output", default="cookies.txt", help="file to write (default: cookies.txt)")
    args = parser.parse_args()

    browser, cookies, problems = find_login([args.browser] if args.browser else available)
    if browser is None:
        details = "\n".join(f"  - {problem}" for problem in problems)
        sys.exit(
            f"Could not export a Fur Affinity login:\n{details}\n"
            "Sign in to furaffinity.net in the browser, close it, and try again. Firefox is the most "
            "reliable; recent Chrome/Edge versions on Windows encrypt cookies so other programs cannot read them."
        )

    names = {cookie.name for cookie in cookies}
    lines = ["# Netscape HTTP Cookie File", "# Exported by scripts/export-cookies.py - treat this file like a password."]
    lines += [to_netscape(cookie) for cookie in sorted(cookies, key=lambda c: (c.domain, c.name))]
    write_private(args.output, "\n".join(lines) + "\n")

    expiries = [cookie.expires for cookie in cookies if cookie.name in SESSION_COOKIES and cookie.expires]
    print(f"Wrote {len(cookies)} Fur Affinity cookies from {browser} to {args.output} ({', '.join(sorted(names))}).")
    if expiries:
        expiry = datetime.datetime.fromtimestamp(min(expiries), datetime.timezone.utc)
        print(f"The login cookies expire on {expiry:%Y-%m-%d}.")
    if "cf_clearance" in names:
        print("Note: cf_clearance is tied to your browser's User-Agent; set the same value as userAgent in settings.json.")
    print("Copy the file next to docker-compose.yml on the machine that runs the notifier, then restart it.")


if __name__ == "__main__":
    main()
