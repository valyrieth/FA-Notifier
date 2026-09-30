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

View logs with `docker compose logs -f`. Stop it with `docker compose down`; notification history is kept in `./notification-state/notifications.json` between restarts.

## Settings

Edit `settings.json`:

| Setting | Default | Description |
| --- | --- | --- |
| `discordWebhookUrl` | Required | Discord channel webhook URL. Keep it private. |
| `pollIntervalMinutes` | `30` | Check interval. Keep this at 30 minutes or longer to avoid unnecessary requests to FurAffinity. |
| `notifyOn` | All types | Notification types: `submissions`, `watches`, `comments`, `favorites`, `journals`, `notes`. |
| `notificationPrefix` | Empty | Optional text prepended to Discord messages. |
| `userAgent` | Firefox UA | User-Agent sent to FurAffinity. |

If Fur Affinity redirects to login, refresh `cookies.txt` and restart the container. Failed checks and failed Discord sends do not replace the saved notification state.

## Security

Treat `cookies.txt`, `settings.json`, and the Discord webhook URL as credentials. If the webhook URL has been exposed, revoke it in Discord and create a new one.
