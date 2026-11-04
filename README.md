# Proof of Reserve API

This API lets users check that their balance is included in a published Merkle root.

- **Get the root:** `GET /api/reserves/root` returns one hash representing all users’ IDs and balances.
- **Get a user’s proof:** `GET /api/reserves/users/{userId}/proof` returns that user’s balance and the hashes needed to verify its inclusion in the root.

## API contract

Method and path | Result |
| --- | --- |
| `GET /api/reserves/root` | Cached root, user count, snapshot ID, generation time, and source version. |
| `GET /api/reserves/users/{userId}/proof` | User ID, JPY balance, root, snapshot ID, and sibling tuples. |
| `GET /health` | `200` while the snapshot is current, `503` once it is stale; see [Health and logging](#health-and-logging). |

**Proof errors** (JSON body `{"error": "..."}`):

| Status | When | `error` |
| --- | --- | --- |
| `400` | `userId` is not an integer, e.g. `abc`, `1.5`, or larger than 2147483647 | `User ID must be a positive integer.` |
| `400` | `userId` is zero or negative | `User ID must be positive.` |
| `404` | No user has this ID in the current snapshot | `User not found.` |
| `409` | `?snapshotId=` names a snapshot that has been replaced; the body also has the current `snapshotId` | `Snapshot changed; fetch the current root and retry.` |

**Get the root — eight-user example**
```
curl http://localhost:5080/api/reserves/root
```
```json
{
  "snapshotId": "00000000000000000000000000000001",
  "root": "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3",
  "userCount": 8,
  "generatedAtUtc": "2026-11-04T00:10:00+00:00",
  "dataVersion": "00000000000000000000000000000002"
}
```
**Get the proof for user 3**
```
curl http://localhost:5080/api/reserves/users/3/proof
```
```json
{
  "snapshotId": "00000000000000000000000000000001",
  "root": "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3",
  "userId": 3,
  "userBalance": 3333,
  "proof": [
    ["8520072399ad3462db395a7a9803c6fe3f4143d502a0eb145e6c69ba7ec6d22d", 1],
    ["99fb04c9b8fd37e66b2dde367d91f2c930b2ab162dbbf7298e9313c309c7925f", 0],
    ["9d7f79fa8e788d4a32c9c674b67dcfaf0885f539ac2699129e3c4d88c11c76e7", 1]
  ]
}
```

## Repository layout

| Directory | Purpose |
| --- | --- |
| `src/Reserve.Api/` | The running server, grouped into folders by job; see [How the server is organized](#how-the-server-is-organized). |
| `tests/Reserve.Tests/` | NUnit tests for responses, validation, scheduling, caching, and Redis. |
| `tests/Reserve.Tests/Fixtures/` | Saved eight-user and 100k mock datasets, used only for testing and seeding test Redis. |
| `tests/` | Contains the full-system runner and `stress.py` for repeatable HTTP load tests. |
| `tools/ReserveData/` | Generates mock data and seeds Redis; used by the integration script, not by the running server. |
| `docs/` | `WALKTHROUGH.md`: a read-aloud walkthrough of how the server works, for reviewers and new engineers. |
| `vendor/tagged-merkle/` | Merkle library submodule; its source and tests belong to that library. |
| `.github/workflows/` | GitHub Actions configuration for automated builds and tests. |
| `bin/` and `obj/` | Generated build output and intermediate files inside projects; ignored by Git.


## How the server is organized

The server keeps one immutable **snapshot** in memory: every user, the Merkle tree, and the root JSON. Requests only read the current snapshot; background jobs build a replacement and swap it in.

```text
Refresh (background: build a new snapshot, then swap it in)
  RefreshScheduler ─┐
  ManifestPoller   ─┴─▶ ReserveCache.RefreshAsync ─▶ RedisConnector ─▶ Redis
                              └─▶ BuildSnapshot (tagged-merkle) ─▶ publish snapshot

Requests (read only: use the current snapshot)
  GET root / proof ─▶ ReserveEndpoints ─▶ ReserveCache.Current ─▶ JSON or 304
  GET /health      ─▶ SnapshotHealthCheck ─▶ ReserveCache.Current
```

**Serving a request:** `ReserveEndpoints` takes the current snapshot. The root response is pre-built JSON; a proof comes from the proof cache, or is built from the tree and cached. A matching `ETag` returns `304`.

**Refreshing data:** `RefreshScheduler` and `ManifestPoller` decide *when*. `ReserveCache.RefreshAsync` does the work: it loads users through `RedisConnector`, builds the tree with `tagged-merkle`, and publishes the new snapshot in one step. Refreshes run one at a time, and a failure keeps the old snapshot serving.

| Folder (`src/Reserve.Api/`) | Contents |
| --- | --- |
| *(root)* | `Program.cs` (entry point), `ReserveApplication.cs` (wires everything together), `appsettings.json` |
| `Endpoints/` | `ReserveEndpoints`: the root, proof and `/health` routes, ETag handling, per-request Debug logs |
| `Snapshots/` | `ReserveCache`: current snapshot, `RefreshAsync`, `BuildSnapshot`, proof cache. `ProofTags`: Merkle hash tags |
| `Refresh/` | `RefreshScheduler`: startup load, daily refresh, retry with backoff. `ManifestPoller`: optional mid-day version check |
| `Data/` | `IUserDataSource`: where user data comes from (load users, read the current version). `RedisConnector`: the production source; reads the manifest and its immutable user hash |
| `Monitoring/` | `SnapshotHealthCheck`: the `/health` staleness check |
| `Configuration/` | `ReserveOptions` (all `Reserve:*` settings) and its startup validator; see [Settings: appsettings.json and ReserveOptions](#settings-appsettingsjson-and-reserveoptions) |
| `Models/` | `UserAccount`/`UserDataSet` (loaded data) and `RootResponse`/`ProofResponse` (API JSON) |

- Every class stays in the `Reserve.Api` namespace whatever its folder, so code that uses these types and log-level settings keyed by class name (e.g. `Reserve.Api.ReserveCache`) keep working.

### Settings: appsettings.json and ReserveOptions

The two files play different roles; they are not two copies of the settings.

| | `appsettings.json` | `Configuration/ReserveOptions.cs` |
| --- | --- | --- |
| Holds | The **values** this deployment uses | The **shape**: each setting's name and type, the validation rules, and defaults for optional settings |
| Read by | People configuring the server | The code, e.g. `options.RefreshTime` rather than a string lookup |

At startup, ASP.NET Core combines these sources, each overriding the one before, and copies the result into one `ReserveOptions`:

```text
appsettings.json  →  appsettings.{Environment}.json  →  environment variables  →  command-line arguments
                                         (Reserve__RefreshTimeUtc=01:00:00)   (--Reserve:RefreshTimeUtc 01:00:00)
```

The validator then checks the result; an invalid or missing required value stops the server before it serves anything.

- **Required settings** (`RefreshTimeUtc`, `ProofCacheEntries`, `RedisConnection`, `RedisManifestKey`, `RedisUserKeyPrefix`) have no default in `ReserveOptions`; leaving one out stops startup.
- **Optional settings** (`StaleGracePeriod`, `RefreshRetryDelay`, `RefreshRetryMaxDelay`, `ManifestPollInterval`) have a default in `ReserveOptions`, e.g. `StaleGracePeriod = 30 minutes`. The default applies only when no source sets the key.
- The shipped `appsettings.json` lists the optional settings too, with the same values as the defaults, so every setting is visible in one file. If you change a default in `ReserveOptions`, change the shipped value as well: the file's value wins whenever the key is present.

## Run

```sh
git submodule update --init --recursive
dotnet restore --locked-mode
dotnet build -c Release
docker compose up -d --wait redis
dotnet run --project tools/ReserveData -c Release --no-build -- seed-redis tests/Reserve.Tests/Fixtures/users-example.json localhost:6379
dotnet run --project src/Reserve.Api -c Release --no-build -- --urls http://localhost:5080
```

- The first snapshot loads before the server accepts requests; no valid initial data means startup fails.
- Settings are validated at startup; an invalid or missing `Reserve` setting stops the server with an `OptionsValidationException` naming the setting.


## Data fixtures

| File | Contents |
| --- | --- |
| `tests/Reserve.Tests/Fixtures/users-100000.json` | IDs `1..100000`, randomized integer balances `0..999999` JPY, generated with seed `340`. |
| `tests/Reserve.Tests/Fixtures/users-example.json` | `(1,1111)` through `(8,8888)`, exactly as specified. |

- All fixtures are copied only to test output; the API ships no mock datasets. The test-data tool loads fixtures into Redis before the server starts.
- The generator writes fixture files atomically; the publisher validates each fixture before writing a new Redis dataset.

```sh
# Reproduce the large fixture using the .NET 10 generator.
dotnet run --project tools/ReserveData -c Release -- generate tests/Reserve.Tests/Fixtures/users-100000.json
```

Expected roots, independently checked with Python's `hashlib` during implementation:

```text
8-user example:
b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3

Saved 100,000-user fixture:
347cfc213378db8331eef9b7fac2dccf625401a1fd1339b9f345432843221a9f
```

## Redis

```sh
# Start Redis, load the example users, and start the API.
docker compose up -d --wait redis
dotnet run --project tools/ReserveData -c Release -- seed-redis tests/Reserve.Tests/Fixtures/users-example.json localhost:6379
dotnet run --project src/Reserve.Api -c Release -- --urls http://localhost:5080
```

**Example Redis data** (`version-A` represents a generated version ID):

```text
Key: por:users:version-A    Type: hash
User ID → Balance (JPY)
"1"     → "1111"
"2"     → "2222"
"3"     → "3333"
"4"     → "4444"
"5"     → "5555"
"6"     → "6666"
"7"     → "7777"
"8"     → "8888"

Key: por:current            Type: string containing JSON
{"version":"version-A","key":"por:users:version-A","count":8}
```

```text
Update: write complete version-B → point por:current to version-B
        keep version-A unchanged while existing readers finish
```

## Caching and daily refresh

```text
Versioned Redis hash
              ↓ startup / daily at 00:10 UTC
Validate → sort IDs → serialize tuples → build immutable Merkle tree
              ↓ one atomic reference swap
        current snapshot
          ├── cached root JSON
          └── bounded proof JSON cache: (snapshot ID, user ID)
```

- **Root cache:** JSON is created once per snapshot and reused until a successful refresh replaces it.
- **Refresh:** load once at startup, then rebuild daily at `00:10 UTC`. Set `Reserve:RefreshTimeUtc` in `appsettings.json` (or `Reserve__RefreshTimeUtc`) to another UTC clock time; this is a time of day, not an interval.
- **Consistency:** each request captures one complete snapshot; a replacement cannot mix old balances with a new root, and proof cache keys never cross snapshot versions.
- **Failure and retry:** a failed scheduled refresh keeps the last valid snapshot, logs the error, and retries after `Reserve:RefreshRetryDelay` (default `00:01:00`). The wait doubles each time up to `Reserve:RefreshRetryMaxDelay` (default `00:15:00`): 1, 2, 4, 8, then every 15 minutes.
- Retries stop when one succeeds or when the next scheduled refresh would come first. `RefreshRetryDelay` `00:00:00` turns retries off.
- If the first load at startup fails, the server does not start; that load is not retried. Monitor `/health` (see below) for stale data.
- **Mid-day updates (optional):** by default, data published during the day is picked up at the next daily refresh or on restart. Set `Reserve:ManifestPollInterval` (e.g. `00:01:00`) to check the `por:current` manifest version at that interval and refresh as soon as it changes. Each check is one small Redis `GET`; users are reloaded only when the version is new, and the current snapshot keeps serving while the new one is built. A failed check logs a warning and the next interval tries again. `00:00:00` (the default) is off.
- With polling on, the root and `snapshotId` can change during the day, so clients pinning `?snapshotId=` get `409` more often and must fetch the root again.
- When a snapshot is replaced, the proofs cached for the old one are cleared.
- **HTTP caching:** both APIs send `ETag` and `Cache-Control: private, no-cache`; a matching `If-None-Match` returns `304` after revalidation.

```json
"RefreshTimeUtc": "00:10:00"
```
Configuration values live only in `src/Reserve.Api/appsettings.json`;

## Health and logging

**Health check:** `GET /health` returns `200` with `"status": "Healthy"` while the snapshot is current, and `503` with `"Unhealthy"` once it is stale.
```json
{"status":"Healthy","checks":{"snapshot":{"status":"Healthy","description":"Snapshot is current.",
  "data":{"snapshotId":"2742e1…","dataVersion":"bdef87…","userCount":8,
          "generatedAtUtc":"2026-11-04T10:43:41.98+00:00","nextRefreshUtc":"2026-11-05T00:10:00+00:00"}}}}
```
- **Stale** means the snapshot is older than the most recent scheduled refresh and `Reserve:StaleGracePeriod` (default `00:30:00`) has passed since that refresh was due. With the default settings, retries at 1, 2, 4, 8 and 15 minutes all fit inside the grace period, so `/health` turns unhealthy only if they all fail.
- Use it for alerts or a load-balancer readiness check. Avoid it as a liveness probe that restarts the server: if Redis is down, a restarted server cannot load data and will not start.

**Logs at the default level (Information):**

| When | Line |
| --- | --- |
| Startup and each successful refresh | `Published reserve snapshot {id}: {n} users, data version {v}, root {hash}, loaded in {ms} ms` |
| After each load | `Next reserve refresh scheduled at {time}` |
| Failed refresh attempt (Error) | `Refresh failed; keeping snapshot {id} (attempt {n}). Retrying in {delay}` with the exception, or `… No more retries before the next scheduled refresh at {time}`. The line still starts with the old `Refresh failed; keeping snapshot` text, so existing log alerts keep matching. |
| Refresh recovered after retrying | `Refresh succeeded on attempt {n}` |
| Polling found a new version | `Manifest version changed from {old} to {new}; refreshing` |
| Polling check failed (Warning) | `Manifest poll failed; keeping snapshot {id}. Next poll in {interval}` with the exception |

- To check the server is alive and the data is fresh, poll `/health`; it reports the snapshot's age and next refresh time.

**Per-request lines (Debug, off by default):** set the `Reserve.Api.Requests` log level to `Debug` in `appsettings.json`, or pass `--Logging:LogLevel:Reserve.Api.Requests=Debug`.
```text
GET root: 200, snapshot 2742e1…
GET proof for user 3: 200, proof newly built, snapshot 2742e1…
GET proof for user 3: 200, proof from cache, snapshot 2742e1…
GET proof for user 42: 404, user not found
```
- These are off by default because at thousands of requests per second they flood the logs and cost throughput. Use them briefly for debugging. "From cache" means the proof JSON was reused; "newly built" means it was built from the snapshot on that request.

## Tests

Run these commands from the repository root; the paths are relative to it.

```sh
# HTTP/cache tests with an injected in-memory source; no Redis required.
dotnet test tests/Reserve.Tests -c Release

# Include Redis integration using an isolated test instance.
RESERVE_TEST_REDIS=localhost:6379 dotnet test tests/Reserve.Tests -c Release

# Show each test name, or run only some tests.
dotnet test tests/Reserve.Tests --logger "console;verbosity=normal"
dotnet test tests/Reserve.Tests --filter "FullyQualifiedName~ApiTests"
```

- Tests use [NUnit](https://docs.nunit.org/). Each test class starts its own in-process server in `[SetUp]`, so tests are independent and can run alone.
- Shared helpers live in `TestSupport.cs`: fixtures, `TestApp` (a fresh server), `MutableSource` (a controllable data source) and `ProofVerifier` (rebuilds the root without the production Merkle library).
- Redis tests are reported as **skipped** unless `RESERVE_TEST_REDIS` is set.

## End-to-end test

GitHub Actions runs the same script on pushes, pull requests, and manual workflow runs. It runs the integration suite and Docker end-to-end checks; stress testing remains optional locally.

- Requires `.NET 10`, `Python 3`, and a running Docker engine; The script uses only Python's standard library to control processes and independently verify hashes.
- Run from the repository root; the script creates its own Redis container and chooses unused loopback ports. It does not use or modify an existing Redis instance.

```sh
python3 tests/run-integration.py
```

- **Build:** initialize submodules, restore locked packages, build Release, and publish the API into a temporary directory with no mock datasets.
- **Redis:** start `redis:7.4-alpine` with persistence disabled, then run the integration suite against it; the first run may download the image.
- **End to end:** run the real publishing tool, load the 100k fixture into Redis, launch the published API as a separate process, and independently verify the HTTP root and sampled proofs.
- **Refresh:** publish the eight-user fixture and verify its updated root and proofs; the script temporarily sets the daily UTC time a few seconds ahead.
- **Cleanup:** stop the API and remove the script's container and temporary files, including on failure or `Ctrl+C`. Failures print logs and exit nonzero; the downloaded Docker image remains available for later runs.

If Redis is installed directly, the same script can run without Docker:

```sh
python3 tests/run-integration.py --redis local
```

- Local mode requires `redis-server` and `redis-cli` on `PATH`; it starts and stops an isolated Redis process with persistence disabled.
- Mock files remain under `tests/Reserve.Tests/Fixtures/`; neither mode changes the saved fixtures or requires a manually started API.

## Stress test
```s
python3 tests/run-integration.py --stress
```
- Sends **10,000 requests per case with 32 concurrent clients** using persistent HTTP connections; warmup is excluded from measurements.
- Tests the cached root, cached proofs for 100 users, and proofs for distinct users whose responses are not yet cached.

```sh
# Change request count and concurrency, or use installed Redis instead of Docker.
python3 tests/run-integration.py --stress --requests 20000 --clients 64
python3 tests/run-integration.py --stress --redis local
```
Example local run: macOS 15.3.2, .NET 10 Release, Redis in Docker, 100k users, 10,000 requests per case, 32 clients.

| Case | Requests/s | Median | p95 | Errors |
| --- | ---: | ---: | ---: | ---: |
| Root, cached | 17,814 | 1.46 ms | 3.77 ms | 0 |
| Proof, cached | 17,161 | 1.56 ms | 3.82 ms | 0 |
| Proof, distinct users | 18,002 | 1.47 ms | 3.74 ms | 0 |

- All **30,000 requests completed**; each measured batch lasted about **0.6 seconds**, so this is a short load check rather than a sustained capacity test.
- **p95 latency:** 95% of measured request times are at or below this value.
- The Python client and API share the same machine, so results include local client overhead and do not establish production capacity or long-term memory stability.

## Dependencies

| Dependency | Purpose |
| --- | --- |
| .NET 10 / ASP.NET Core shared framework | HTTP hosting, JSON, background refresh, and bounded in-memory caching. |
| `tagged-merkle` `v0.1.0` (Git submodule) | Tagged SHA-256 roots and immutable proof snapshots. |
| `StackExchange.Redis` `3.2.0` | Maintained asynchronous Redis client and shared connections. |
| `RESPite` `3.2.0` (transitive) | Redis protocol support used by the Redis client. |
| `System.IO.Hashing` `10.0.12` (transitive) | Redis-client dependency; application Merkle hashes use only BIP340-tagged SHA-256. |
| `NUnit` `4.6.1` (tests only) | Test framework: `[Test]`, `[TestCase]`, `Assert.That`. |
| `NUnit3TestAdapter` `6.3.1` (tests only) | Lets `dotnet test` discover and run NUnit tests. |
| `Microsoft.NET.Test.Sdk` `18.11.0` (tests only) | Test host used by `dotnet test`. |

- NuGet versions are recorded in `packages.lock.json`.
- See [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) and its [connection guidance](https://stackexchange.github.io/StackExchange.Redis/Basics.html).
 |
