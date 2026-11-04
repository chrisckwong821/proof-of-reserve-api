#!/usr/bin/env python3
"""Run the real publisher, Redis, published API, and independent HTTP checks.

Python is used only for test orchestration and an independent hashlib oracle.
No third-party Python packages are needed. Nothing is written to saved fixtures.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[1]
FIXTURES = REPO / "tests/Reserve.Tests/Fixtures"


def run(*args, capture=False, **kwargs):
    """Run a command with a timeout and fail on a nonzero exit code."""
    return subprocess.run(
        args, check=True, text=True, capture_output=capture, timeout=kwargs.pop("timeout", 120), **kwargs
    )


def free_port():
    """Ask the operating system for an available loopback port."""
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stop(process):
    """Stop a child process gracefully, forcing termination if needed."""
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def wait_for(check, description, seconds=30):
    """Poll until a condition succeeds or the deadline expires."""
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(0.1)
    raise RuntimeError(f"Timed out waiting for {description}")


def tagged(tag, message):
    """Compute BIP340-tagged SHA-256 independently of the C# library."""
    prefix = hashlib.sha256(tag.encode("utf-8")).digest()
    return hashlib.sha256(prefix + prefix + message).digest()


def independent_root(users):
    """Build the expected root using ordered leaves and Bitcoin-style odd-node duplication."""
    nodes = [
        tagged("ProofOfReserve_Leaf", f"({u['id']},{u['balance']})".encode())
        for u in sorted(users, key=lambda u: u["id"])
    ]
    while len(nodes) > 1:
        if len(nodes) % 2:
            nodes.append(nodes[-1])
        nodes = [tagged("ProofOfReserve_Branch", nodes[i] + nodes[i + 1]) for i in range(0, len(nodes), 2)]
    return nodes[0].hex() if nodes else "00" * 32


class Integration:
    def __init__(self, folder, mode, dotnet):
        """Track the isolated services and temporary files owned by this run."""
        self.folder, self.mode, self.dotnet = folder, mode, dotnet
        self.redis = self.api = None
        self.container = "reserve-e2e-" + uuid.uuid4().hex
        self.container_created = False
        self.logs = []
        self.api_count = 0

    def log(self, name):
        """Open a temporary log and track it for cleanup."""
        stream = (self.folder / name).open("w")
        self.logs.append(stream)
        return stream

    def start_redis(self):
        """Start an isolated Redis container or local process and wait for readiness."""
        if self.mode == "docker":
            # Loopback only, an automatically assigned port, and a unique container.
            # No volume or persistence: cleanup cannot affect another Redis instance.
            self.container_created = True
            run(
                "docker",
                "run",
                "--detach",
                "--name",
                self.container,
                "--publish",
                "127.0.0.1::6379",
                "redis:7.4-alpine",
                "redis-server",
                "--save",
                "",
                "--appendonly",
                "no",
                timeout=180,
            )

            def port_ready():
                """Wait for Docker to publish the assigned host port before connecting."""
                address = run("docker", "port", self.container, "6379/tcp", capture=True).stdout.strip()
                if not address:
                    return False
                self.port = int(address.rsplit(":", 1)[1])
                return True

            wait_for(port_ready, "Docker port mapping")
        else:
            self.port = free_port()
            self.redis = subprocess.Popen(
                [
                    "redis-server",
                    "--bind",
                    "127.0.0.1",
                    "--port",
                    str(self.port),
                    "--save",
                    "",
                    "--appendonly",
                    "no",
                    "--dir",
                    str(self.folder),
                ],
                stdout=self.log("redis.log"),
                stderr=subprocess.STDOUT,
            )
        self.connection = f"127.0.0.1:{self.port},abortConnect=false," "connectTimeout=1000,asyncTimeout=1000"

        def ready():
            """Check whether the service is ready to accept requests."""
            try:
                return self.redis_cli("PING") == "PONG"
            except subprocess.CalledProcessError:
                return False

        wait_for(ready, "Redis")
        print(f"Redis ready: {self.mode}, 127.0.0.1:{self.port}", flush=True)

    def redis_cli(self, *args):
        """Run a Redis command against this test instance only."""
        prefix = (
            ["docker", "exec", self.container, "redis-cli"]
            if self.mode == "docker"
            else ["redis-cli", "-h", "127.0.0.1", "-p", str(self.port)]
        )
        return run(*prefix, "--raw", *args, capture=True, timeout=10).stdout.strip()

    def stop_redis(self):
        """Stop this Redis instance to exercise cached service during an outage."""
        if self.mode == "docker":
            run("docker", "stop", "--time", "5", self.container, capture=True, timeout=15)
        else:
            stop(self.redis)

    def seed(self, fixture):
        """Publish a saved fixture through the real Redis seeding tool."""
        path = FIXTURES / fixture
        run(
            self.dotnet,
            str(REPO / "tools/ReserveData/bin/Release/net10.0/ReserveData.dll"),
            "seed-redis",
            str(path),
            self.connection,
        )
        return json.loads(path.read_text())

    def start_api(self, refresh_time=None):
        """Launch the published API as a separate process and wait for its first snapshot."""
        stop(self.api)
        self.api_count += 1
        self.api_log = self.folder / f"api-{self.api_count}.log"
        self.base = f"http://127.0.0.1:{free_port()}"
        command = [
            self.dotnet,
            str(self.folder / "publish/Reserve.Api.dll"),
            "--urls",
            self.base,
            "--Reserve:RedisConnection",
            self.connection,
            "--Logging:LogLevel:Default",
            "Warning",
        ]
        if refresh_time is not None:
            command += ["--Reserve:RefreshTimeUtc", refresh_time]
        self.api = subprocess.Popen(
            command,
            cwd=self.folder / "publish",
            stdout=self.log(self.api_log.name),
            stderr=subprocess.STDOUT,
        )

        def ready():
            """Check whether the service is ready to accept requests."""
            if self.api.poll() is not None:
                raise RuntimeError("API exited during startup")
            try:
                return self.http("/api/reserves/root")[0] == 200
            except (OSError, urllib.error.URLError):
                return False

        wait_for(ready, "API")

    def http(self, path, headers=None):
        """Send an HTTP request and preserve status, headers, and body for assertions."""
        request = urllib.request.Request(self.base + path, headers=headers or {})
        try:
            response = urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, dict(response.headers), response.read()

    def get(self, path):
        """Fetch a successful JSON response or fail the test."""
        status, _, body = self.http(path)
        assert status == 200, (path, status, body)
        return json.loads(body)

    def verify(self, users):
        """Compare the HTTP root and proofs with independently computed hashes and balances."""
        expected = independent_root(users)
        root = self.get("/api/reserves/root")
        assert root["root"] == expected and root["userCount"] == len(users)
        balances = {u["id"]: u["balance"] for u in users}
        ids = [1, 2, 3, 32768, 65535, 99999, 100000] if len(users) > 8 else list(balances)
        for user_id in ids:
            proof = self.get(f"/api/reserves/users/{user_id}/proof")
            assert proof["userId"] == user_id and proof["userBalance"] == balances[user_id]
            node = tagged("ProofOfReserve_Leaf", f"({user_id},{balances[user_id]})".encode())
            for sibling, side in proof["proof"]:
                assert side in (0, 1) and len(sibling) == 64
                sibling = bytes.fromhex(sibling)
                node = tagged("ProofOfReserve_Branch", sibling + node if side == 0 else node + sibling)
            assert node.hex() == proof["root"] == expected
        print(f"PASS: {len(users):,} users; independent root and {len(ids)} HTTP proofs.", flush=True)
        return root

    def cache_checks(self):
        """Check ETags, concurrent cached requests, Redis read counts, and error responses."""
        for path in ["/api/reserves/root", "/api/reserves/users/100000/proof"]:
            status, headers, body = self.http(path)
            assert status == 200 and self.http(path)[2] == body
            status, _, body = self.http(path, {"If-None-Match": headers["ETag"]})
            assert status == 304 and not body

        def reads():
            """Capture Redis data-read counters to detect unexpected database access."""
            return {
                line.split(":", 1)[0]: line.split("calls=")[1].split(",")[0]
                for line in self.redis_cli("INFO", "commandstats").splitlines()
                if line.startswith(("cmdstat_get:", "cmdstat_hscan:", "cmdstat_hgetall:", "cmdstat_hget:"))
            }

        before = reads()
        paths = ["/api/reserves/root", "/api/reserves/users/100000/proof"] * 64
        with ThreadPoolExecutor(max_workers=16) as pool:
            list(pool.map(self.get, paths))
        assert reads() == before
        assert self.http("/api/reserves/users/0/proof")[0] == 400
        assert self.http("/api/reserves/users/100001/proof")[0] == 404
        assert self.http("/api/reserves/users/1/proof?snapshotId=outdated")[0] == 409
        print("PASS: ETags, 128 requests / 16 clients with no Redis data rereads, and error statuses.", flush=True)

    def close(self):
        """Stop owned processes and remove the test container before closing logs."""
        stop(self.api)
        stop(self.redis)
        try:
            if self.container_created:
                run("docker", "rm", "--force", "--volumes", self.container, capture=True, timeout=30)
        finally:
            for stream in self.logs:
                stream.close()

    def diagnostics(self):
        """Print recent service logs to help diagnose a failed run."""
        for path in self.folder.glob("*.log"):
            print(f"\n{path.name} (last 30 lines):\n" + "\n".join(path.read_text().splitlines()[-30:]))
        if self.container_created:
            subprocess.run(["docker", "logs", "--tail", "30", self.container], timeout=15)


def main():
    """Build and run the complete integration flow, cleaning up on success or failure."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--redis", choices=["docker", "local"], default="docker")
    parser.add_argument("--stress", action="store_true", help="Also load-test both APIs using the 100k fixture.")
    parser.add_argument("--requests", type=int, default=10000, help="Requests per stress-test case (default: 10000).")
    parser.add_argument("--clients", type=int, default=32, help="Concurrent stress-test clients (default: 32).")
    args = parser.parse_args()
    if not 1 <= args.requests <= 99000 or not 1 <= args.clients <= min(args.requests, 256):
        parser.error("Use 1..99000 requests and 1..256 clients, with clients no greater than requests.")
    if not __debug__:
        parser.error("Run Python without -O: assertions must be enabled.")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        parser.error("Install .NET 10 SDK and add dotnet to PATH.")
    if args.redis == "docker":
        if not shutil.which("docker"):
            parser.error("Docker is not installed; install Docker Desktop or use --redis local.")
        try:
            run("docker", "info", capture=True, timeout=15)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            parser.error("Docker engine is unavailable. Start Docker Desktop, then retry (or use --redis local).")
    elif not all(shutil.which(tool) for tool in ["redis-server", "redis-cli"]):
        parser.error("Local mode requires redis-server and redis-cli on PATH.")

    run("git", "submodule", "update", "--init", "--recursive", cwd=REPO)
    run(dotnet, "restore", "--locked-mode", cwd=REPO)
    run(dotnet, "build", "-c", "Release", "--no-restore", "--disable-build-servers", cwd=REPO)
    with tempfile.TemporaryDirectory(prefix="reserve-integration-") as temporary:
        test = Integration(Path(temporary), args.redis, dotnet)
        try:
            run(
                dotnet,
                "publish",
                "src/Reserve.Api",
                "-c",
                "Release",
                "--no-build",
                "--no-restore",
                "-o",
                str(test.folder / "publish"),
                cwd=REPO,
            )
            for fixture in FIXTURES.glob("*.json"):
                assert not list((test.folder / "publish").rglob(fixture.name))
            test.start_redis()
            run(
                dotnet,
                "test",
                "tests/Reserve.Tests",
                "-c",
                "Release",
                "--no-build",
                "--logger",
                "console;verbosity=normal",
                cwd=REPO,
                env=dict(os.environ, RESERVE_TEST_REDIS=test.connection),
            )
            assert test.redis_cli("DBSIZE") == "0", "Integration tests left Redis keys behind"
            users = test.seed("users-100000.json")
            test.start_api()
            test.verify(users)
            test.cache_checks()
            if args.stress:
                from stress import run_stress

                run_stress(test, users, args.requests, args.clients)

            # Exercise the same daily scheduler at a UTC time a few seconds ahead.
            refresh_time = (datetime.now(timezone.utc) + timedelta(seconds=8)).strftime("%H:%M:%S")
            test.start_api(refresh_time)
            previous = test.get("/api/reserves/root")["snapshotId"]
            users = test.seed("users-example.json")
            wait_for(lambda: test.get("/api/reserves/root")["userCount"] == len(users), "scheduled snapshot refresh")
            test.verify(users)
            assert test.http(f"/api/reserves/users/8/proof?snapshotId={previous}")[0] == 409

            # Restart with a nearby daily deadline so the outage test does not wait until tomorrow.
            refresh_time = (datetime.now(timezone.utc) + timedelta(seconds=5)).strftime("%H:%M:%S")
            test.start_api(refresh_time)
            test.stop_redis()
            wait_for(lambda: "Refresh failed; keeping snapshot" in test.api_log.read_text(), "failed refresh")
            test.verify(users)
            print(
                "PASS: automatic refresh, stale snapshot rejection, and cached service during Redis outage.", flush=True
            )
        except BaseException:
            test.diagnostics()
            raise
        finally:
            test.close()
    print("PASS: end-to-end integration complete; temporary processes, container, and files removed.", flush=True)


if __name__ == "__main__":
    main()
