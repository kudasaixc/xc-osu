#!/usr/bin/env python3
# xc-osu! load test: hundreds of simultaneous clients against the PostgreSQL-backed server.
# Usage: load_test.py [--base http://127.0.0.1:5081] [--connections 300] [--seconds 30] [--mode read|write|mixed]
import argparse, http.client, json, random, statistics, threading, time
from urllib.parse import urlsplit

parser = argparse.ArgumentParser()
parser.add_argument("--base", default="http://127.0.0.1:5081")
parser.add_argument("--connections", type=int, default=300)
parser.add_argument("--seconds", type=int, default=30)
parser.add_argument("--mode", choices=["read", "write", "mixed"], default="read")
args = parser.parse_args()
url = urlsplit(args.base)
host = url.hostname
port = url.port or (443 if url.scheme == "https" else 80)

READ_PATHS = [
    "/api/xc/health",
    "/api/xc/teams",
    "/api/xc/menu-content",
    "/api/v2/rankings/osu/performance",
    "/api/v2/rankings/taiko/score?page=2",
    "/api/v2/beatmapsets/search?q=test&m=0",
    "/api/v2/users/1/scores/best",
    "/api/v2/users/1/recent_activity",
    "/api/v2/changelog",
    "/api/v2/news",
]

def call(conn, method, path, body=None, token=None, client_ip=None):
    headers = {"Accept": "application/json"}
    # Apache fronts the server, so each simulated client carries its own forwarded address: the per-address
    # abuse budgets then behave as they do for a campus of real players instead of collapsing into one loopback.
    if client_ip: headers["X-Forwarded-For"] = client_ip
    if body is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(body).encode()
    else:
        data = None
    if token: headers["Authorization"] = "Bearer " + token
    started = time.perf_counter()
    conn.request(method, path, body=data, headers=headers)
    response = conn.getresponse()
    payload = response.read()
    elapsed = (time.perf_counter() - started) * 1000
    return response.status, payload, elapsed

def new_connection():
    c = http.client.HTTPConnection(host, port, timeout=30)
    c.connect()
    return c

# ---- write mode needs accounts ----
accounts = []
if args.mode in ("write", "mixed"):
    import uuid
    bootstrap = new_connection()
    for i in range(min(args.connections, 120)):
        name = "load_" + uuid.uuid4().hex[:12]
        status, _, _ = call(bootstrap, "POST", "/users", {
            "user[username]": name, "user[user_email]": name + "@example.invalid", "user[password]": "a-good-password-123"},
            client_ip=f"10.43.{i // 250}.{i % 250 + 1}")
        if status == 200: accounts.append(name)
    bootstrap.close()
    print(f"prepared {len(accounts)} accounts")

latencies, statuses, errors, per_worker = [], {}, [], []
lock = threading.Lock()
deadline = time.monotonic()
start_barrier = threading.Barrier(args.connections + 1)

def worker(index):
    client = f"10.42.{index // 250}.{index % 250 + 1}"
    conn = None
    mine = []
    local_status, local_errors = {}, []
    try:
        conn = new_connection()
        start_barrier.wait()
        while time.monotonic() < deadline:
            if args.mode == "read" or (args.mode == "mixed" and index % 4 != 0):
                path = READ_PATHS[(index + len(mine)) % len(READ_PATHS)]
                status, _, ms = call(conn, "GET", path, client_ip=client)
            else:
                path = "/oauth/token"
                name = accounts[index % len(accounts)]
                status, _, ms = call(conn, "POST", path,
                                     {"grant_type": "password", "username": name, "password": "a-good-password-123"},
                                     client_ip=client)
            mine.append(ms)
            local_status[status] = local_status.get(status, 0) + 1
            if status >= 500 or status in (401, 403) and not path.startswith("/api/xc/account"):
                local_errors.append(f"{status} {path}")
    except Exception as e:
        local_errors.append(type(e).__name__ + ": " + str(e)[:120])
    finally:
        try:
            if conn: conn.close()
        except Exception: pass
        with lock:
            latencies.extend(mine); per_worker.append(len(mine))
            for k, v in local_status.items(): statuses[k] = statuses.get(k, 0) + v
            errors.extend(local_errors)

threads = [threading.Thread(target=worker, args=(i,)) for i in range(args.connections)]
for t in threads: t.start()
deadline = time.monotonic() + args.seconds
start_barrier.wait()
print(f"{args.connections} connections, {args.seconds}s, mode={args.mode} ...")
for t in threads: t.join()

latencies.sort()
def q(p): return latencies[min(len(latencies) - 1, int(len(latencies) * p))] if latencies else 0
total = len(latencies)
print(json.dumps({
    "requests": total,
    "per_second": round(total / args.seconds, 1),
    "connections": args.connections,
    "requests_per_connection": round(total / max(1, args.connections), 1),
    "latency_ms": {"p50": round(q(.50), 1), "p95": round(q(.95), 1), "p99": round(q(.99), 1),
                   "max": round(latencies[-1], 1) if latencies else 0, "mean": round(statistics.mean(latencies), 1) if latencies else 0},
    "status": statuses,
    "errors": len(errors),
    "error_samples": errors[:8],
}, indent=2))
