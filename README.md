# FurAffinity Notify - FurAffinity Notification Tool

A .NET 10 worker that checks Fur Affinity notifications and posts them to Discord.

## Run with Docker

1. Export your FurAffinity login cookies in Netscape `cookies.txt` format and put the file beside `docker-compose.yml`.
2. Copy `settings.example.json` to `settings.json` and set `discordWebhookUrl` to your Discord channel's webhook URL.
3. Start the service:

   ```sh
   cp settings.example.json settings.json
   # Edit settings.json and set discordWebhookUrl
   docker compose up -d --build
   ```

View logs with `docker compose logs -f` or read the newest `./logs/fa-notify-*.log` file on the host. A uniquely named log file is created in the persisted `./logs` folder on every boot. Set `logLevel` to `Debug` to include page responses, parsed counts, deduplication decisions, fallback reasons, Discord batches, and state saves. Each notification is logged as delivered only after Discord accepts its batch. Stop it with `docker compose down`; notification history is kept in `./notification-state/notifications.json` between restarts.

The notifier runs as a non-root user with `PUID=1000` and `PGID=1000` by default. Set these environment variables to match your host user, for example `PUID=$(id -u) PGID=$(id -g) docker compose up -d --build`. The entrypoint assigns the writable data and log directories to these IDs before dropping privileges.

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

The first detail-aware check sends current unread items and stores their stable IDs in `./notification-state/notifications.json`. Later checks skip items already delivered. Here, “seen” means successfully sent by this notifier; it is not a Discord read receipt or a mark-as-read action on Fur Affinity. If a category's row markup is not recognized, the notifier falls back to a count-only alert. Failed checks and failed Discord sends do not replace the saved state.

If Fur Affinity redirects to login, refresh `cookies.txt` and restart the container.

## Security

Treat `cookies.txt`, `settings.json`, and the Discord webhook URL as credentials. If the webhook URL has been exposed, revoke it in Discord and create a new one.
