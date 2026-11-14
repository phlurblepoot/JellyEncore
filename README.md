# JellyEncore

Encore is a small .NET 10 service that runs alongside a Jellyfin server. It is an ASP.NET Core minimal host with a background worker; the worker does the periodic job and the host exposes `GET /health` for monitoring.

This is the initial scaffold: the host starts, the worker runs idle, and `/health` returns 200.

## Layout

- `src/Encore` - the host and background worker
- `tests/Encore.Tests` - xUnit tests

## Build, test, run

    dotnet build
    dotnet test
    dotnet run --project src/Encore

## Environment variables

Standard ASP.NET Core variables are supported. Encore-specific settings will be added here as they are introduced.

| Variable | Purpose | Default |
|---|---|---|
| `ASPNETCORE_URLS` | Address(es) the host listens on | `http://localhost:5000` |
| `ASPNETCORE_ENVIRONMENT` | Environment name (`Development`, `Production`) | `Production` |
| `Logging__LogLevel__Default` | Minimum log level | `Information` |

### Encore settings

Invalid settings are all reported at once (one JSON log line per bad variable) and the process exits with code 2. The API key is never logged.

| Variable | Purpose | Default |
|---|---|---|
| `JELLYFIN_URL` | Jellyfin base URL (http/https), required | - |
| `JELLYFIN_API_KEY` | Jellyfin API key, required, no whitespace | - |
| `ENCORE_ALLOWED_USERS` | Comma-separated Jellyfin user names, at least one | - |
| `ENCORE_LOOKAHEAD` | Songs planned ahead (1-20) | `5` |
| `ENCORE_BATCH` | Songs sent per trigger (1 to lookahead) | `2` |
| `ENCORE_POLL_SECONDS` | Fallback poll interval while the socket is down (5-300) | `10` |
| `ENCORE_RECENT_WINDOW` | `session` or a duration `30m`, `6h`, `2d` (1m-30d) | `session` |
| `ENCORE_DRY_RUN` | `true/false/1/0/yes/no`; default on so a fresh deploy never queues songs | `true` |
| `ENCORE_DATA_DIR` | Writable folder for state and `device-id` | `/data` |
| `LOG_LEVEL` | Trace, Debug, Information, Warning, Error (Encore category) | `Information` |
