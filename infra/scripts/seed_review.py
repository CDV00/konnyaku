"""Dev/staging only: a full dataset for reviewing every screen and every role.

Creates tenant "review" (host review.<base domain>, e.g. http://review.localhost:4000) with:
- staff for every role, one shared password (STAFF_PASSWORD below):
    admin@, manager@, coordinator@, consultant@, accountant@, accountant.hn@ (all @review.example.com)
    teacher.ja@, teacher.en@, teacher.zh@ (Teacher, hourly rates set 90 days back)
- learners hv-review01..hv-review12 (Student) and ph-review01 (Parent), password LEARNER_PASSWORD, no forced change
- 3 branches (Online, Q1 HCM with 2 rooms, Ha Noi with 1 room), a branch holiday, Fake meeting provider with 3 hosts
- 4 programs (Japanese JLPT, English CEFR, Chinese HSK, Korean TOPIK), 5 classes:
    JP-N5-ON1  online, started 4 weeks ago   -> past sessions held on the Fake provider, attendance confirmed
    EN-A2-Q1   offline Q1 room, started 3 weeks ago -> past attendance taken by the teacher
    ZH-HSK1-HN offline Ha Noi, starts next week
    JP-N4-ON2  online, starts in 2 weeks, capacity 2 and full -> a third paid order waits for a refund
    KO-T1-ON3  online, no schedule yet (for the coordinator to schedule)
- packages, orders (paid, partially paid, unpaid, one refunded, one waiting for a refund), leads in every status
- timesheets from the held sessions, last month's payroll period locked, this month's open
- the platform super-admin (console at http://admin.<base domain>:4000/platform), when none exists yet:
    OPERATOR_EMAIL / OPERATOR_PASSWORD, TOTP secret OPERATOR_TOTP_SECRET (add it to an authenticator app,
    or print the current code with: python infra/scripts/seed_review.py --totp)
- two-step sign-in (TOTP) turned on for accountant@: the script prints its secret key (new on every run); add it to
  an authenticator app or print the current code with: python infra/scripts/seed_review.py --totp <secret>

Every API call goes to KONNYAKU_API (default http://localhost:5080) with the platform key KONNYAKU_PLATFORM_KEY
(default dev-platform-key). The Worker must be running: enrollments, rooms, attendance and timesheets come from events.

Usage: python infra/scripts/seed_review.py [--base-domain localhost]
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = os.environ.get("KONNYAKU_API", "http://localhost:5080")
PLATFORM_KEY = os.environ.get("KONNYAKU_PLATFORM_KEY", "dev-platform-key")
SLUG = "review"
STAFF_PASSWORD = "Review-Pass-123"
LEARNER_PASSWORD = "Learner-Pass-123"
TZ = "Asia/Ho_Chi_Minh"
TODAY = dt.date.today()
# Development only: a fixed TOTP secret so every developer can sign in to the platform console.
OPERATOR_EMAIL = "superadmin@platform.example.com"
OPERATOR_PASSWORD = "Platform-Pass-123"
OPERATOR_TOTP_SECRET = "KONNYAKUDEVPLATFORMTOTP234567ABC"


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
        try:
            return e.code, (json.loads(text) if text else None)
        except json.JSONDecodeError:
            return e.code, text


def solve_captcha(headers):
    """Solves the public-form proof of work like the browser does (None when the API has the captcha switched off)."""
    status, challenge = call("GET", "/api/crm/public/captcha", None, headers)
    if status == 204:
        return None
    if status != 200:
        sys.exit(f"captcha challenge failed: {status} {challenge}")
    for n in range(challenge["maxNumber"] + 1):
        if hashlib.sha256(f"{challenge['salt']}{n}".encode()).hexdigest() == challenge["challenge"]:
            return {"salt": challenge["salt"], "challenge": challenge["challenge"], "signature": challenge["signature"], "number": n}
    sys.exit("captcha challenge has no solution")


def must(result, what):
    status, body = result
    if status >= 400:
        sys.exit(f"{what} failed: {status} {body}")
    return body


def wait_for(what, check, seconds=60):
    """Polls until check() returns a truthy value; events are handled asynchronously by the Worker."""
    for _ in range(seconds):
        value = check()
        if value:
            return value
        time.sleep(1)
    sys.exit(f"timed out waiting for {what} - is the Worker running?")


def totp(secret, at=None):
    """Current RFC 6238 code (SHA-1, 30 s, 6 digits) for a base32 secret, as an authenticator app shows it."""
    import base64
    import hashlib
    import hmac
    import struct
    key = base64.b32decode(secret + "=" * (-len(secret) % 8))
    counter = struct.pack(">Q", int((at or time.time()) // 30))
    digest = hmac.new(key, counter, hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    return f"{(struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000:06d}"


def last_weekday_on_or_before(day, weekday):
    return day - dt.timedelta(days=(day.weekday() - weekday) % 7)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-domain", default="localhost", help="Platform:BaseDomain of the API (default localhost)")
    parser.add_argument("--totp", nargs="?", const=OPERATOR_TOTP_SECRET, metavar="SECRET",
                        help="only print the current TOTP code of a secret (default: the dev super-admin's)")
    args = parser.parse_args()
    if args.totp:
        print(totp(args.totp))
        return
    host = {"X-Tenant-Host": f"{SLUG}.{args.base_domain}"}
    platform = {"X-Platform-Key": PLATFORM_KEY}

    wait_for("the API", lambda: _healthy())

    # --- platform super-admin (single-use bootstrap: skipped when an operator already exists) -------------------
    status, body = call("POST", "/api/platform/operators/bootstrap", {
        "email": OPERATOR_EMAIL, "displayName": "Quản trị nền tảng", "password": OPERATOR_PASSWORD,
        "totpSecret": OPERATOR_TOTP_SECRET}, platform)
    if status >= 400 and not (isinstance(body, dict) and body.get("code") == "platform.bootstrap_done"):
        sys.exit(f"bootstrap platform operator failed: {status} {body}")

    # --- tenant and first admin -------------------------------------------------------------------------------
    status, tenant = call("POST", "/api/platform/tenants", {
        "slug": SLUG, "name": "Trung tâm Ngoại ngữ Review", "defaultLocale": "vi", "enabledLocales": ["vi", "en"],
        "timeZone": TZ}, platform)
    if status == 409:
        sys.exit(f"tenant '{SLUG}' already exists; reset the database first (docker compose ... down -v)")
    must((status, tenant), "create tenant")
    must(call("POST", "/api/identity/bootstrap/tenant-admin", {
        "tenantId": tenant["id"], "email": "admin@review.example.com", "displayName": "Quản trị Review",
        "password": STAFF_PASSWORD}, platform), "bootstrap admin")

    def staff_token(email):
        return must(call("POST", "/api/identity/staff/login", {"email": email, "password": STAFF_PASSWORD}, host),
                    f"login {email}")["accessToken"]

    admin = {**host, "Authorization": f"Bearer {staff_token('admin@review.example.com')}"}

    def post(path, body, what, as_=None):
        return must(call("POST", path, body, as_ or admin), what)

    def get(path, what, as_=None):
        return must(call("GET", path, None, as_ or admin), what)

    # --- meeting engine ------------------------------------------------------------------------------------------
    must(call("PUT", "/api/workspace/connection", {"provider": "Fake"}, admin), "workspace connection")
    for i in range(1, 4):
        post("/api/workspace/hosts", {"email": f"host{i:02d}@review.example.com"}, "host")

    # --- organization ----------------------------------------------------------------------------------------------
    online = post("/api/organization/branches", {
        "code": "ONLINE", "name": {"vi": "Cơ sở Online", "en": "Online campus"}, "timeZone": TZ, "isVirtual": True}, "branch")
    q1 = post("/api/organization/branches", {
        "code": "HCM-Q1", "name": {"vi": "Cơ sở Quận 1", "en": "District 1 campus"}, "timeZone": TZ, "isVirtual": False}, "branch")
    hn = post("/api/organization/branches", {
        "code": "HN-CG", "name": {"vi": "Cơ sở Cầu Giấy", "en": "Cau Giay campus"}, "timeZone": TZ, "isVirtual": False}, "branch")
    room_q1 = post(f"/api/organization/branches/{q1['id']}/rooms", {"code": "P101", "capacity": 12}, "room")
    post(f"/api/organization/branches/{q1['id']}/rooms", {"code": "P102", "capacity": 8}, "room")
    room_hn = post(f"/api/organization/branches/{hn['id']}/rooms", {"code": "P201", "capacity": 10}, "room")
    post("/api/organization/holidays", {
        "date": (TODAY + dt.timedelta(days=45)).isoformat(), "name": {"vi": "Nghỉ bảo trì cơ sở", "en": "Campus maintenance"},
        "branchId": q1["id"]}, "holiday")

    # --- staff -------------------------------------------------------------------------------------------------------
    staff = {}
    for email, name, roles, *branches in [
        ("manager@review.example.com", "Quản lý cơ sở", ["BranchManager"]),
        ("coordinator@review.example.com", "Điều phối viên", ["Coordinator"]),
        ("consultant@review.example.com", "Tư vấn viên", ["Consultant"]),
        ("accountant@review.example.com", "Kế toán", ["Accountant"]),
        ("teacher.ja@review.example.com", "GV tiếng Nhật", ["Teacher"]),
        ("teacher.en@review.example.com", "GV tiếng Anh", ["Teacher"]),
        ("teacher.zh@review.example.com", "GV tiếng Trung", ["Teacher"]),
        # Limited to Cầu Giấy: sees every order but collects and refunds only those of that branch.
        ("accountant.hn@review.example.com", "Kế toán Cầu Giấy", ["Accountant"], [hn["id"]]),
    ]:
        staff[email] = post("/api/identity/staff", {
            "email": email, "displayName": name, "initialPassword": STAFF_PASSWORD, "roles": roles,
            "branchIds": branches[0] if branches else []}, f"staff {email}")
    teachers = {lang: staff[f"teacher.{lang}@review.example.com"]["userId"] for lang in ("ja", "en", "zh")}
    for lang, rate in (("ja", 300000), ("en", 350000), ("zh", 280000)):
        must(call("PUT", f"/api/hr/teachers/{teachers[lang]}/rate", {
            "hourlyRate": rate, "currency": "VND", "effectiveFrom": (TODAY - dt.timedelta(days=90)).isoformat()}, admin),
            "teacher rate")

    # --- programs and levels --------------------------------------------------------------------------------------
    def program(code, vi, en, language, framework, levels):
        p = post("/api/academic/programs", {
            "code": code, "name": {"vi": vi, "en": en}, "languageCode": language, "frameworkCode": framework,
            "isPublic": True}, f"program {code}")
        made = {}
        for level_code, target, name_vi, name_en, count in levels:
            made[level_code] = post(f"/api/academic/programs/{p['id']}/levels", {
                "code": level_code, "name": {"vi": name_vi, "en": name_en}, "targetLevelCode": target,
                "sessionCount": count, "sessionMinutes": 90}, f"level {level_code}")
        return made

    jp = program("JP-COMM", "Tiếng Nhật giao tiếp", "Conversational Japanese", "ja", "JLPT",
                 [("N5-A", "N5", "Sơ cấp N5", "Beginner N5", 24), ("N4-A", "N4", "Sơ cấp N4", "Elementary N4", 24)])
    en = program("EN-GEN", "Tiếng Anh tổng quát", "General English", "en", "CEFR",
                 [("A2-A", "A2", "Tiền trung cấp A2", "Elementary A2", 30)])
    zh = program("ZH-HSK", "Tiếng Trung HSK", "Chinese HSK", "zh-Hans", "HSK",
                 [("HSK1-A", "HSK1", "HSK 1", "HSK 1", 20)])
    ko = program("KO-TOPIK", "Tiếng Hàn TOPIK", "Korean TOPIK", "ko", "TOPIK",
                 [("T1-A", "TOPIK1", "TOPIK I", "TOPIK I", 20)])

    # --- classes and schedules --------------------------------------------------------------------------------------
    def klass(code, level, branch, mode, capacity, starts):
        return post("/api/academic/classes", {
            "code": code, "courseLevelId": level["id"], "branchId": branch["id"], "deliveryMode": mode,
            "capacity": capacity, "startsOn": starts.isoformat()}, f"class {code}")

    def schedule(cls, days, start, teacher, room=None):
        return post(f"/api/scheduling/classes/{cls['id']}/schedule", {
            "daysOfWeek": days, "startTime": start, "teacherId": teacher, "roomId": room}, f"schedule {cls['code']}")

    jp_on1 = klass("JP-N5-ON1", jp["N5-A"], online, "Online", 12, last_weekday_on_or_before(TODAY - dt.timedelta(days=28), 1))
    en_q1 = klass("EN-A2-Q1", en["A2-A"], q1, "Offline", 10, last_weekday_on_or_before(TODAY - dt.timedelta(days=21), 0))
    zh_hn = klass("ZH-HSK1-HN", zh["HSK1-A"], hn, "Offline", 10, TODAY + dt.timedelta(days=7))
    jp_on2 = klass("JP-N4-ON2", jp["N4-A"], online, "Online", 2, TODAY + dt.timedelta(days=14))
    klass("KO-T1-ON3", ko["T1-A"], online, "Online", 12, TODAY + dt.timedelta(days=21))

    # Days: 1 = Monday ... 7 = Sunday (same convention as the schedule API).
    jp_sessions = schedule(jp_on1, [2, 4], "19:00", teachers["ja"])
    en_sessions = schedule(en_q1, [1, 3, 5], "18:00", teachers["en"], room_q1["id"])
    schedule(zh_hn, [6], "09:00", teachers["zh"], room_hn["id"])
    schedule(jp_on2, [3, 6], "20:00", teachers["ja"])

    # --- learners -----------------------------------------------------------------------------------------------------
    first_names = ["An", "Bình", "Chi", "Dũng", "Giang", "Hà", "Khoa", "Lan", "Minh", "Nga", "Phúc", "Quân"]
    learners = []
    for i, first in enumerate(first_names, start=1):
        account = post("/api/identity/learners", {
            "displayName": f"{first} (demo)", "role": "Student", "username": f"hv-review{i:02d}"}, "learner")
        _set_known_password(host, account["username"], account["temporaryPassword"])
        branch = [online, q1, hn][i % 3]
        student = post("/api/crm/students", {
            "fullName": f"{first} (demo)", "birthYear": 2000 + i, "phone": f"+84 90 000 {i:04d}",
            "email": f"hv{i:02d}@review.example.com", "homeBranchId": branch["id"], "userId": account["userId"]}, "student")
        learners.append({"user": account["userId"], "student": student["id"], "name": f"{first} (demo)"})
    parent = post("/api/identity/learners", {"displayName": "Phụ huynh (demo)", "role": "Parent", "username": "ph-review01"},
                  "parent")
    _set_known_password(host, parent["username"], parent["temporaryPassword"])

    # --- packages, orders, payments -----------------------------------------------------------------------------------
    def package(code, vi, en, paid, bonus, price):
        return post("/api/billing/packages", {
            "code": code, "name": {"vi": vi, "en": en}, "paidCredits": paid, "bonusCredits": bonus,
            "price": price, "currency": "VND"}, f"package {code}")

    pkg_jp = package("JP-24", "Gói tiếng Nhật 24 buổi", "Japanese, 24 lessons", 24, 2, 4800000)
    pkg_en = package("EN-30", "Gói tiếng Anh 30 buổi", "English, 30 lessons", 30, 0, 6000000)
    pkg_zh = package("ZH-20", "Gói tiếng Trung 20 buổi", "Chinese, 20 lessons", 20, 2, 3600000)

    def order(learner, pkg, cls, paid):
        o = post("/api/billing/orders", {"studentId": learner["student"], "packageId": pkg["id"], "classId": cls["id"]}, "order")
        if paid:
            post(f"/api/billing/orders/{o['id']}/payments", {"amount": paid, "method": "bank_transfer",
                                                              "reference": f"CK-{o['id'][:8]}"}, "payment")
        return o

    for learner in learners[0:5]:
        order(learner, pkg_jp, jp_on1, 4800000)
    for learner in learners[5:9]:
        order(learner, pkg_en, en_q1, 6000000)
    order(learners[9], pkg_zh, zh_hn, 3600000)
    order(learners[10], pkg_zh, zh_hn, 1800000)        # half paid: not enrolled yet
    order(learners[11], pkg_jp, jp_on2, None)           # unpaid order
    for learner in learners[0:2]:
        order(learner, pkg_jp, jp_on2, 4800000)          # fills JP-N4-ON2 (capacity 2)
    order(learners[2], pkg_jp, jp_on2, 4800000)          # class full -> order waits for a refund

    def roster(cls):
        return get(f"/api/academic/classes/{cls['id']}/roster", "roster")

    wait_for("JP-N5-ON1 enrollments", lambda: len(roster(jp_on1)) >= 5)
    wait_for("EN-A2-Q1 enrollments", lambda: len(roster(en_q1)) >= 4)

    # A refunded order: paid for ZH-HSK1-HN, then the student withdrew before the start (full refund, seat freed).
    withdrawn = order(learners[3], pkg_zh, zh_hn, 3600000)
    wait_for("ZH-HSK1-HN enrollment", lambda: any(r["studentId"] == learners[3]["student"] for r in roster(zh_hn)))
    accountant = {**host, "Authorization": f"Bearer {staff_token('accountant@review.example.com')}"}
    post(f"/api/billing/orders/{withdrawn['id']}/refunds", {
        "method": "bank_transfer", "reference": f"HT-{withdrawn['id'][:8]}",
        "reason": "Đổi lịch làm việc, nghỉ trước khai giảng (demo)"}, "refund", accountant)

    # --- leads (public form, as a visitor would) ----------------------------------------------------------------------
    leads = [("Khách Lan (demo)", "ja", "Học giao tiếp để đi làm"), ("Khách Tú (demo)", "en", "IELTS 6.5"),
             ("Khách Vy (demo)", "zh-Hans", "Thi HSK 3"), ("Khách Nam (demo)", "ko", "Du học Hàn Quốc"),
             ("Khách Hòa (demo)", "en", "Tiếng Anh cho con 8 tuổi"), ("Khách Sơn (demo)", "ja", "JLPT N3")]
    for i, (name, lang, goal) in enumerate(leads, start=1):
        must(call("POST", "/api/crm/public/leads", {
            "studentName": name, "phone": f"+84 91 000 {i:04d}", "email": f"lead{i:02d}@review.example.com",
            "languageCode": lang, "goal": goal, "preferredBranchId": q1["id"], "consentVersion": "2026-11",
            "consentGiven": True, "locale": "vi", "captcha": solve_captcha(host)}, host), "lead")
    consultant = {**host, "Authorization": f"Bearer {staff_token('consultant@review.example.com')}"}
    lead_rows = wait_for("leads", lambda: get("/api/crm/leads", "leads", consultant))
    for lead, status in zip(lead_rows, ["Contacted", "Contacted", "Converted", "Lost"]):
        post(f"/api/crm/leads/{lead['id']}/status", {"status": status}, "lead status", consultant)

    # --- past lessons: rooms, participants, attendance ----------------------------------------------------------------
    now = dt.datetime.now(dt.timezone.utc)
    held_jp = [s for s in jp_sessions if _ends(s) < now]
    held_en = [s for s in en_sessions if _ends(s) < now]
    teacher_ja = {**host, "Authorization": f"Bearer {staff_token('teacher.ja@review.example.com')}"}
    teacher_en = {**host, "Authorization": f"Bearer {staff_token('teacher.en@review.example.com')}"}

    jp_learners = learners[0:5]
    for n, session in enumerate(held_jp):
        # Varied attendance: one student often late, one misses every fourth lesson, one leaves early.
        people = [{"userId": teachers["ja"], "displayName": "GV tiếng Nhật", "joinedMinute": 0, "leftMinute": 90}]
        for k, learner in enumerate(jp_learners):
            if k == 4 and n % 4 == 3:
                continue
            joined, left = (15, 90) if k == 3 else (0, 40) if (k == 2 and n % 3 == 0) else (0, 90)
            people.append({"userId": learner["user"], "displayName": learner["name"], "joinedMinute": joined, "leftMinute": left})
        wait_for("room for a past lesson", lambda s=session: _simulate(admin, s["id"], people))
        wait_for("draft attendance", lambda s=session: get(f"/api/learning/sessions/{s['id']}/attendance", "attendance", teacher_ja))
        post(f"/api/learning/sessions/{session['id']}/attendance/confirm", {"overrides": None}, "confirm attendance", teacher_ja)

    # Offline class: no meeting log, so the teacher marks everyone (unmarked seats default to absent).
    en_seats = roster(en_q1)
    for n, session in enumerate(held_en):
        overrides = [{"enrollmentId": seat["enrollmentId"], "status": "Absent" if (i == 1 and n % 3 == 2) else "Present"}
                     for i, seat in enumerate(en_seats)]
        post(f"/api/learning/sessions/{session['id']}/attendance/confirm", {"overrides": overrides}, "confirm attendance", teacher_en)

    # --- payroll -------------------------------------------------------------------------------------------------------
    first_this_month = TODAY.replace(day=1)
    last_month_end = first_this_month - dt.timedelta(days=1)
    time.sleep(5)  # let the Worker turn the confirmed attendance into timesheets
    previous = call("POST", "/api/hr/payroll-periods", {
        "from": last_month_end.replace(day=1).isoformat(), "to": last_month_end.isoformat()}, admin)
    if previous[0] < 400:
        call("POST", f"/api/hr/payroll-periods/{previous[1]['id']}/lock", None, admin)
    next_month = (first_this_month + dt.timedelta(days=32)).replace(day=1)
    call("POST", "/api/hr/payroll-periods", {
        "from": first_this_month.isoformat(), "to": (next_month - dt.timedelta(days=1)).isoformat()}, admin)

    # --- two-step sign-in for the accountant (the last step: the accountant signs in with a code from now on) --------
    accountant = {**host, "Authorization": f"Bearer {staff_token('accountant@review.example.com')}"}
    secret = must(call("POST", "/api/identity/me/2fa/setup", None, accountant), "2fa setup")["secret"]
    must(call("POST", "/api/identity/me/2fa/enable", {"code": totp(secret)}, accountant), "2fa enable")

    print(f"tenant '{SLUG}' ready at http://{SLUG}.{args.base_domain}:4000")
    print(f"  {len(held_jp)} online + {len(held_en)} offline lessons held with confirmed attendance")
    print(f"  staff (password {STAFF_PASSWORD}): admin, manager, coordinator, consultant, accountant,")
    print("    accountant.hn (Cau Giay branch only), teacher.ja, teacher.en, teacher.zh  @review.example.com")
    print(f"  learners (password {LEARNER_PASSWORD}): hv-review01..hv-review12, parent ph-review01")
    print(f"  accountant@ signs in with two-step sign-in, TOTP secret {secret}")
    print(f"    current code: python infra/scripts/seed_review.py --totp {secret}")
    print(f"platform console at http://admin.{args.base_domain}:4000/platform")
    print(f"  {OPERATOR_EMAIL} / {OPERATOR_PASSWORD}, TOTP secret {OPERATOR_TOTP_SECRET} (code now: {totp(OPERATOR_TOTP_SECRET)})")


def _healthy():
    try:
        return call("GET", "/health")[0] == 200
    except Exception:
        return False


def _ends(session):
    return dt.datetime.fromisoformat(session["endsAt"].replace("Z", "+00:00"))


def _simulate(admin, session_id, people):
    status, body = call("POST", f"/api/workspace/dev/sessions/{session_id}/simulate",
                        {"participants": people, "recording": True}, admin)
    if status < 400:
        return True
    if isinstance(body, dict) and body.get("code") == "workspace.meeting_not_ready":
        return False
    sys.exit(f"simulate failed: {status} {body}")


def _set_known_password(host, username, temporary):
    token = must(call("POST", "/api/identity/learn/login", {"username": username, "password": temporary}, host),
                 f"login {username}")["accessToken"]
    must(call("POST", "/api/identity/learn/change-password", {
        "currentPassword": temporary, "newPassword": LEARNER_PASSWORD}, {**host, "Authorization": f"Bearer {token}"}),
        f"set password {username}")


if __name__ == "__main__":
    main()
