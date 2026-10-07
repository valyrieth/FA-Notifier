# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props FaNotify.slnx ./
COPY src/FaNotify/FaNotify.csproj src/FaNotify/
RUN dotnet restore
COPY src/ src/
ARG VERSION=dev
RUN dotnet publish src/FaNotify -c Release --no-restore -p:UseAppHost=false -p:InformationalVersion=${VERSION} -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
WORKDIR /app
RUN apk add --no-cache su-exec
COPY --from=build /out .
COPY <<'EOF' /usr/local/bin/docker-entrypoint.sh
#!/bin/sh
set -eu

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

case "$PUID" in
	''|*[!0-9]*) echo "PUID must be a non-zero numeric user ID." >&2; exit 1 ;;
esac
case "$PGID" in
	''|*[!0-9]*) echo "PGID must be a non-zero numeric group ID." >&2; exit 1 ;;
esac
if [ "$PUID" -eq 0 ] || [ "$PGID" -eq 0 ]; then
	echo "PUID and PGID must be non-zero to run rootless." >&2
	exit 1
fi

chown "$PUID:$PGID" /data /logs
exec su-exec "$PUID:$PGID" dotnet /app/FaNotify.dll "$@"
EOF
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
HEALTHCHECK --interval=60s --timeout=5s --start-period=3m --retries=2 \
  CMD ["sh", "-c", "[ \"$(cat /data/healthy-until 2>/dev/null || echo 0)\" -gt \"$(date +%s)\" ]"]
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]