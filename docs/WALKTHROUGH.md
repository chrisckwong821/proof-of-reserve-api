# Walkthrough script: how the Proof of Reserve API works

A script to read aloud when introducing the repository to a code reviewer or a new engineer. It follows one path through the system: the data goes in, a snapshot gets built, a request gets served, and the data stays fresh. It names each class at the moment it does its job. Lines in *[brackets]* are cues for what to show on screen; don't read them aloud. It takes about 25 minutes. If a .NET or library type in the code is unfamiliar, see the [types reference](#appendix-the-net-and-library-types-youll-see) at the end; it's meant for looking things up, not for reading aloud.

---

## 1. What this service is for

*[Show README.md, top section.]*

This service publishes a proof of reserves. We hold a balance in Japanese yen for each user, and we want to commit publicly to all of those balances with a single value, a Merkle root, so that any user can check their own balance is included without seeing anyone else's.

So the service has two jobs, and they're its two main endpoints.

`GET /api/reserves/root` returns that single root hash, plus some context: how many users it covers, when it was built, and which data version it came from.

`GET /api/reserves/users/{userId}/proof` returns one user's balance and a short list of sibling hashes. With those, the user can recompute the root themselves and compare it with the published one. If it matches, their balance is in the tree.

There's also a third endpoint, `GET /health`, for operations. We'll get to it near the end.

This is an internal API. There's no authentication; that's a deliberate, known decision, and I'll come back to it under limitations.

## 2. The one idea to hold on to: the snapshot

*[Show the diagram in README.md under "How the server is organized".]*

Before any code, here's the one idea that explains most of the design.

The server keeps exactly one **snapshot** in memory. A snapshot is immutable. It contains every user, the full Merkle tree, an index from user ID to position in the tree, and the root response already turned into JSON bytes.

Requests never change anything. They grab the current snapshot and read from it.

When data changes, a background job builds a whole new snapshot off to the side. When it's completely finished, we swap a single reference so the new one becomes current. A request that started a moment earlier keeps using the old snapshot until it finishes. So a request can never see a new root with old balances, and there are no locks on the read path.

The diagram has two halves. The top half is refresh, which happens in the background. The bottom half is requests, which only read. Keep that split in mind and the folder layout will make sense too.

## 3. Where the data comes from: Redis

*[Show README.md, the "Redis" section with the example keys.]*

User data lives in Redis, in two kinds of keys.

The first is a **versioned user hash**, for example `por:users:<some-version-id>`. It's a Redis hash: the field is the user ID, and the value is the balance. Once it's written, it's never modified.

The second is a **manifest**, at `por:current`. It's one small JSON string with three fields: `version`, `key` (which user hash to read), and `count` (how many users that hash should contain).

To publish new data, the publisher writes a complete new hash first, then switches the manifest to point at it. That's one atomic `SET`. The old hash stays untouched, so anyone halfway through reading it isn't disturbed.

An important boundary: in production, a separate service writes this data. This repository only reads it. The `tools/ReserveData` program here writes the same format, but only for tests and local runs.

## 4. Startup: wiring everything together

*[Open `src/Reserve.Api/Program.cs`, then `ReserveApplication.cs`.]*

`Program.cs` is a single line. It calls `ReserveApplication.Build` and runs the result.

`ReserveApplication.Build` is where everything is wired together, and nothing else. Reading it top to bottom:

First, it creates the web host and points it at the folder next to the executable, so `appsettings.json` is found no matter where you launch from.

Second, settings. It binds the `Reserve` section of configuration into a `ReserveOptions` object and registers a validator that runs at startup. If any setting is wrong, the server refuses to start, and the error lists every bad setting at once.

Third, it registers the services. `RedisConnector` talks to Redis. `ReserveCache` holds the snapshot. `RefreshScheduler` and `ManifestPoller` run in the background and decide when to refresh. `SnapshotHealthCheck` backs the `/health` endpoint.

Finally, it builds the app and calls `MapReserveEndpoints` to attach the HTTP routes.

One detail matters for testing. Everything that needs user data asks for an `IUserDataSource`, a small interface with two methods: load all users, and read the current data version. In production, `Build` registers `RedisConnector` as that source. Tests pass their own in-memory source to `Build` instead, which is why most tests need no Redis at all. Every registration is a single line, and the framework creates each object and hands it what its constructor asks for.

### Settings, briefly

*[Open `Configuration/ReserveOptions.cs` next to `appsettings.json`.]*

People often ask why there's both a settings file and a settings class. They do different jobs. `appsettings.json` holds the **values**. `ReserveOptions` defines the **shape**: each setting's name and type, the rules it must follow, and defaults for the optional ones.

At startup, ASP.NET Core layers the sources, each overriding the one before: the JSON file, then an environment-specific file, then environment variables, then command-line arguments. It copies the result into one `ReserveOptions`.

There are five required settings: the daily refresh time, the size of the proof cache, and three Redis settings. There are four optional ones, with defaults, covering retries, health and polling. `GetErrors` checks every rule, one per line, so you can read the rules straight off the code.

`GetNextRefreshUtc` is the small helper everyone uses to answer "when is the next daily refresh?"

## 5. The first load, before any request is served

*[Open `Refresh/RefreshScheduler.cs`, the `StartAsync` method.]*

When the host starts, `RefreshScheduler.StartAsync` runs before the web server begins listening. It calls `ReserveCache.RefreshAsync` once.

If that first load fails, startup fails. That's deliberate: a server with no data can't answer anything, so it's better to fail loudly than to come up empty.

So let's follow `RefreshAsync`, because it's the heart of the system.

## 6. Loading users from Redis

*[Open `Data/RedisConnector.cs`, `LoadAsync`.]*

`RefreshAsync` first asks for the data. In production, that's `RedisConnector.LoadAsync`.

It reads the manifest and validates it. The version must be present, the count must not be negative, and the key must start with the configured prefix (`por:users:`). That prefix check stops a bad manifest from pointing us at some unrelated key.

Then it reads the user hash with `HSCAN`, a thousand entries per page. Each field and value must be a plain non-negative decimal integer, or the load is rejected.

There's a subtlety here. `HSCAN` is allowed to return the same field more than once, so we put users into a dictionary to drop repeats. If the same ID ever comes back with two different balances, the hash was modified while we were reading it, which breaks the publishing rules, so we stop.

Finally, the number of users must equal the manifest's `count`. That's our check that we read the complete dataset, not part of it.

The result is a `UserDataSet`: the version plus an array of `UserAccount` records, each just an ID and a balance.

The connection itself is created lazily, once, and shared by every load. At shutdown, `DisposeAsync` closes it cleanly, waiting at most five seconds.

## 7. Building the snapshot

*[Open `Snapshots/ReserveCache.cs`, `BuildSnapshot`.]*

Back in `RefreshAsync`, the data goes to `BuildSnapshot`. That's a pure function: data in, snapshot out, with no shared state. We run it on a background thread.

Step one, validate. Every user needs a positive ID and a non-negative balance.

Step two, sort by ID and reject duplicates. Sorting matters: it makes the tree, and so the root, the same no matter what order Redis returned the users in.

Step three, turn each user into **leaf text**. That's `UserAccount.Serialize`, which produces strings like `(3,3333)`: no spaces, and the same digits in every locale. This exact text is what gets hashed, so any client verifying a proof has to produce the same string.

Step four, build the Merkle tree. That's the `tagged-merkle` library in `vendor/`. A few facts worth knowing:

- It uses **tagged SHA-256**: hash the tag with SHA-256, write that 32-byte hash twice, append the message, and hash the whole thing with SHA-256. Leaves and branches use different tags, `ProofOfReserve_Leaf` and `ProofOfReserve_Branch`, which live in `ProofTags`. Different tags mean a leaf can never be mistaken for an internal node. Changing either tag changes every root, which is why they're named constants with a warning.
- Each parent is the hash of its left child followed by its right child.
- If a level has an odd number of nodes, the last one is paired with itself.
- An empty dataset has a root of all zeros.
- The library keeps every level of the tree, so producing a proof later is just reading one hash per level. Nothing is recomputed.

Step five, build a lookup from user ID to their position in the tree and their balance. A user's position in the sorted array is their leaf index.

Step six, create the root response: a new snapshot ID, the root hash, the user count, the build time and the data version. Turn it into JSON bytes once, right here.

All of that is packed into one `Snapshot` record: the root, the root JSON, the tree and the user lookup. Those four always travel together.

### Publishing

*[Back to `RefreshAsync`.]*

`RefreshAsync` only lets one refresh run at a time. When the build finishes, it does three things.

It publishes with `Volatile.Write`. That's the single reference swap from section 2.

It clears the proof cache, because every proof in it belonged to the old snapshot.

It logs one line: "Published reserve snapshot", with the ID, user count, version, root and how long it took.

If anything fails along the way, nothing is published, and the old snapshot keeps serving.

## 8. Serving a request

*[Open `Endpoints/ReserveEndpoints.cs`.]*

Now the bottom half of the diagram. `MapReserveEndpoints` just lists the three routes. The logic is in two named methods.

### The root

`GetRoot` takes the current snapshot. It sets an `ETag` header to the snapshot ID. If the client sent the same value in `If-None-Match`, it answers `304 Not Modified` with no body. Otherwise, it sends the stored root JSON bytes as they are. Nothing is computed per request, which is why the root endpoint handles around eighteen thousand requests a second in the local stress test.

### A proof

`GetProof` checks things in a fixed order, and each failure has its own JSON error:

1. The user ID must be an integer, or the answer is `400`, "must be a positive integer".
2. It must be positive, or it's `400`, "must be positive".
3. It takes the current snapshot, once. Everything after this uses that same snapshot.
4. If the client pinned a snapshot with `?snapshotId=` and that's no longer the current one, the answer is `409`, and the body includes the current ID so the client knows to fetch the root again.
5. It asks `ReserveCache.GetProofJson` for the proof. If the user doesn't exist, that's `404`.
6. Finally, the same ETag logic as the root, with the snapshot ID plus the user ID, so it's `304` or the bytes.

`GetProofJson` is where caching happens. The cache key is the pair of snapshot ID and user ID, so a proof can never leak across snapshots. On a hit, it returns the stored bytes. On a miss, it reads the sibling hashes from the tree, builds the JSON, and stores it. The cache holds at most `ProofCacheEntries` proofs, ten thousand by default, and evicts older, less-used entries when it's full.

### What the client does with a proof

*[Show the user 3 example in README.md.]*

Here's the example for user 3 in the eight-user dataset. The client hashes the leaf text `(3,3333)` with the leaf tag. Then, for each pair in `proof`, it combines its current hash with the given sibling. Position `1` means the sibling goes on the right, and `0` means it goes on the left. Each combination is hashed with the branch tag. After three steps, it has a hash. If that equals `root`, the balance is proven to be included.

Our tests do exactly this, independently. More on that later.

## 9. Keeping the data fresh

*[Open `Refresh/RefreshScheduler.cs`, `ExecuteAsync`.]*

Two background jobs decide **when** to refresh. Both just call the same `RefreshAsync`.

### The daily refresh, with retries

`RefreshScheduler` works out the next daily time, 00:10 UTC by default, logs it, and waits.

The waiting has a subtle fix in `DelayUntilAsync`. A timer can fire a millisecond before the clock actually reaches the target time. Without the fix, a fast refresh could run more than once, or be stamped a hair before its scheduled time. So we keep waiting until the clock really has passed it.

If the refresh fails, it retries with backoff: wait one minute, then two, four, eight, then every fifteen minutes. It stops when a retry succeeds, or when the next day's refresh would come first. Every failure is logged as an error. Those lines start with "Refresh failed; keeping snapshot", the same text as before retries existed, so existing alerts still match. The old snapshot serves the whole time.

`GetRetryDelay` is the backoff calculation on its own, a pure function, so it's tested without any real waiting.

### Mid-day updates, optional

*[Open `Refresh/ManifestPoller.cs`.]*

By default, data published at two in the afternoon isn't picked up until the next 00:10, or until a restart. If you set `ManifestPollInterval`, `ManifestPoller` reads only the manifest's version at that interval. That's one tiny Redis `GET`. If the version differs from the one being served, it calls `RefreshAsync`.

A failed check logs a warning and tries again at the next interval. It's off by default, because mid-day updates change the root during the day, which is a product decision, not just a technical one.

## 10. Knowing it's working: health and logs

*[Open `Monitoring/SnapshotHealthCheck.cs`.]*

There are two ways to see what the server is doing.

The first is `SnapshotHealthCheck`, which powers `GET /health`. The rule is in `Evaluate`: the snapshot is **stale** if it's older than the most recent scheduled refresh *and* the grace period, thirty minutes by default, has passed since that refresh was due. Stale means `503`; otherwise it's `200`. The body includes the snapshot ID, data version, user count, when it was built, and when the next refresh is due. With the default settings, all the retries fit inside the grace period, so `/health` only turns red if every retry has failed.

A practical note: use `/health` for alerts or for a load balancer's readiness check. Don't use it as a probe that restarts the server. If Redis is down, a restarted server can't load data, so it won't come back up.

The second is the logs. At the normal level, you see one line each time a snapshot is published, the next scheduled refresh time, every failed refresh with its retry plan, and polling changes. Per-request lines also exist, at Debug level, under the category `Reserve.Api.Requests`. They say whether each proof came from the cache or was newly built. They're off by default because at thousands of requests a second they'd flood the logs, so you switch them on for short debugging sessions.

## 11. How we know it's correct: the tests

*[Open `tests/Reserve.Tests/`.]*

The tests use NUnit and are grouped by area: `ConfigurationTests`, `ApiTests`, `RefreshTests`, `PollingTests`, `ObservabilityTests` and `RedisTests`.

Three helpers in `TestSupport.cs` do most of the work:

- `TestApp` starts a real, in-process server for each test, so tests never share state and any one can run alone.
- `MutableSource` is a fake data source. A test can change its data, make the next few loads fail, or pause a load halfway, to simulate slow or broken Redis.
- `ProofVerifier` recomputes the root from a proof **without** using our Merkle library. It has its own copy of the hashing rule and the tag strings. If someone changed the library or the tags by mistake, the known-answer tests would fail instead of quietly agreeing with the mistake.

The expected roots for the eight-user and the hundred-thousand-user fixtures were checked independently with Python. Some tests pin exact response bytes, including the README's example proof and every error body, so a refactor can't change the API without a test noticing.

`RedisTests` run only when `RESERVE_TEST_REDIS` points at a throwaway Redis; otherwise they show as skipped.

On top of that, `tests/run-integration.py` is what CI runs. It builds and publishes the real server, starts its own Redis, seeds the hundred-thousand-user fixture, and checks roots and proofs over HTTP using Python's own hashing. Then it publishes new data and checks the scheduled refresh picks it up, stops Redis to check the server keeps serving, and cleans up after itself. With `--stress`, it also runs the load test.

## 12. Design decisions and known limitations

To close, the honest list.

- **No authentication, and some privacy limits.** Anyone who can reach the API can request any user's proof, and a proof's first sibling hash lets someone guess their neighbour's balance by trying values. That's accepted because this is an internal API. It must stay on an internal network. If it's ever exposed to end users, both need fixing first.
- **The snapshot ID is random.** Every load creates a new ID, even for unchanged data. That's fine for one server. With several servers behind a load balancer, clients that pin `snapshotId` could get `409`s when they land on a different server.
- **The first load isn't retried.** If Redis is down at startup, the server doesn't start.
- **Publishing rules are a contract with another team.** The server relies on the publisher never modifying a hash after the manifest points at it, and on keys using the expected prefix.

## 13. Recap, and where to start reading

So, in one breath. Redis holds immutable versioned data behind a manifest. `RedisConnector` reads and validates it. `ReserveCache` turns it into an immutable snapshot with the Merkle tree and swaps it in atomically. `ReserveEndpoints` serves the root and proofs straight from that snapshot, with a proof cache and ETags. `RefreshScheduler` and `ManifestPoller` decide when to rebuild. And `/health` tells you whether the data is fresh.

If you're new and want to read the code, read it in this order:

1. `ReserveApplication.cs`, to see the pieces.
2. `ReserveCache.RefreshAsync` and `BuildSnapshot`, the core.
3. `ReserveEndpoints.cs`, the read path.
4. `RefreshScheduler.cs`, timing and retries.
5. `ApiTests.cs` and `TestSupport.cs`, to see how all of it is exercised.

That's the whole system. Questions?

---

## Appendix: the .NET and library types you'll see

A lookup table for the framework and library types the server uses, in plain words. It's grouped the way the code is.

**Hosting and wiring (ASP.NET Core)**

| Type | What it does here |
| --- | --- |
| `WebApplication` / `WebApplicationBuilder` | The web server, plus a container that creates objects for you. `builder.Services.Add…` registers them, and `Build()` creates the app. |
| `AddSingleton<T>` | "Create one `T` and give the same one to everyone who needs it." |
| `AddSingleton<IUserDataSource, RedisConnector>()` | "Whenever something asks for an `IUserDataSource`, give it the one `RedisConnector`." This is how tests swap in fake data: they register a different source. |
| `BackgroundService` | Base class for something that runs in the background for the app's whole life. You write `ExecuteAsync` as a loop. Used by `RefreshScheduler` and `ManifestPoller`. |
| `AddHostedService<T>` | "Start this background service when the app starts, and stop it when the app stops." |
| `IOptions<T>`, `AddOptions().BindConfiguration()` | Copies a config section, such as `Reserve`, into a C# object. |
| `IValidateOptions<T>`, `ValidateOnStart()` | Checks that object at startup and refuses to start if it's wrong. |

**HTTP**

| Type | What it does here |
| --- | --- |
| `MapGet(path, handler)` | "When a GET hits this path, call this function." Route values such as `{userId}` and query values such as `?snapshotId=` are passed in as parameters automatically. |
| `HttpContext` | The current request and response, used here to read and write headers such as `ETag`. |
| `IResult` / `Results.Bytes`, `Results.Json`, `Results.StatusCode` | What a handler returns: "send these bytes", "send this object as JSON", "send this status". |
| `MapHealthChecks`, `IHealthCheck`, `HealthCheckResult` | Built-in `/health`. You write one check that returns Healthy or Unhealthy, and the framework turns it into `200` or `503`. |

**Redis (StackExchange.Redis)**

| Type | What it does here |
| --- | --- |
| `ConnectionMultiplexer` | One long-lived connection to Redis, shared by everything. Expensive to create, so it's created once. |
| `IDatabase` | The handle you call commands on. |
| `StringGetAsync` | Redis `GET`, which reads the manifest. |
| `HashScanAsync` | Redis `HSCAN`, which reads a hash page by page without blocking Redis. |

**Concurrency and timing (.NET)**

| Type | What it does here |
| --- | --- |
| `Task`, `async`/`await` | "This may take a while; don't block a thread while waiting." |
| `CancellationToken` | A "please stop" signal, passed down so work ends quickly at shutdown. |
| `SemaphoreSlim(1, 1)` | A lock that lets only one refresh run at a time. |
| `Volatile.Read` / `Volatile.Write` | Read or replace the current snapshot reference so other threads see the change immediately. This is the one-step swap. |
| `Lazy<T>` | "Create this the first time someone asks, and only once." Used for the Redis connection. |
| `Task.Run` | Do CPU-heavy work, such as building the tree, on a background thread. |
| `Task.Delay`, `PeriodicTimer` | "Wait this long" and "tick every N". |
| `Stopwatch` | Measures how long a load took, for the log line. |

**Data structures and caching**

| Type | What it does here |
| --- | --- |
| `record` | A small immutable data class, such as `UserAccount(Id, Balance)`, with equality built in. |
| `FrozenDictionary` | A read-only dictionary that's very fast to look up. Ideal for a snapshot that never changes. |
| `MemoryCache` | An in-memory cache with a size limit. It holds proof JSON. `Clear()` empties it. |
| `JsonSerializer.SerializeToUtf8Bytes` | Turns an object into JSON bytes. It's done once, so requests can send stored bytes. |

**Logging**

| Type | What it does here |
| --- | --- |
| `ILogger<T>` | Writes log lines. `T` sets the category, such as `Reserve.Api.ReserveCache`, which is what log levels are configured by. |
| `[LoggerMessage]` | A faster, generated logging method, used only on the per-request path. |
