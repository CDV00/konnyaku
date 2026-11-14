"""Dev only: creates tenant "demo" (host demo.localhost) with test data.

- staff: admin@demo.example.com / Admin-Pass-123 (TenantAdmin), teacher@demo.example.com / Teacher-Pass-123 (Teacher)
- learner: hv000001 with a temporary password (printed), linked to a student who bought a 24+2 lesson package for class JP-N5-01
  (the Worker must be running to turn the paid order into an enrollment)
- an Online branch, a Japanese (JLPT) program with a 24-lesson N5 level, the class scheduled Tue/Thu 19:00

Usage: python infra/scripts/seed_demo.py [learner.json]
"""
import datetime
import json
import sys
import time
import urllib.error
import urllib.request

API = "http://localhost:5080"
PLATFORM = {"X-Platform-Key": "dev-platform-key"}
HOST = {"X-Tenant-Host": "demo.localhost"}


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


def must(result, what):
    status, body = result
    if status >= 400:
        sys.exit(f"{what} failed: {status} {body}")
    return body


for _ in range(60):
    try:
        if call("GET", "/health")[0] == 200:
            break
    except Exception:
        time.sleep(1)

status, tenant = call("POST", "/api/platform/tenants", {
    "slug": "demo", "name": "Trung tâm Demo", "defaultLocale": "vi", "enabledLocales": ["vi", "en"],
    "timeZone": "Asia/Ho_Chi_Minh"}, PLATFORM)
if status == 409:
    sys.exit("tenant 'demo' already exists; reset the dev database first (docker compose ... down -v)")
must((status, tenant), "create tenant")
must(call("POST", "/api/identity/bootstrap/tenant-admin", {
    "tenantId": tenant["id"], "email": "admin@demo.example.com", "displayName": "Quản trị Demo",
    "password": "Admin-Pass-123"}, PLATFORM), "bootstrap admin")

token = must(call("POST", "/api/identity/staff/login",
                  {"email": "admin@demo.example.com", "password": "Admin-Pass-123"}, HOST), "admin login")["accessToken"]
ADMIN = {**HOST, "Authorization": f"Bearer {token}"}

teacher = must(call("POST", "/api/identity/staff", {
    "email": "teacher@demo.example.com", "displayName": "Giáo viên Demo", "initialPassword": "Teacher-Pass-123",
    "roles": ["Teacher"]}, ADMIN), "teacher")
branch = must(call("POST", "/api/organization/branches", {
    "code": "ONLINE", "name": {"vi": "Cơ sở Online", "en": "Online campus"}, "timeZone": "Asia/Ho_Chi_Minh",
    "isVirtual": True}, ADMIN), "branch")
program = must(call("POST", "/api/academic/programs", {
    "code": "JP-COMM", "name": {"vi": "Tiếng Nhật giao tiếp", "en": "Conversational Japanese"},
    "languageCode": "ja", "frameworkCode": "JLPT", "isPublic": True}, ADMIN), "program")
level = must(call("POST", f"/api/academic/programs/{program['id']}/levels", {
    "code": "N5-A", "name": {"vi": "Sơ cấp N5", "en": "Beginner N5"}, "targetLevelCode": "N5",
    "sessionCount": 24, "sessionMinutes": 90}, ADMIN), "level")
must(call("POST", "/api/academic/programs", {
    "code": "EN-KIDS", "name": {"vi": "Tiếng Anh thiếu nhi", "en": "English for kids"},
    "languageCode": "en", "frameworkCode": "CAMBRIDGE_YLE", "isPublic": True}, ADMIN), "program 2")

starts = datetime.date.today() + datetime.timedelta(days=1)
cls = must(call("POST", "/api/academic/classes", {
    "code": "JP-N5-01", "courseLevelId": level["id"], "branchId": branch["id"], "deliveryMode": "Online",
    "capacity": 12, "startsOn": starts.isoformat()}, ADMIN), "class")
sessions = must(call("POST", f"/api/scheduling/classes/{cls['id']}/schedule", {
    "daysOfWeek": [2, 4], "startTime": "19:00", "teacherId": teacher["userId"]}, ADMIN), "schedule")

learner = must(call("POST", "/api/identity/learners", {
    "displayName": "Học viên Test", "role": "Student", "username": "hv000001"}, ADMIN), "learner")
student = must(call("POST", "/api/crm/students", {"fullName": "Học viên Test", "userId": learner["userId"]}, ADMIN), "student")

# Real flow: package -> order for the class -> payment. The Worker turns the paid order into an enrollment.
package = must(call("POST", "/api/billing/packages", {
    "code": "N5-24", "name": {"vi": "Gói N5 24 buổi", "en": "N5 package, 24 lessons"}, "paidCredits": 24,
    "bonusCredits": 2, "price": 4800000, "currency": "VND"}, ADMIN), "package")
order = must(call("POST", "/api/billing/orders", {"studentId": student["id"], "packageId": package["id"], "classId": cls["id"]}, ADMIN), "order")
must(call("POST", f"/api/billing/orders/{order['id']}/payments", {"amount": 4800000, "method": "bank_transfer"}, ADMIN), "payment")
for _ in range(30):
    roster = must(call("GET", f"/api/academic/classes/{cls['id']}/roster", None, ADMIN), "roster")
    if roster:
        break
    time.sleep(1)
else:
    print("warning: no enrollment yet - is the Worker running? (dotnet run --project backend/src/Hosts/Konnyaku.Worker)")

print(f"tenant demo ready: {len(sessions)} sessions scheduled for class JP-N5-01")
print("learner hv000001 temporary password:", learner["temporaryPassword"])
if len(sys.argv) > 1:
    json.dump(learner, open(sys.argv[1], "w", encoding="utf-8"))
