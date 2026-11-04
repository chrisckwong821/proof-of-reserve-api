# Operations guide

For people running the server: where its data comes from, how it stays fresh, how to tell it's healthy, and how settings work. For a first overview, read the [README](../README.md).

## Redis data

In production, a separate service publishes user data to Redis; this server only reads it. The Redis in `compose.yaml` and the `tools/ReserveData` seeding tool are for local runs and tests only.

The data uses two kinds of key: an immutable, versioned **user hash**, and a **manifest** that points at the current one. `version-A` below stands for a generated version ID.

```text
Key: por:users:version-A    Type: hash
User ID → Balance (JPY)
"1"     → "1111"
"2"     → "2222"
…
"8"     → "8888"

Key: por:current            Type: string containing JSON
{"version":"version-A","key":"por:users:version-A","count":8}
```

To publish new data, write the complete new hash first, then switch the manifest:

```text
Update: write complete version-B → point por:current to version-B
        keep version-A unchanged while existing readers finish
```

### What the server expects from the publishing service

- `por:current` (`Reserve:RedisManifestKey`) holds JSON `{"version", "key", "count"}`; `count` is the number of users in the hash.
- `key` starts with `Reserve:RedisUserKeyPrefix` (`por:users:`); any other key is rejected.
- Hash fields and values are plain non-negative decimal integers: user ID → JPY balance.
- A hash is never modified after the manifest points at it; publish a new version instead. A change during a load fails that load.
- Keep the previous hash for a while after switching the manifest, so a load already in progress can finish.

## Keeping data fresh

```text
Versioned Redis hash
              ↓ startup / daily at 00:10 UTC
Validate → sort IDs → serialize tuples → build immutable Merkle tree
              ↓ one atomic reference swap
        current snapshot
          ├── cached root JSON
          └── bounded proof JSON cache: (snapshot ID, user ID)
```

### Startup

- The first snapshot loads before the server accepts requests.
- If that load fails (for example, Redis is unavailable), the server does not start. This first load is not retried.
- Settings are validated first; any invalid or missing `Reserve` setting stops the server with an `OptionsValidationException` that lists every problem.

### Daily refresh

- The server rebuilds the snapshot every day at `Reserve:RefreshTimeUtc` (shipped as `00:10:00`). It's a UTC clock time, not an interval.
- The new snapshot replaces the old one in a single step. Each request uses one complete snapshot, so a refresh can never mix old balances with a new root.
- When a snapshot is replaced, the proofs cached for the old one are cleared.

### Retries after a failed refresh

- A failed scheduled refresh keeps the last valid snapshot serving and logs the error.
- It retries after `Reserve:RefreshRetryDelay` (`00:01:00`). The wait doubles each time, up to `Reserve:RefreshRetryMaxDelay` (`00:15:00`): 1, 2, 4, 8, then every 15 minutes.
- Retries stop when one succeeds, or when the next scheduled refresh would come first.
- Set `RefreshRetryDelay` to `00:00:00` to turn retries off.

### Mid-day updates (optional)

- By default, data published during the day is picked up at the next daily refresh, or on restart.
- Set `Reserve:ManifestPollInterval` (for example `00:01:00`) to check the manifest's version at that interval. When it changes, the server refreshes straight away.
- Each check is one small Redis `GET`. Users are reloaded only when the version is new, and the current snapshot keeps serving while the new one is built.
- A failed check logs a warning, and the next interval tries again. `00:00:00` (the default) turns polling off.
- With polling on, the root and `snapshotId` can change during the day, so clients that pin `?snapshotId=` get `409` more often and must fetch the root again.

### HTTP caching

- Both API endpoints send an `ETag` and `Cache-Control: private, no-cache`.
- A request with a matching `If-None-Match` gets `304 Not Modified` with no body.

## Health check

`GET /health` returns `200` with `"status": "Healthy"` while the snapshot is current, and `503` with `"Unhealthy"` once it's stale.

```json
{"status":"Healthy","checks":{"snapshot":{"status":"Healthy","description":"Snapshot is current.",
  "data":{"snapshotId":"2742e1…","dataVersion":"bdef87…","userCount":8,
          "generatedAtUtc":"2026-11-04T10:43:41.98+00:00","nextRefreshUtc":"2026-11-05T00:10:00+00:00"}}}}
```

- **Stale** means the snapshot is older than the most recent scheduled refresh, *and* `Reserve:StaleGracePeriod` (`00:30:00`) has passed since that refresh was due.
- With the default settings, the retries at 1, 2, 4, 8 and 15 minutes all fit inside the grace period, so `/health` turns unhealthy only if every retry fails.
- Use it for alerts or a load balancer's readiness check. Don't use it as a liveness probe that restarts the server: if Redis is down, a restarted server can't load data and won't start.

## Logs

At the default level (Information):

| When | Line |
| --- | --- |
| Startup and each successful refresh | `Published reserve snapshot {id}: {n} users, data version {v}, root {hash}, loaded in {ms} ms` |
| After each load | `Next reserve refresh scheduled at {time}` |
| Failed refresh attempt (Error) | `Refresh failed; keeping snapshot {id} (attempt {n}). Retrying in {delay}`, or `… No more retries before the next scheduled refresh at {time}`, with the exception |
| Refresh recovered after retrying | `Refresh succeeded on attempt {n}` |
| Polling found a new version | `Manifest version changed from {old} to {new}; refreshing` |
| Polling check failed (Warning) | `Manifest poll failed; keeping snapshot {id}. Next poll in {interval}`, with the exception |

Log categories are the class names in the `Reserve.Api` namespace, such as `Reserve.Api.ReserveCache` or `Reserve.Api.RefreshScheduler`, so you can set a level per class under `Logging:LogLevel`. Moving a class to another folder doesn't change its category.

Failure lines start with `Refresh failed; keeping snapshot`, the same text used before retries existed, so existing log alerts keep matching.

### Per-request lines (Debug, off by default)

Set the `Reserve.Api.Requests` log level to `Debug` in `appsettings.json`, or pass `--Logging:LogLevel:Reserve.Api.Requests=Debug`:

```text
GET root: 200, snapshot 2742e1…
GET proof for user 3: 200, proof newly built, snapshot 2742e1…
GET proof for user 3: 200, proof from cache, snapshot 2742e1…
GET proof for user 42: 404, user not found
```

- "From cache" means the proof JSON was reused; "newly built" means it was built from the snapshot on that request.
- They're off by default because at thousands of requests per second they flood the logs and cost throughput. Turn them on briefly for debugging.

## Configuration in depth

`appsettings.json` and `Configuration/ReserveOptions.cs` play different roles; they are not two copies of the settings.

| | `appsettings.json` | `ReserveOptions.cs` |
| --- | --- | --- |
| Holds | The **values** this deployment uses | The **shape**: each setting's name and type, the validation rules, and defaults for optional settings |
| Read by | People configuring the server | The code, e.g. `options.RefreshTime` rather than a string lookup |

At startup, ASP.NET Core combines these sources, each overriding the one before, and copies the result into one `ReserveOptions`:

```text
appsettings.json  →  appsettings.{Environment}.json  →  environment variables  →  command-line arguments
                                         (Reserve__RefreshTimeUtc=01:00:00)   (--Reserve:RefreshTimeUtc 01:00:00)
```

- **Required settings** have no default in `ReserveOptions`; leaving one out stops startup.
- **Optional settings** have a default in `ReserveOptions` that applies only when no source sets the key.
- The shipped `appsettings.json` also lists the optional settings, with the same values as the defaults, so every setting is visible in one file. If you change a default in `ReserveOptions`, change the shipped value too: the file's value wins whenever the key is present.
- Unknown keys are ignored, so a leftover setting from an older version (such as `Reserve:HeartbeatInterval`) doesn't stop startup.
