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
