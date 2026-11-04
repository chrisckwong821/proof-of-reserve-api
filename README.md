# Proof of Reserve API

A .NET 10 API using `tagged-merkle` as a Git submodule pinned to **v0.1.0**. The default data source is a saved 100,000-user fixture; a Redis adapter is included.

## Run

```sh
git submodule update --init --recursive
dotnet restore --locked-mode
dotnet build -c Release
dotnet run --project src/Reserve.Api -c Release --no-build -- --urls http://localhost:5080
```

```sh
curl http://localhost:5080/api/reserves/root
curl http://localhost:5080/api/reserves/users/10/proof
```

- The first snapshot loads before the server accepts requests; no valid initial data means startup fails.
- This demo has no authentication; a deployed service must authorize access to each user's balance and proof.

## API contract

| Method and path | Result |
| --- | --- |
| `GET /api/reserves/root` | Cached root, user count, snapshot ID, generation time, and source version. |
| `GET /api/reserves/users/{userId}/proof` | User ID, JPY balance, root, snapshot ID, and sibling tuples. |
| `GET /health/ready` | Loaded snapshot ID and generation time. |

- Users are ordered by **numeric ID ascending**; duplicate IDs, nonpositive IDs, null records, and negative balances are rejected.
- IDs use C# `int`; balances use `long` to allow large integer JPY amounts without floating-point rounding.
- A leaf is the strict UTF-8 encoding of `"(id,balance)"`, using invariant decimal formatting with no spaces or separators.

```text
User ID 10, balance 100 JPY → "(10,100)"
Leaf tag:   ProofOfReserve_Leaf
Branch tag: ProofOfReserve_Branch
H_tag(M) = SHA256(SHA256(UTF8(tag)) || SHA256(UTF8(tag)) || M)
```

- Sibling tuples are JSON arrays `["lowercase hex digest", position]`, ordered from the leaf toward the root; `0` means a left sibling and `1` means a right sibling.
- Duplicate the last node at every odd-sized level; a singleton has an empty proof, and an empty dataset has a 32-byte zero root.

```json
{
  "snapshotId": "snapshot identifier",
  "root": "64 lowercase hexadecimal characters",
  "userId": 10,
  "userBalance": 100,
  "proof": [
    ["closest sibling's 64-character hex digest", 0],
    ["next sibling's 64-character hex digest", 1]
  ]
}
```

- To verify, hash `"(userId,userBalance)"` with the leaf tag, then combine each sibling on the indicated side using the branch tag; the final digest must equal `root`.
- To bind a proof request to a previously fetched root, pass `?snapshotId=<id>`; a refresh in between returns `409`, so the client can fetch the new root and retry.
- Unknown IDs return `404`; nonpositive IDs return `400`, and non-integer route values do not match the route (`404`).

## Data fixtures

| File | Contents |
| --- | --- |
| `data/users-100000.json` | IDs `1..100000`, randomized integer balances `0..999999` JPY, generated with seed `340`. |
| `data/users-example.json` | `(1,1111)` through `(8,8888)`, exactly as specified. |
| `data/user-10-example.json` | `(10,100)`, demonstrating tuple serialization and a singleton proof. |

- The saved JSON is the test dataset; balances are not regenerated at server startup.
- The generator replaces the output file after writing a complete temporary file, so refreshes do not read partially written JSON.

```sh
# Reproduce the large fixture using the .NET 10 generator.
dotnet run --project tools/ReserveData -c Release -- generate data/users-100000.json

# Run with the eight-user example instead.
dotnet run --project src/Reserve.Api -c Release -- --urls http://localhost:5080 --Reserve:FilePath=data/users-example.json
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
docker compose up -d redis

# This command explicitly writes a new immutable dataset and updates its pointer.
dotnet run --project tools/ReserveData -c Release -- seed-redis data/users-100000.json localhost:6379

dotnet run --project src/Reserve.Api -c Release -- --urls http://localhost:5080 --Reserve:Source=Redis
```

- `por:current` stores a manifest `{ "version": "...", "key": "por:users:...", "count": 100000 }`; the versioned Redis hash maps decimal user IDs to decimal balances.
- The publisher writes every field under a **new unique key**, then atomically replaces the manifest string; it must never modify a published hash in place.
- The reader loads the manifest once, pages through its immutable hash with `HSCAN`, deduplicates repeated scan entries, and checks the declared user count before accepting the snapshot.
- Keep old versioned hashes until no reader can still be loading them; the sample publishing tool deliberately does not delete old versions.
- Redis connections are shared and reused; supply production connection settings through `Reserve__RedisConnection`, not committed credentials.

## Caching and daily refresh

```text
File or versioned Redis hash
              ↓ startup / periodic refresh
Validate → sort IDs → serialize tuples → build immutable Merkle tree
              ↓ one atomic reference swap
        current snapshot
          ├── cached root JSON
          └── bounded proof JSON cache: (snapshot ID, user ID)
```

- **Root cache:** JSON is created once per snapshot and reused for every root request.
- **Proof cache:** the first lookup builds a proof from retained tree levels; subsequent lookups reuse its JSON, with a configurable 10,000-entry limit.
- **Refresh:** rebuild every 24 hours by default; `Reserve__RefreshInterval=00:05:00` changes this to five minutes when earlier update detection is needed.
- **Consistency:** each request captures one complete snapshot; a replacement cannot mix old balances with a new root, and proof cache keys never cross snapshot versions.
- **Failure:** a failed refresh keeps the last valid snapshot and logs the error; retries occur on the next configured interval, so monitor the generation timestamp for stale data.
- **HTTP caching:** both APIs send `ETag` and `Cache-Control: private, no-cache`; a matching `If-None-Match` returns `304` after revalidation.
- **Concurrency:** only one refresh runs at a time per process; simultaneous proof-cache misses may repeat small proof work but never rebuild the tree or query the database.

Configuration is in `src/Reserve.Api/appsettings.json`; command-line options and `Reserve__...` environment variables can override it.

## Tests

```sh
# Real HTTP tests against an ephemeral local server; no Redis required.
dotnet run --project tests/Reserve.Tests -c Release

# Include Redis integration using an isolated test instance.
RESERVE_TEST_REDIS=localhost:6379 dotnet run --project tests/Reserve.Tests -c Release
```

- **Known answers:** the eight-user root and all eight proofs, including sibling tuple format, balances, and left/right bits.
- **Caching:** repeated requests, conditional GET, concurrent requests, bounded-cache eviction, and no database rereads on cached paths.
- **Refresh:** both caches change together, stale snapshot IDs return `409`, periodic refresh runs, and slow/failed refreshes preserve a complete usable snapshot.
- **Inputs:** unknown IDs, invalid IDs, duplicate users, negative balances, numeric ordering, and culture-independent `"(10,100)"` formatting.
- **Tree shapes:** empty, singleton, five-user odd-sized trees, and independently reconstructed proofs for sampled users in the 100,000-user fixture.
- **Redis:** scan an isolated versioned hash, reconstruct the expected root, and reject an incorrect manifest count.
- The executable suite runs **15 checks without Redis or 16 with Redis**; any failed assertion exits nonzero.
- GitHub Actions checks out submodules, restores locked dependencies, builds Release, and runs the full suite with a Redis service on pushes and pull requests.

## Complexity and practical loads

Let `n` be user count, `B` the total serialized UTF-8 bytes, and `c` the proof-cache entry limit.

| Operation | Time | Additional space |
| --- | --- | --- |
| Load dataset | `O(n)` records | `O(n)` |
| Validate and sort IDs | `O(n log n)` | `O(n)` snapshot copy |
| Build Merkle tree | `O(B + n)` | `O(n)` retained hashes |
| Root cache lookup | `O(1)` | `O(1)` |
| Uncached proof | `O(log n)` | `O(log n)` response |
| Cached proof lookup | Expected `O(1)` | Cache bounded by `O(c log n)` |

- Snapshot loading is **I/O-bound**; sorting, encoding, and hashing are **CPU-bound**, performed during background refresh rather than per request.
- Cached requests still incur HTTP transfer costs; neither API reads Redis or rebuilds the tree on its request path.
- For 100,000 users, the tree retains roughly **6.1 MiB of digest bytes**, plus user indexes, serialized responses, and runtime overhead; old and new snapshots coexist during refresh.
- For datasets that cannot fit in memory, move snapshot construction into a worker and persist indexed tree levels; this reference implementation intentionally retains each process's snapshot in RAM.
- This is a commitment to the supplied balances and inclusion proofs; asset ownership and completeness of the source database are outside these two endpoints.

## Dependencies

| Dependency | Purpose |
| --- | --- |
| .NET 10 / ASP.NET Core shared framework | HTTP hosting, JSON, background refresh, and bounded in-memory caching. |
| `tagged-merkle` `v0.1.0` (Git submodule) | Tagged SHA-256 roots and immutable proof snapshots. |
| `StackExchange.Redis` `3.2.0` | Maintained asynchronous Redis client and shared connections. |
| `RESPite` `3.2.0` (transitive) | Redis protocol support used by the Redis client. |
| `System.IO.Hashing` `10.0.12` (transitive) | Redis-client dependency; application Merkle hashes use only BIP340-tagged SHA-256. |

- NuGet versions are recorded in `packages.lock.json`; no test framework package is required.
- See [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) and its [connection guidance](https://stackexchange.github.io/StackExchange.Redis/Basics.html).

## Repository layout

```text
src/Reserve.Api/       HTTP endpoints, data adapters, snapshots, and caches
tests/Reserve.Tests/   Executable HTTP and Redis integration suite
tools/ReserveData/     Reproducible fixture generation and Redis publishing
data/                 Saved JSON fixtures
vendor/tagged-merkle/  Submodule pinned to v0.1.0
```
