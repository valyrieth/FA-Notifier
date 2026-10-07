# FurAffinity Notify

Get your Fur Affinity notifications in Discord. The notifier signs in with your browser session, checks your notification pages on a schedule, and posts each new item to a Discord channel as its own message.

It reports new **submissions, watches, comments, favorites, journals and notes**. You choose which types you want, and every item is sent only once.

## What you need

- A machine that is always on, with [Docker](https://docs.docker.com/get-docker/) and Docker Compose.
- A Fur Affinity account, signed in on a browser (used once to export your login).
- A Discord server where you can create a webhook.

## Quick start

1. **Export your login** to `cookies.txt` ([step 1](#1-export-fur-affinity-cookies)).
2. **Create a Discord webhook** and copy its URL ([step 2](#2-create-a-discord-webhook)).
3. **Configure and start** ([step 3](#3-configure-and-start-the-notifier)). Using the published image, no clone needed:

   ```sh
   mkdir fa-notify && cd fa-notify
   curl -fsSLO https://raw.githubusercontent.com/valyrieth/FA-Notifier/main/docker-compose.yml
   curl -fsSL https://raw.githubusercontent.com/valyrieth/FA-Notifier/main/settings.example.json -o settings.json
   # copy cookies.txt into this folder and paste your webhook URL into settings.json
   mkdir -p fa-notifier/data fa-notifier/logs
   PUID="$(id -u)" PGID="$(id -g)" docker compose up -d
   docker compose logs -f fa-notify           # the first line shows the version
   ```

   Make sure `cookies.txt` and `settings.json` exist before starting; otherwise Docker creates empty folders with those names.

   Or build from a clone of this repository (`docker-compose.override.yml` makes Compose build locally):

   ```sh
   cp settings.example.json settings.json     # then paste your webhook URL into it
   mkdir -p fa-notifier/data fa-notifier/logs
   PUID="$(id -u)" PGID="$(id -g)" docker compose up -d --build
   ```

The first check sends every notification you currently have unread, so expect a burst of messages on first start. To update the published image, run `docker compose pull && docker compose up -d`; from a clone, run `git pull` and `docker compose up -d --build`.

## Setup

### 1. Export Fur Affinity cookies

The notifier uses your authenticated Fur Affinity browser session. It does not need your password, but it does need a Netscape-format `cookies.txt` export.

Sign in to Fur Affinity in a browser first. A separate browser profile dedicated to the notifier keeps this session isolated from your other browsing. Then pick one method.

**Script (recommended).** [`scripts/export-cookies.py`](scripts/export-cookies.py) reads the login from your browser and writes a `cookies.txt` that contains only `furaffinity.net` cookies, readable by you alone. Run it on the computer where you are signed in:

```sh
uv run scripts/export-cookies.py firefox      # or: chrome, edge, brave, chromium, vivaldi, opera, librewolf, safari
uv run scripts/export-cookies.py              # tries every supported browser
```

Without `uv`, install the one dependency (`browser-cookie3`, listed in [`scripts/requirements.txt`](scripts/requirements.txt)) into a virtual environment, since many systems refuse system-wide `pip install`:

```sh
python3 -m venv .venv
.venv/bin/pip install -r scripts/requirements.txt
.venv/bin/python scripts/export-cookies.py firefox      # Windows: .venv\Scripts\pip and .venv\Scripts\python
```

`pipx run scripts/export-cookies.py` also works. If the notifier runs on another machine, copy the resulting file there, for example `scp cookies.txt server:path/to/furaffinity-notify/`. Firefox is the most reliable; recent Chrome and Edge builds on Windows encrypt cookies in a way other programs cannot read, in which case use Firefox or the manual method. The script prints when the login cookies expire.

**Manual.** Use a reputable cookie-export extension from your browser's official extension store, export cookies for `furaffinity.net` in Netscape `cookies.txt` format, and save the file as `cookies.txt` in the repository root, beside `docker-compose.yml`. Remove the extension when you no longer need it, since cookie exporters can access session credentials.

Treat `cookies.txt` like a password: do not share it, commit it, or include it in support logs. If the notifier reports a failed check, sign in again and replace the file; session cookies can expire.

### 2. Create a Discord webhook

1. In your Discord server, create or choose a channel for Fur Affinity notifications. You need permission to manage that channel's webhooks.
2. Open the channel's **Edit Channel** settings, then **Integrations** > **Webhooks**. Create a webhook, choose the destination channel, and copy its webhook URL.
3. Keep the URL private. Anyone who has it can post messages to that channel.

This project posts through a channel webhook; it does not require creating a Discord bot application, inviting a bot user, or generating a bot token.

### 3. Configure and start the notifier

From the folder that holds `docker-compose.yml` (a clone of this repository, or the folder from the quick start):

```sh
cp settings.example.json settings.json     # skip if you downloaded settings.json already
mkdir -p fa-notifier/data fa-notifier/logs
```

Edit `settings.json` and replace `discordWebhookUrl` with the webhook URL. `logLevel` defaults to `Information`; use `Debug` for detailed request, parsing, deduplication, and state-save diagnostics. Leave `useFlareSolverr` set to `false` for the standard setup.

Set the container's runtime user to match your host user, then start the service:

```sh
PUID="$(id -u)" PGID="$(id -g)" docker compose up -d          # published image
PUID="$(id -u)" PGID="$(id -g)" docker compose up -d --build  # from a clone: build locally
```

The notifier runs as that non-root UID/GID. Creating the data and log folders before starting keeps their parent directory owned by your host user. Check startup with `docker compose logs -f fa-notify`.

The Compose bind mounts keep state in `./fa-notifier/data/notifications.json` and create a unique log file under `./fa-notifier/logs/` on every boot. Each notification is logged as delivered only after Discord accepts its batch, and the saved state is updated after every accepted batch, so a failure part-way through a large set does not cause already-delivered items to be sent again. Stop the service with `docker compose down`; the notification history and log files remain on the host.

## Settings

Edit `settings.json`; `//` comments and trailing commas are allowed:

| Setting | Default | Description |
| --- | --- | --- |
| `discordWebhookUrl` | Required | Discord channel webhook URL. Keep it private. |
| `pollIntervalMinutes` | `30` | Normal check interval, plus up to 10% (at most 30 seconds) of random delay so requests are not perfectly periodic. Below 15,000 registered users online, checks switch to once per minute until the count reaches 15,000. |
| `failureAlertThreshold` | `3` | Number of failed checks in a row before a Discord alert is sent. A login redirect (expired session) alerts immediately. |
| `logLevel` | `Information` | Minimum log level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`. |
| `notifyOn` | All types | Notification types: `submissions`, `watches`, `comments`, `favorites`, `journals`, `notes`. |
| `notificationPrefix` | Empty | Optional text prepended to Discord messages. |
| `userAgent` | Firefox UA | User-Agent sent to FurAffinity. |
| `useFlareSolverr` | `false` | Fall back to the optional browser-based FlareSolverr service when Cloudflare blocks a normal request. |
| `flareSolverrUrl` | `http://flaresolverr:8191/v1` | FlareSolverr API endpoint. `settings.example.json` sets `http://fa-solver:8191/v1` to match the Compose service; change it if you rename the service or run the solver elsewhere. |

## Reliability

Requests to Fur Affinity and Discord are retried up to four attempts in total when the failure looks temporary: timeouts, connection errors, and HTTP 408, 429, or 5xx responses. A `Retry-After` header (for example from Discord's rate limiting) is honoured; otherwise the wait doubles from 2 seconds. Waits are capped at 60 seconds, and each retry is logged as a warning. Other errors, such as a login redirect or a 403 from Cloudflare, are not retried and go through the normal failed-check handling.

In the rare case that Discord processes a post but the response is lost or an error follows, the retry can post that batch twice.

## Health check

After every successful check the notifier writes a "healthy until" timestamp to `./fa-notifier/data/healthy-until`. The container's Docker health check passes while that time is in the future, which allows roughly two missed polls plus five minutes. If checks keep failing (for example because the cookies expired) or the loop is stuck, `docker compose ps` shows the container as `unhealthy`. Docker does not restart unhealthy containers by itself; use it for monitoring or an autoheal tool.

## Optional Cloudflare Solver

FlareSolverr runs a separate Chromium-based browser and uses more memory than the notifier. To enable it, set `useFlareSolverr` to `true` in `settings.json`, then start the solver profile:

```sh
docker compose --profile solver up -d            # published image
docker compose --profile solver up -d --build    # from a clone: build locally
```

The solver API is only available on the private Compose network; do not publish its port. FA cookies are sent to that local browser service. FlareSolverr can handle some browser challenges, but it may still fail if FA blocks the server's IP or requires a CAPTCHA.

The solver is not used for every request. The notifier makes normal requests first and only calls FlareSolverr when Cloudflare answers with a challenge. It then keeps the Cloudflare clearance cookie and the solver browser's User-Agent, saves them in `./fa-notifier/data/cookies-refreshed.txt`, and reuses them, so FlareSolverr runs again only when the clearance expires or stops working. While a clearance is in use, requests send the solver browser's User-Agent instead of `userAgent`.

## Discord Notifications

New items are sent as individual Discord embeds with their category, title, link, and available author/profile or submission artwork details. When the page provides an image or user icon, it is included in the embed. The embed's timestamp is the time Fur Affinity recorded for the event when the page shows one (watches, favorites, notes), otherwise the time the notifier saw it. Submissions show their rating (General, Mature, or Adult); the thumbnail is still included for every rating, so use an age-restricted Discord channel if you watch adult artists. Large batches are split to respect Discord's embed limits.

At startup, the notifier logs the current Fur Affinity registered-user count. If it is below 15,000, it logs that checks are switching to once per minute. When the count reaches 15,000, it logs that normal polling has resumed. These status messages are written to the log file and container output, not sent to Discord.

If checks keep failing, the notifier sends one Discord alert after `failureAlertThreshold` consecutive failures, so a single timeout or temporary 5xx does not page you. If Fur Affinity redirects to login (expired session cookies), the alert is sent immediately. It avoids repeating the alert for every failed poll and sends a recovery alert after the next successful check. Detailed failure information remains in the application log.

The notifier logs when the Fur Affinity login cookies (`a` and `b`) expire: once at startup and again whenever the date changes. Within 24 hours of expiry it logs a warning on every check and sends one Discord alert. Replace `cookies.txt` and restart the container to pick up new cookies.

If Fur Affinity renews the session cookies in a response, the notifier stores the new values in `./fa-notifier/data/cookies-refreshed.txt` and uses that file on later starts, so the session can outlast the original export. Replacing `cookies.txt` with a new export discards the renewed copy automatically. Renewal is only tracked for normal requests, not requests made through FlareSolverr.

### Detecting new items

Every check loads the detail page of each category that has unread items, so a new item is found even when the unread count does not change (for example, one item is read while another arrives). Items already delivered are skipped using IDs stored in `./fa-notifier/data/notifications.json`:

| Type | ID |
| --- | --- |
| Submissions | `submissions:<view id>` |
| Journals | `journals:<journal id>` |
| Comments | `comments:<comment id>` |
| Favorites | `favorites:<view id>:<user>` |
| Watches, notes | `<type>:<short hash of link, user and text>` |

Edits to a title or description therefore do not cause a repeat. The first check sends all current unread items. Here, “seen” means successfully sent by this notifier; it is not a Discord read receipt or a mark-as-read action on Fur Affinity. If a category's row markup is not recognized, the notifier falls back to a count-only alert. Failed checks do not change the saved state, and a failed Discord send keeps only the batches Discord accepted.

If Fur Affinity redirects to login, refresh `cookies.txt` and restart the container.

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| Discord alert “FurAffinity check failed”, or the log mentions a login redirect | The session expired. Export a fresh `cookies.txt`, replace the file and run `docker compose restart fa-notify`. |
| HTTP 403 mentioning Cloudflare | Fur Affinity is challenging the request. Check that `userAgent` matches the browser you exported from, or enable the [optional solver](#optional-cloudflare-solver). |
| Nothing arrives in Discord | Run `docker compose logs fa-notify`. Check the webhook URL, that the type is in `notifyOn`, and that there really are unread notifications. |
| Container shows `unhealthy` | Checks have been failing for several polls; see the log for the reason. |
| Permission errors on `fa-notifier/` | Create the folders before starting and pass `PUID`/`PGID` as shown above. |
| Need more detail | Set `logLevel` to `Debug` in `settings.json` and restart. |

## Security

Treat `cookies.txt`, `settings.json`, and the Discord webhook URL as credentials. If the webhook URL has been exposed, revoke it in Discord and create a new one.
