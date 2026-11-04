"""Small HTTP load test using persistent connections and Python's standard library."""

from concurrent.futures import ThreadPoolExecutor
import http.client
import json
import math
from threading import Barrier
import time
from urllib.parse import urlsplit


def percentile(samples, percentage):
    """Return a nearest-rank percentile from sorted latency samples."""
    return samples[max(0, math.ceil(len(samples) * percentage / 100) - 1)]


def exercise(base, paths, expected_root, balances, clients):
    """Run a bounded batch with one reusable connection per client and validate every response."""
    address = urlsplit(base)
    ready = Barrier(clients + 1)

    def worker(client_id):
        """Send this client's requests sequentially, recording latency and response errors."""
        connection = http.client.HTTPConnection(address.hostname, address.port, timeout=10)
        latencies, errors = [], []
        ready.wait()
        deadline = time.perf_counter() + 120
        try:
            for index in range(client_id, len(paths), clients):
                if time.perf_counter() >= deadline:
                    break
                path, user_id = paths[index]
                started = time.perf_counter()
                try:
                    connection.request("GET", path)
                    response = connection.getresponse()
                    body = response.read()
                    latencies.append((time.perf_counter() - started) * 1000)
                    data = json.loads(body)
                    valid = response.status == 200 and data.get("root") == expected_root
                    if user_id is None:
                        valid = valid and data.get("userCount") == len(balances)
                    else:
                        valid = (
                            valid
                            and data.get("userId") == user_id
                            and data.get("userBalance") == balances[user_id]
                            and len(data.get("proof", [])) == 17
                        )
                    if not valid:
                        errors.append(f"Unexpected HTTP status or body for {path}: {response.status}")
                except (OSError, http.client.HTTPException) as error:
                    latencies.append((time.perf_counter() - started) * 1000)
                    errors.append(str(error))
                    break
                except (ValueError, TypeError, AttributeError) as error:
                    errors.append(f"Invalid JSON response: {error}")
        finally:
            connection.close()
        return latencies, errors

    with ThreadPoolExecutor(max_workers=clients) as pool:
        futures = [pool.submit(worker, client) for client in range(clients)]
        started = time.perf_counter()
        ready.wait()
        results = [future.result() for future in futures]
        elapsed = time.perf_counter() - started

    samples = sorted(latency for latencies, _ in results for latency in latencies)
    errors = [error for _, failures in results for error in failures]
    skipped = len(paths) - len(samples)
    return {
        "requests": len(samples),
        "seconds": elapsed,
        "rps": len(samples) / elapsed,
        "p50": percentile(samples, 50) if samples else 0,
        "p95": percentile(samples, 95) if samples else 0,
        "errors": len(errors),
        "skipped": skipped,
        "first_error": errors[0] if errors else "",
    }


def run_stress(test, users, requests, clients):
    """Measure both endpoints on the 100k fixture before the integration run changes its data."""
    balances = {user["id"]: user["balance"] for user in users}
    root = test.get("/api/reserves/root")["root"]
    # Warm the root and 100 proof entries outside the measured batches.
    for user_id in range(1, 101):
        test.get("/api/reserves/root")
        test.get(f"/api/reserves/users/{user_id}/proof")

    # Earlier correctness checks requested these IDs; exclude them from the uncached case.
    cold_users = [user_id for user_id in range(101, 100001) if user_id not in {32768, 65535, 99999, 100000}]
    cases = [
        ("Root (cached)", [("/api/reserves/root", None)] * requests),
        ("Proof (cached)", [(f"/api/reserves/users/{i % 100 + 1}/proof", i % 100 + 1) for i in range(requests)]),
        (
            "Proof (distinct users)",
            [(f"/api/reserves/users/{user_id}/proof", user_id) for user_id in cold_users[:requests]],
        ),
    ]
    print(f"\nStress test: 100,000 users; {requests:,} requests per case; {clients} concurrent clients.", flush=True)
    print("Client and API share one machine; these are local measurements, not a maximum-capacity claim.", flush=True)
    print("| Case | Requests | Seconds | Requests/s | Median ms | p95 ms | Errors | Skipped |", flush=True)
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |", flush=True)
    failed = []
    for name, paths in cases:
        result = exercise(test.base, paths, root, balances, clients)
        print(
            f"| {name} | {result['requests']} | {result['seconds']:.2f} | {result['rps']:.0f} | "
            f"{result['p50']:.2f} | {result['p95']:.2f} | {result['errors']} | {result['skipped']} |",
            flush=True,
        )
        if result["errors"] or result["skipped"]:
            failed.append(f"{name}: {result['first_error']} ({result['skipped']} skipped)")
    if failed:
        raise RuntimeError("Stress test failed: " + "; ".join(failed))
    print("PASS: all stress-test responses returned HTTP 200 with the expected data.\n", flush=True)
