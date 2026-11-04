# Production enhancements

Code-level improvements to consider if this Proof of Reserve API is offered as an actual service, beyond its current internal use. Each item explains the need and the benefit, with a short example.

Items are grouped by area and listed roughly from the quickest wins to the larger changes.

## Privacy and security

**1. Authentication on the proof endpoint**
- Anyone who can reach the API can read any user's balance just by changing the ID in the URL, **so** the server must check who is asking **before** returning a proof.
- **Once** the user ID comes from the sign-in token, each user can fetch only their own proof, **while** the root stays public for everyone.
```text
GET /api/reserves/users/4/proof   (token says user = 3)
  today: 200 with user 4's balance
  with auth: 403, because token.user ≠ route userId
```

**2. Rate limiting**
- Because nothing limits how many requests one client sends, a single client can guess IDs, force cache misses or overload the server, **so** each client needs a request budget.
- **With** a per-client limit, abusive traffic gets `429` early, **and therefore** the service stays fast for everyone else.
```text
policy "proof": 20 requests per minute per user (or per IP when there's no token)
request 21 in the same minute → 429 Too Many Requests, Retry-After: 30
```

**3. HTTPS in the app**
- Balances and sign-in tokens currently travel as plain HTTP, **so** anyone on the network path could read or change them.
- **When** every request is encrypted, tokens can't be stolen **and** proofs can't be altered on the way to the user.
```text
http://api/…  → 308 redirect to https://api/…
response header: Strict-Transport-Security: max-age=31536000
```

**4. Privacy-preserving leaves**
- Each proof includes the neighbouring user's leaf hash, **and because** IDs are sequential and balances fall in a small range, that hash can be brute-forced in seconds, even with authentication.
- **If** each leaf includes a secret random salt that only its owner receives, guessing becomes impossible, **so** a proof no longer reveals anything about other users.
```text
today: leaf = H("ProofOfReserve_Leaf",    "(4,4444)")             try balances 0…10⁹ → found in seconds
v2:    leaf = H("ProofOfReserve_Leaf_v2", "(4,4444,c2a7e1…2e05)") must also guess a 2²⁵⁶ salt → infeasible
```

## Scaling

**5. Deterministic snapshot ID**
- Because each server instance invents a random ID for the same data, a client's pinned `snapshotId` and `ETag` stop matching **whenever** a load balancer sends it to a different instance.
- **If** the ID is derived from the data itself, every instance reports the same ID, **so** the service can scale out without spurious `409`s or cache misses.
```text
today: snapshotId = random GUID                           (instance A ≠ instance B)
fixed: snapshotId = sha256(dataVersion + ":" + root)[:32] (A = B for the same data)
```

**6. Public caching for the root**
- The root is the same for every user **and** changes at most a few times a day, **yet** every request still reaches the server.
- **Since** it can safely be shared, CDNs and browsers can serve it, **which** cuts server load **and** makes responses faster.
```text
today: Cache-Control: private, no-cache                              → every request reaches the server
fixed: Cache-Control: public, max-age=60, stale-while-revalidate=300 → most served by the CDN
```

## Metrics and monitoring

**7. OpenTelemetry metrics**
- Today there are only logs and a yes/no `/health`, **so** there's no record over time of traffic, latency, cache efficiency, data age or refresh failures.
- **With** those numbers exported, dashboards and alerts catch slow responses, stale data and failing refreshes **before** users notice, **and** they give real figures for capacity planning.
```text
reserve_proof_cache_hits_total / reserve_proof_cache_misses_total
reserve_refresh_duration_seconds      reserve_refresh_failures_total
reserve_snapshot_age_seconds          → alert if > 90000 (about 25 h)
```

## Trust

**8. Signed roots**
- A client can't tell whether a root really came from us, **so** a dishonest operator could show different users different roots **and** later deny it.
- **When** each root is signed with our private key, anyone can check it with our public key, **so** every published root is provably ours **and** can't be denied later.
```text
signature = Sign(privateKey, root | userCount | dataVersion)
GET /api/reserves/root → { …, "signature": "3045…", "keyId": "2026-11" }
client: Verify(publicKey, root | userCount | dataVersion, signature) → true
```

**9. Merkle sum tree**
- The current root commits only to the list of leaves, **and because** sibling hashes carry no amounts, a user can prove their balance is *included* but nobody can prove what the balances *add up to*. An operator could even add fake negative accounts to shrink the total, **and** every honest proof would still pass.
- **If** each node also commits to the sum of the balances below it, every user can check that their path adds up to a published total with no negative subtrees, **so** the root proves total liabilities, not just membership.
```text
today:    node = H("ProofOfReserve_Branch", left ‖ right)                    → no total
sum tree: node = H("ProofOfReserve_Branch", leftHash ‖ leftSum ‖ rightHash ‖ rightSum)
          nodeSum = leftSum + rightSum

user checks along their path:  parentSum == mySum + siblingSum,  siblingSum >= 0,  rootSum == published total
```
