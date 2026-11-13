import json, os, pathlib, subprocess, tempfile, time, urllib.request, urllib.error, uuid
import psycopg2

root = pathlib.Path(__file__).resolve().parents[3]

def psql(database, sql):
    return subprocess.run(["psql", "-d", database, "-Atc", sql], capture_output=True, text=True, check=True).stdout.strip()

# a throwaway database: this smoke run registers players and rewrites passwords, never the live one.
database = "xc_test_" + uuid.uuid4().hex
subprocess.run(["createdb", "-T", "template0", "-E", "UTF8", "--lc-collate=C", "--lc-ctype=C", database], check=True)
try:
    with tempfile.TemporaryDirectory(prefix="xc-http-test-") as directory:
        env = dict(os.environ, DataDirectory=directory, Urls="http://127.0.0.1:5189", AccountMail__Enabled="false",
                   Database__ConnectionString="Host=/var/run/postgresql;Database=" + database)
        with open(directory + "/server.log", "w") as log:
            server = subprocess.Popen(["/home/admin/.dotnet/dotnet", str(root / "server/XcOsu.Server/bin/Debug/net10.0/xc-osu-server.dll")], env=env, stdout=log, stderr=log)
            try:
                def req(path, body=None, token=None, method=None):
                    headers = {"Content-Type": "application/json"}
                    if token: headers["Authorization"] = "Bearer " + token
                    request = urllib.request.Request("http://127.0.0.1:5189" + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
                    try:
                        with urllib.request.urlopen(request, timeout=10) as response:
                            data = response.read()
                            return response.status, json.loads(data) if data else None
                    except urllib.error.HTTPError as error: return error.code, None
                for attempt in range(50):
                    if server.poll() is not None: raise RuntimeError(pathlib.Path(directory + "/server.log").read_text()[-3000:])
                    try:
                        if req("/api/xc/health")[0] == 200: break
                    except OSError: time.sleep(.2)
                assert req("/users", {"user[username]":"SmokePlayer", "user[user_email]":"smoke@example.invalid", "user[password]":"a-good-password-123"})[0] == 200
                status, tokens = req("/oauth/token", {"grant_type":"password","username":"SmokePlayer","password":"a-good-password-123"})
                assert status == 200
                access = tokens["access_token"]
                assert req("/api/xc/account", token=access)[0] == 200
                assert req("/api/xc/account?access_token=" + access)[0] == 401
                assert req("/api/xc/account/password", {"current_password":"a-good-password-123", "new_password":"different-password-123"}, access)[0] == 204
                assert req("/api/xc/account", token=access)[0] == 401
                assert req("/oauth/token", {"grant_type":"refresh_token","refresh_token":tokens["refresh_token"]})[0] == 401
                status, tokens = req("/oauth/token", {"grant_type":"password","username":"SmokePlayer","password":"different-password-123"})
                assert status == 200
                assert req("/api/xc/account", token=tokens["access_token"])[0] == 200
                assert req("/api/v2/session/verify", {})[0] == 501
                assert req("/api/xc/proxy?u=https%3A%2F%2Fexample.invalid")[0] == 403
                browser_request = urllib.request.Request("http://127.0.0.1:5189/oauth/token",
                    data=json.dumps({"grant_type":"password", "username":"SmokePlayer", "password":"different-password-123", "browser_session":"1"}).encode(),
                    headers={"Content-Type":"application/json", "Origin":"http://127.0.0.1:5189"})
                with urllib.request.urlopen(browser_request) as response:
                    result = json.load(response)
                    cookies = response.headers.get_all("Set-Cookie")
                    assert result.get("browser_session") and "access_token" not in result and "refresh_token" not in result
                    assert len(cookies) == 2 and all("httponly" in c.lower() and "secure" in c.lower() and "samesite=strict" in c.lower() for c in cookies)
                    cookie_header = "; ".join(c.split(";")[0] for c in cookies)
                cookie_request = urllib.request.Request("http://127.0.0.1:5189/api/xc/account", headers={"Cookie":cookie_header})
                with urllib.request.urlopen(cookie_request) as response: assert response.status == 200
                cross_request = urllib.request.Request("http://127.0.0.1:5189/api/xc/account/password",
                    data=b'{}', headers={"Cookie":cookie_header,"Origin":"https://attacker.invalid","Content-Type":"application/json"})
                try: urllib.request.urlopen(cross_request); raise AssertionError("Cross-origin cookie authentication accepted")
                except urllib.error.HTTPError as error: assert error.code == 401
                assert req("/api/xc/tm", {})[0] == 401
                with urllib.request.urlopen("http://127.0.0.1:5189/api/xc/tm/session") as response:
                    assert response.status == 204
                    telemetry_cookie = response.headers["Set-Cookie"].split(";")[0]
                for origin, cookie, expected in [("https://attacker.invalid", telemetry_cookie, 401),
                    ("http://127.0.0.1:5189", telemetry_cookie + "x", 401),
                    ("http://127.0.0.1:5189", telemetry_cookie, 400)]:
                    request = urllib.request.Request("http://127.0.0.1:5189/api/xc/tm", data=b'{}',
                        headers={"Cookie":cookie, "Origin":origin, "Content-Type":"text/plain"})
                    try: urllib.request.urlopen(request); raise AssertionError("Invalid telemetry accepted")
                    except urllib.error.HTTPError as error: assert error.code == expected, error.code
                print("PASS telemetry session required, tampering and cross-origin rejected, valid session reaches payload validation")
                print("PASS browser HttpOnly cookies, no tokens in response, cookie authentication, cross-origin rejection")
                with psycopg2.connect(host="/var/run/postgresql", dbname=database) as connection:
                    with connection.cursor() as cursor:
                        cursor.execute("SELECT id FROM users WHERE username = %s", ("SmokePlayer",))
                        account_id = cursor.fetchone()[0]
                result = subprocess.run(["/home/admin/.dotnet/dotnet", str(root / "server/XcOsu.Server/bin/Debug/net10.0/xc-osu-server.dll"), "grant-admin", str(account_id)], env=env, capture_output=True, timeout=20)
                assert result.returncode == 0, result.stderr.decode()[-1000:]
                assert psql(database, "SELECT is_admin FROM users WHERE id = %d" % account_id) == "1"
                print("PASS local admin provisioning command")
                print("PASS HTTP registration, login, bearer auth, query rejection, password change, session revocation, relogin, 2FA unavailable, proxy allowlist")
            finally:
                server.terminate()
                try: server.wait(timeout=10)
                except subprocess.TimeoutExpired: server.kill(); server.wait()
finally:
    subprocess.run(["dropdb", "--if-exists", "-f", database], check=True)
