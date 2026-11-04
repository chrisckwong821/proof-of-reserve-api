# Testing guide

For people changing the code: how the tests are organised, what the end-to-end script checks, how to load-test, and what the test fixtures contain. Run every command from the repository root.

## Unit and integration tests

```sh
# HTTP and cache tests with an in-memory data source; no Redis needed.
dotnet test tests/Reserve.Tests -c Release

# Also run the Redis tests against an isolated test instance.
RESERVE_TEST_REDIS=localhost:6379 dotnet test tests/Reserve.Tests -c Release

# Show each test's name, or run only some tests.
dotnet test tests/Reserve.Tests --logger "console;verbosity=normal"
dotnet test tests/Reserve.Tests --filter "FullyQualifiedName~ApiTests"
```

- Tests use [NUnit](https://docs.nunit.org/), grouped by area: `ApiTests`, `RefreshTests`, `PollingTests`, `ConfigurationTests`, `ObservabilityTests` and `RedisTests`.
- Each test class starts its own in-process server in `[SetUp]`, so tests are independent and can run alone.
- Shared helpers live in `TestSupport.cs`:
  - `TestApp` starts a fresh server.
  - `MutableSource` is a controllable in-memory data source that can change its data, fail loads, or pause them.
  - `ProofVerifier` rebuilds the root from a proof **without** the production Merkle library, so a library bug can't hide itself.
- Redis tests are reported as **skipped** unless `RESERVE_TEST_REDIS` is set.

## End-to-end test

GitHub Actions runs this script on pushes, pull requests and manual runs.

```sh
python3 tests/run-integration.py
```

- Requires .NET 10, Python 3 and a running Docker engine. The script uses only Python's standard library, both to control processes and to verify hashes independently.
- It creates its own Redis container on unused loopback ports, and doesn't use or modify an existing Redis.

What it checks, in order:

1. **Build:** initialize submodules, restore locked packages, build Release, and publish the API to a temporary folder. It checks that no test fixtures are shipped.
2. **Redis tests:** start `redis:7.4-alpine` with persistence off, then run the NUnit suite against it, including `RedisTests`. The first run may download the image.
3. **End to end:** seed the 100k fixture with the real `ReserveData` tool, start the published API as its own process, and verify the root and sampled proofs over HTTP with Python's `hashlib`.
4. **Caching:** check ETags, concurrent requests that don't reread Redis, and error statuses.
5. **Refresh:** publish the eight-user fixture and check the scheduled refresh picks it up. The script sets the daily time a few seconds ahead.
6. **Outage:** stop Redis and check the server keeps serving its current snapshot.
7. **Cleanup:** stop the API and remove the container and temporary files, including on failure or `Ctrl+C`. Failures print logs and exit non-zero.

If Redis is installed locally, the same script runs without Docker:

```sh
python3 tests/run-integration.py --redis local
```

Local mode needs `redis-server` and `redis-cli` on `PATH`. It starts and stops its own isolated Redis process, with persistence off.

## Stress test

```sh
python3 tests/run-integration.py --stress
```

- The load generator is `tests/stress.py`, run through the integration script so Redis and the API are set up for it.
- Sends **10,000 requests per case with 32 concurrent clients** over persistent HTTP connections. Warmup is excluded from the measurements.
- Cases: the cached root, cached proofs for 100 users, and proofs for distinct users that aren't cached yet.

```sh
# Change the request count and concurrency, or use a local Redis instead of Docker.
python3 tests/run-integration.py --stress --requests 20000 --clients 64
python3 tests/run-integration.py --stress --redis local
```

Example run on a laptop (Apple M-series, .NET 10 Release, Redis in Docker, 100k users):

| Case | Requests/s | Median | p95 | Errors |
| --- | ---: | ---: | ---: | ---: |
| Root, cached | 17,814 | 1.46 ms | 3.77 ms | 0 |
| Proof, cached | 17,161 | 1.56 ms | 3.82 ms | 0 |
| Proof, distinct users | 18,002 | 1.47 ms | 3.74 ms | 0 |

- Each measured batch lasted about 0.6 seconds, so this is a short load check, not a sustained capacity test.
- **p95:** 95% of measured requests were at or below this time.
- The Python client and the API share one machine, so the results include client overhead and don't establish production capacity or long-term memory stability.

## Fixtures

| File | Contents |
| --- | --- |
| `tests/Reserve.Tests/Fixtures/users-example.json` | `(1,1111)` through `(8,8888)`; the dataset used in the README examples |
| `tests/Reserve.Tests/Fixtures/users-100000.json` | IDs `1..100000`, random integer balances `0..999999` JPY, generated with seed `340` |

- Fixtures are copied only to test output; the published API ships no mock data.
- The `ReserveData` tool's `generate` command writes fixture files atomically, and its `seed-redis` command validates a fixture before writing a new Redis dataset.

```sh
# Regenerate the large fixture.
dotnet run --project tools/ReserveData -c Release -- generate tests/Reserve.Tests/Fixtures/users-100000.json
```

Expected roots, checked independently with Python's `hashlib`:

```text
8-user example:          b1231de33da17c23cebd80c104b88198e0914b0463d0e14db163605b904a7ba3
100,000-user fixture:    347cfc213378db8331eef9b7fac2dccf625401a1fd1339b9f345432843221a9f
```
