# Proof of Reserve API

This API lets users check that their JPY balance is included in a published Merkle root.

- `GET /api/reserves/root` returns one hash that commits to every user's ID and balance.
- `GET /api/reserves/users/{userId}/proof` returns a user's balance and the hashes needed to rebuild that root.

This is an **internal** service: it has no authentication and must only be reachable on an internal network (see [Limitations](#limitations)). A separate service publishes user balances to Redis; this repository only reads them.

New to the code? Start with the [walkthrough](docs/WALKTHROUGH.md).

## Quick start

You need the .NET 10 SDK and Docker. From the repository root:

```sh
git submodule update --init --recursive
docker compose up -d --wait redis
dotnet build -c Release
dotnet run --project tools/ReserveData -c Release --no-build -- seed-redis tests/Reserve.Tests/Fixtures/users-example.json localhost:6379
dotnet run --project src/Reserve.Api -c Release --no-build -- --urls http://localhost:5080
```

Then, in another terminal:

```sh
curl http://localhost:5080/api/reserves/root
```

The server loads its data from Redis before it accepts requests. If Redis has no valid data, or a setting is invalid, it stops with an error saying why.

## API

| Method and path | Returns |
| --- | --- |
| `GET /api/reserves/root` | The root, user count, snapshot ID, build time and data version |
| `GET /api/reserves/users/{userId}/proof` | The user's ID and balance, the root, the snapshot ID, and the proof |
| `GET /health` | `200` while the data is current, `503` once it's stale; see [Operations](docs/OPERATIONS.md#health-check) |

**Root**, for the eight-user example:

```json
{
  "snapshotId": "00000000000000000000000000000001",
  "root": "b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3",
  "userCount": 8,
  "generatedAtUtc": "2026-11-04T00:10:00+00:00",
  "dataVersion": "00000000000000000000000000000002"
}
```

**Proof for user 3** (`GET /api/reserves/users/3/proof`):

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

### How a proof is checked

- Hashes are tagged SHA-256: `H(tag, msg) = SHA256(SHA256(tag) ‖ SHA256(tag) ‖ msg)`.
- Start with the **leaf**: `H("ProofOfReserve_Leaf", "(userId,balance)")`, using the UTF-8 text with no spaces, e.g. `(3,3333)`.
- Each `proof` entry is `[siblingHash, bit]`, ordered from the leaf up to the root. Bit `0` means the sibling is on the left, and `1` means it's on the right.
- At each step, put the sibling on its side and hash the pair: `H("ProofOfReserve_Branch", left ‖ right)`. If the final hash equals `root`, the balance is included.
- Users are sorted by ID before the tree is built. On a level with an odd number of nodes, the last node is paired with itself.

### Errors

Proof errors return JSON `{"error": "..."}`:

| Status | When | `error` |
| --- | --- | --- |
| `400` | `userId` isn't an integer, e.g. `abc`, `1.5`, or larger than 2147483647 | `User ID must be a positive integer.` |
| `400` | `userId` is zero or negative | `User ID must be positive.` |
| `404` | No user has this ID in the current snapshot | `User not found.` |
| `409` | `?snapshotId=` names a snapshot that has since been replaced; the body also includes the current `snapshotId` | `Snapshot changed; fetch the current root and retry.` |

## How it works

The server keeps one immutable **snapshot** in memory: every user, the Merkle tree, and the root response. Requests only read the current snapshot. To pick up new data, a background job builds a complete replacement and swaps it in with one step, so a request never sees a new root with old balances.

The data comes from Redis. A **manifest** key (`por:current`) points at the current immutable hash of user balances. To publish, the publishing service writes a new hash, then moves the manifest.

```text
Refresh (background: build a new snapshot, then swap it in)
  RefreshScheduler ─┐
  ManifestPoller   ─┴─▶ ReserveCache.RefreshAsync ─▶ RedisConnector ─▶ Redis
                              └─▶ BuildSnapshot (tagged-merkle) ─▶ publish snapshot

Requests (read only: use the current snapshot)
  GET root / proof ─▶ ReserveEndpoints ─▶ ReserveCache.Current ─▶ JSON or 304
  GET /health      ─▶ SnapshotHealthCheck ─▶ ReserveCache.Current
```

| Folder (`src/Reserve.Api/`) | Contents |
| --- | --- |
| *(root)* | `Program.cs` (entry point), `ReserveApplication.cs` (wires everything together), `appsettings.json` |
| `Endpoints/` | The root, proof and `/health` routes |
| `Snapshots/` | `ReserveCache`: builds and holds the snapshot, plus the proof cache |
| `Refresh/` | `RefreshScheduler` (startup load, daily refresh, retries) and `ManifestPoller` (optional mid-day check) |
| `Data/` | `IUserDataSource` and its Redis implementation, `RedisConnector` |
| `Monitoring/` | `SnapshotHealthCheck`, behind `/health` |
| `Configuration/` | `ReserveOptions`: every setting and its validation |
| `Models/` | The loaded data and the API response types |

Other folders: `tests/` (tests and scripts), `tools/ReserveData/` (test data generator and Redis seeder), `vendor/tagged-merkle/` (the Merkle library, a Git submodule pinned to `v0.1.0`), and `docs/`.

## Configuration

Settings live in `src/Reserve.Api/appsettings.json` under `Reserve`. Override any of them with an environment variable (`Reserve__RefreshTimeUtc=01:00:00`) or a command-line argument (`--Reserve:RefreshTimeUtc 01:00:00`). Times use `hh:mm:ss`.

| Setting | Shipped value | What it does |
| --- | --- | --- |
| `RefreshTimeUtc` | `00:10:00` | UTC time of day to reload data from Redis |
| `ProofCacheEntries` | `10000` | How many user proofs to keep in memory |
| `RedisConnection` | `localhost:6379,abortConnect=false` | Redis connection string |
| `RedisManifestKey` | `por:current` | Key holding the manifest |
| `RedisUserKeyPrefix` | `por:users:` | Required prefix of the user-hash key the manifest points to |
| `StaleGracePeriod` *(optional)* | `00:30:00` | How long after a missed refresh `/health` still reports healthy |
| `RefreshRetryDelay` *(optional)* | `00:01:00` | First wait before retrying a failed refresh; doubles each time; `00:00:00` turns retries off |
| `RefreshRetryMaxDelay` *(optional)* | `00:15:00` | Longest wait between retries |
| `ManifestPollInterval` *(optional)* | `00:00:00` (off) | How often to check Redis for new data during the day |

The [operations guide](docs/OPERATIONS.md) covers refresh, retries, health, logs and the Redis data format in detail.

## Testing

```sh
# Unit and HTTP tests; no Redis needed.
dotnet test tests/Reserve.Tests -c Release

# Everything: the Redis tests, the published app end to end, and independent hash checks in Python (needs Docker).
python3 tests/run-integration.py

# Load test.
python3 tests/run-integration.py --stress
```

CI runs `tests/run-integration.py` on every push and pull request. On a laptop, both endpoints handle about 18,000 requests per second. See the [testing guide](docs/TESTING.md) for the test layout, the fixtures and the full results.

## Limitations

- **No authentication.** Anyone who can reach the API can read any user's balance, so keep it on an internal network.
- **Neighbour balances can be guessed.** A proof's first sibling hash is the neighbouring user's leaf. With sequential IDs and a limited balance range, it can be brute-forced to reveal that user's balance.
- **The snapshot ID is random per load.** Several instances serving the same data report different `snapshotId`s, so clients that pin `?snapshotId=` can get `409` when requests land on different instances.
- **The first load isn't retried.** If Redis is unavailable at startup, the server doesn't start.

The [production enhancements](docs/PRODUCTION_ENHANCEMENT.md) doc describes how each of these could be addressed.

## Dependencies

- .NET 10 / ASP.NET Core
- [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) `3.2.0`
- [`tagged-merkle`](https://github.com/chrisckwong821/tagged-merkle) `v0.1.0`, as a Git submodule. Submodules record a commit, not a tag name; `git submodule status` shows the matching tag. To move to another release, run `git -C vendor/tagged-merkle fetch --tags` and `git -C vendor/tagged-merkle checkout <tag>`, then commit `vendor/tagged-merkle`.
- Tests: NUnit `4.6.1`, NUnit3TestAdapter `6.3.1`, Microsoft.NET.Test.Sdk `18.11.0`

Exact versions, including transitive packages, are recorded in each project's `packages.lock.json`.
