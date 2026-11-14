"""Dev only: creates tenant "demo" (host demo.localhost), an admin and a learner account.
Usage: python infra/scripts/seed_demo.py [output.json]  (prints the learner temporary password)"""
import json, sys, time, urllib.request, urllib.error

API = "http://localhost:5080"
KEY = {"X-Platform-Key": "dev-platform-key"}


def call(method, path, body=None, headers=None):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"Content-Type": "application/json; charset=utf-8", **(headers or {})})
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read().decode("utf-8")
            return r.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8")
        return e.code, (json.loads(text) if text else None)


for _ in range(60):
    try:
        if call("GET", "/health")[0] == 200:
            break
    except Exception:
        time.sleep(1)

status, bad = call("POST", "/api/platform/tenants", None, KEY)
print("empty body ->", status)

status, tenant = call("POST", "/api/platform/tenants", {
    "slug": "demo", "name": "Trung tâm Demo", "defaultLocale": "vi", "enabledLocales": ["vi", "en"],
    "timeZone": "Asia/Ho_Chi_Minh"}, KEY)
print("tenant", status, tenant.get("hosts") if isinstance(tenant, dict) else tenant)
if status == 409:
    sys.exit("tenant exists; reset the dev DB first")
status, _ = call("POST", "/api/identity/bootstrap/tenant-admin", {
    "tenantId": tenant["id"], "email": "admin@demo.example.com", "displayName": "Quản trị Demo",
    "password": "Admin-Pass-123"}, KEY)
print("admin", status)
host = {"X-Tenant-Host": "demo.localhost"}
status, signin = call("POST", "/api/identity/staff/login", {"email": "admin@demo.example.com", "password": "Admin-Pass-123"}, host)
token = signin["accessToken"]
status, learner = call("POST", "/api/identity/learners", {"displayName": "Học viên Test", "role": "Student", "username": "hv000001"},
                       {**host, "Authorization": f"Bearer {token}"})
print("learner", status, learner["username"])
print("learner temporary password:", learner["temporaryPassword"])
if len(sys.argv) > 1:
    json.dump(learner, open(sys.argv[1], "w", encoding="utf-8"))
