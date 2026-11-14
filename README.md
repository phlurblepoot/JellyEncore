# JellyEncore

Encore is a small .NET 10 service that runs alongside a Jellyfin 12.4 server. When a listed user's music queue is down to one song after the current one, Encore adds more songs to the queue, so the music keeps going. In v0.1 it only uses songs already in the library.

## What it does

When an allow-listed user's queue is down to one song after the current one (the current song is the second-to-last), Encore adds `ENCORE_BATCH` (2) owned songs to the end of that session's queue with PlayLast. When only the last song Encore added is still to come, it adds the next batch (a top-up), so a song is always left to play and the radio keeps going for up to about 60 songs per run. If the app's next queue report does not show the songs, Encore sends them once more (only while something is playing, and never just because a request got no answer). If the second-to-last song was missed (a restart, a skip straight to the last song), the last song still starts the radio. Songs come from similar artists, similar songs and an instant mix, and never repeat within the recent window.

Works with Jellyfin Web and Finamp. Other apps are skipped (reason `unknown_client`). Nothing is added while a session has repeat on (`repeat_on`) or does not allow remote control (`no_remote_control`).

## Requirements

- Jellyfin 12.4. The queue rules depend on how 12.4 stores the queue.
- A Jellyfin API key made by an admin (Dashboard, API Keys). Encore needs it to see all sessions and to send PlayLast. Enter it yourself in the environment file; never put it on a command line or in the repository.
- The playing app must allow remote control, and repeat must be off.

## Layout

- `src/Encore` - the host: config, Jellyfin client, session watcher, trigger, picker, orchestrator, store, health
- `tests/Encore.Tests` - xUnit tests. `EndToEnd/` runs the orchestrator against a fake Jellyfin and a real SQLite file.

## Build, test, run

    dotnet build
    dotnet test
    dotnet run --project src/Encore

## Environment variables

Standard ASP.NET Core variables are supported.

| Variable | Purpose | Default |
|---|---|---|
| `ASPNETCORE_URLS` | Address(es) the host listens on | `http://localhost:5000` |
| `ASPNETCORE_HTTP_PORTS` | Port(s) the host listens on (the image uses 8080) | - |
| `ASPNETCORE_ENVIRONMENT` | Environment name (`Development`, `Production`) | `Production` |
| `Logging__LogLevel__Default` | Minimum log level | `Information` |

### Encore settings

Invalid settings are all reported at once (one JSON log line per bad variable) and the process exits with code 2. A database that is broken or newer than this build exits with code 3. The API key is never logged.

| Variable | Purpose | Default |
|---|---|---|
| `JELLYFIN_URL` | Jellyfin base URL (http/https), required | - |
| `JELLYFIN_API_KEY` | Jellyfin API key, required, no whitespace | - |
| `ENCORE_ALLOWED_USERS` | Comma-separated Jellyfin user names or user ids, at least one. Only these users get radio | - |
| `ENCORE_LOOKAHEAD` | Songs planned ahead (1-20) | `5` |
| `ENCORE_BATCH` | Songs sent per trigger (1 to lookahead) | `2` |
| `ENCORE_POLL_SECONDS` | Fallback poll interval while the socket is down (5-300) | `10` |
| `ENCORE_RECENT_WINDOW` | `session` or a duration `30m`, `6h`, `2d` (1m-30d). Songs played or given in this window are not picked again | `session` |
| `ENCORE_DRY_RUN` | `true/false/1/0/yes/no`. **Defaults to on**: a fresh install only logs what it would add and sends nothing | `true` |
| `ENCORE_DATA_DIR` | Writable folder for `encore.db` and `device-id` | `/data` |
| `LOG_LEVEL` | Trace, Debug, Information, Warning, Error (Encore category) | `Information` |

Fixed in v0.1, not configurable: a 60-minute dedupe window, a 20-second cooldown per session, at most 30 songs per session and 120 in total per hour (re-sent songs count), at most 60 songs per radio run, two fire attempts 15 seconds apart, one re-send of songs the next queue report does not show, a 3-second delivery lag (plus the poll interval for poll reports) before a report can prove songs missing, a 10-second grace after a stop, a 10-second timeout per Jellyfin call, song lengths of 60 seconds to 12 minutes, and at most 8 Jellyfin calls per pick.

## Running in Docker

- Image: `ghcr.io/phlurblepoot/jellyencore:<tag>`. Pin a version tag, not `latest`.
- Listens on port 8080. Mount a host folder at `/data`; it holds `encore.db` and `device-id`.
- Runs as a non-root user (for example `user: "99:100"`). `/data` must be writable by that user.
- Join the same Docker network as Jellyfin and use Jellyfin's container name in `JELLYFIN_URL`.
- Keep the API key in an `.env` file next to the compose file and reference it from the compose file. Do not write it inline.

## Jellyfin Web fix (Jellyfin 12.4)

Radio needs a live connection (a websocket) between Jellyfin and the player. Jellyfin 12.4 turned off legacy authentication, but the bundled Jellyfin Web client still opens `/socket?api_key=...`, which the server now rejects ("Token is required"). A web session without that socket cannot be remote-controlled, so Encore's PlayLast is not delivered and the web player never shows radio songs. Finamp is not affected.

`contrib/jellyfin-web-socket-fix.js` fixes this in the browser. It renames that one query parameter to `ApiKey`, which 12.4 accepts, and only for `/socket` URLs. It is safe to run twice, keeps 12.4's stricter authentication, and can be removed once Jellyfin Web sends `ApiKey` itself.

Install it with the JavaScript Injector plugin (the plugin that adds custom JavaScript to Jellyfin Web):

1. Install the JavaScript Injector plugin in the Jellyfin dashboard and restart Jellyfin.
2. Open the plugin's settings and add a new script. Name it for example "Encore socket fix" and paste the whole of `contrib/jellyfin-web-socket-fix.js`. Enable it and save.
3. Reload Jellyfin Web (a hard refresh, Ctrl+F5) on every device that should get radio.
4. Check in the browser's developer tools (Network, WS) that the `/socket?ApiKey=...` connection stays open, or look for `queue.confirmed` in Encore's logs after a radio fire.

## Going live

1. Start with `ENCORE_DRY_RUN=true` (the default).
2. Play an album as an allow-listed user, through to its last song.
3. Look for `queue.would_add` in the logs: one line per album end, at the second-to-last song (the last song logs `already_queued` at Debug). The line lists the songs Encore would have added. Check that they are sensible.
4. Set `ENCORE_DRY_RUN=false` and restart. Encore only queues songs from then on.

## Health

`GET /health` returns JSON. It shows the same kind of data as the startup summary, and it never shows the key.

- `status`: `starting` for the first 30 seconds; `ok` when the socket is subscribed (or polling works), every allow-list name resolved and the store is open; `degraded` otherwise; `stopped` (HTTP 503) when the main loop has not run for 60 seconds.
- `dryRun`: whether the service only logs.
- `socket`: `state` (`connecting`, `subscribed`, `down`), when it changed, `reconnects`, and the last message and Sessions times. An idle server sends nothing, so an old `lastSessionsUtc` alone is not a fault.
- `poll`: whether the fallback poll is running and when it last worked.
- `allowList`: the configured names, the resolved name-to-id map, names that did not resolve, and the last error.
- `triggers` and `playLast`: counters since start (fires, sent, dry runs, no picks, failures, songs sent, songs never seen in the queue).
- `store`: whether the database is working and its schema version.

The image's HEALTHCHECK runs `--healthcheck`, which calls `/health` and fails on anything but a 2xx.

## Logs

JSON lines on stdout. Set `LOG_LEVEL` to see more. The events an operator looks for:

| Event | Level | Meaning |
|---|---|---|
| `startup.config` | Info | The settings in use, without the key |
| `startup.config_invalid` | Critical | A setting is wrong (exit 2) |
| `store.ready` | Info | The database is open, with the number of rows pruned at startup |
| `store.error` | Error | A database operation failed |
| `allowlist.resolved` | Info | Which names matched which user ids |
| `allowlist.unresolved` | Warning | A name in `ENCORE_ALLOWED_USERS` matches no user |
| `allowlist.resolve_failed` | Warning | Jellyfin could not list users; retried with backoff |
| `jellyfin.auth_failed` | Error | Jellyfin rejected the API key (401 or 403) |
| `socket.closed`, `socket.reconnect_scheduled` | Warning, Info | The session socket dropped and is coming back |
| `trigger.fired` | Info | A radio fire started (`Kind` is `start`, `topup` or `resend`; `Rule` is `near_end`, `last_song` or `resend`) |
| `trigger.skipped` | Info | Radio did not start for a reason (see below). `not_last`, `not_allowed` and `already_queued` are logged at Debug as `trigger.skipped_quiet` |
| `trigger.gave_up` | Warning | Two attempts failed; no more tries for this song |
| `queue.would_add` | Info | Dry run: the songs that would have been queued |
| `queue.added` | Info | Songs sent with PlayLast |
| `queue.add_failed` | Error | PlayLast failed; the reason and status are logged |
| `queue.unconfirmed` | Warning | Songs were accepted but were not in the queue the app reported at the next song or stop. `Resend` says what happens next: `pending` (sent again now), `armed` (sent again if a song starts within 10 s), `skipped_not_playing`, `skipped_user_songs`, `already_resent` |
| `queue.unverified` | Info | The session ended before a queue report could show the songs. Delivery is unknown, not failed; nothing is re-sent |
| `queue.resent` | Info | The missing songs were sent once more, appended to the end of the queue |
| `queue.add_no_reply` | Warning | PlayLast got no answer; not retried, the next queue report decides |
| `picks.planned`, `picks.none` | Info, Warning | Which songs were picked, or that none were found |
| `worker.crashed` | Critical | A step failed; Encore keeps running |

`trigger.skipped` reasons: `not_allowed`, `not_audio` (a video or episode), `no_remote_control`, `repeat_on`, `unknown_client`, `stale_queue`, `no_history`, `not_last` (two or more songs still to come), `duplicate`, `rate_limited`, `run_cap`, `already_queued` (songs sent at the previous song have not shown up yet; the delivery check handles them).

## Troubleshooting

- **Nothing happens.** Read the `trigger.skipped` reasons. `repeat_on` means repeat is on for that session. `no_remote_control` means the app does not allow remote control. `unknown_client` means the app is not Jellyfin Web or Finamp. `stale_queue` and `no_history` are normal for the first song after a restart or a song started from idle.
- **`jellyfin.auth_failed`, or `/health` shows `unauthorized`.** The key was revoked or mistyped. Create a new key in Jellyfin and update the `.env` file.
- **`allowlist.unresolved`.** A user name is mistyped, or the user does not exist. Use the user's exact name, or the user's id so that a rename does not break it. Each unresolved name is checked again every 5 minutes.
- **Songs never show up.** `queue.unconfirmed` means Jellyfin accepted the command but the songs were not in the queue the app reported next; it is normally followed by `queue.resent`. Two `queue.unconfirmed` in a row for one queue end (the second with `already_resent`) mean the app is not taking commands, and Encore stops for that queue end. Check the app's own queue screen.
- **Exit code 3.** The database is broken or was written by a newer build. Restore the previous image, or back up and remove `encore.db`.

## Data and reset

`encore.db` in the data folder (`/data` in Docker) holds the trigger log, the songs Encore added and recent plays. Old rows are removed automatically: plays after 14 days, triggers and added songs after 90 days. Deleting the file is safe: Encore then forgets its recent picks and its duplicate-check keys. Deleting `device-id` gives Encore a new device entry in Jellyfin.

## Upgrading and rollback

Pin a version tag. The database schema version is stored in the file itself (`PRAGMA user_version`; v0.1 uses schema 1 only). To roll back, run the previous tag. A database from a newer build makes the older build exit with code 3, so back up `encore.db` before an upgrade.
