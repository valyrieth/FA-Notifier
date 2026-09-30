# FurAffinity Notify - FurAffinity Notification Tool

A .NET 10 worker that checks Fur Affinity notifications and posts them to Discord.

## Setup

### 1. Export Fur Affinity cookies

The notifier uses your authenticated Fur Affinity browser session. It does not need your password, but it does need a Netscape-format `cookies.txt` export.

1. Sign in to Fur Affinity in a browser. A separate browser profile dedicated to the notifier keeps this session isolated from your other browsing.
2. Use a reputable cookie-export extension from your browser's official extension store. Export cookies for `furaffinity.net` in Netscape `cookies.txt` format.
3. Save the exported file as `cookies.txt` in the repository root, beside `docker-compose.yml`.

Treat this file like a password: do not share it, commit it, or include it in support logs. Cookie exporters can access session credentials, so remove the extension when you no longer need it. If the notifier reports a failed check, sign in again and replace the export; session cookies can expire.

### 2. Create a Discord webhook

1. In your Discord server, create or choose a channel for Fur Affinity notifications. You need permission to manage that channel's webhooks.
2. Open the channel's **Edit Channel** settings, then **Integrations** > **Webhooks**. Create a webhook, choose the destination channel, and copy its webhook URL.
3. Keep the URL private. Anyone who has it can post messages to that channel.

This project posts through a channel webhook; it does not require creating a Discord bot application, inviting a bot user, or generating a bot token.

### 3. Configure and start the notifier

From the repository root:

```sh
cp settings.example.json settings.json
mkdir -p fa-notifier/data fa-notifier/logs
```

Edit `settings.json` and replace `discordWebhookUrl` with the webhook URL. `logLevel` defaults to `Information`; use `Debug` for detailed request, parsing, deduplication, and state-save diagnostics. Leave `useFlareSolverr` set to `false` for the standard setup.

Set the container's runtime user to match your host user, then start the service:

```sh
PUID="$(id -u)" PGID="$(id -g)" docker compose up -d --build
```

The notifier runs as that non-root UID/GID. Creating the data and log folders before starting keeps their parent directory owned by your host user. Check startup with `docker compose logs -f fa-notify`.

The Compose bind mounts keep state in `./fa-notifier/data/notifications.json` and create a unique log file under `./fa-notifier/logs/` on every boot. Each notification is logged as delivered only after Discord accepts its batch. Stop the service with `docker compose down`; the notification history and log files remain on the host.

## Settings

Edit `settings.json`:

| Setting | Default | Description |
| --- | --- | --- |
| `discordWebhookUrl` | Required | Discord channel webhook URL. Keep it private. |
| `pollIntervalMinutes` | `30` | Normal check interval. Below 15,000 registered users online, checks switch to once per minute until the count reaches 15,000. |
| `logLevel` | `Information` | Minimum log level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`. |
| `notifyOn` | All types | Notification types: `submissions`, `watches`, `comments`, `favorites`, `journals`, `notes`. |
| `notificationPrefix` | Empty | Optional text prepended to Discord messages. |
| `userAgent` | Firefox UA | User-Agent sent to FurAffinity. |
| `useFlareSolverr` | `false` | Use the optional browser-based FlareSolverr service to fetch FA pages. |

## Optional Cloudflare Solver

FlareSolverr runs a separate Chromium-based browser and uses more memory than the notifier. To enable it, set `useFlareSolverr` to `true` in `settings.json`, then start the solver profile:

```sh
docker compose --profile solver up -d --build
```

The solver API is only available on the private Compose network; do not publish its port. FA cookies are sent to that local browser service. FlareSolverr can handle some browser challenges, but it may still fail if FA blocks the server's IP or requires a CAPTCHA.

## Discord Notifications

New items are sent as individual Discord embeds with their category, title, link, and available author/profile or submission artwork details. When the page provides an image or user icon, it is included in the embed. Large batches are split to respect Discord's embed limits.

At startup, the notifier logs the current Fur Affinity registered-user count. If it is below 15,000, it logs that checks are switching to once per minute. When the count reaches 15,000, it logs that normal polling has resumed. These status messages are written to the log file and container output, not sent to Discord.

If a check fails, the notifier sends one Discord alert noting that the session cookies may have expired. It avoids repeating the alert for every failed poll and sends a recovery alert after the next successful check. Detailed failure information remains in the application log.

The first detail-aware check sends current unread items and stores their stable IDs in `./fa-notifier/data/notifications.json`. Later checks skip items already delivered. Here, “seen” means successfully sent by this notifier; it is not a Discord read receipt or a mark-as-read action on Fur Affinity. If a category's row markup is not recognized, the notifier falls back to a count-only alert. Failed checks and failed Discord sends do not replace the saved state.

If Fur Affinity redirects to login, refresh `cookies.txt` and restart the container.

## Security

Treat `cookies.txt`, `settings.json`, and the Discord webhook URL as credentials. If the webhook URL has been exposed, revoke it in Discord and create a new one.
