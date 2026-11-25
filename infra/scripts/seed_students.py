"""Dev/staging only: adds many synthetic students to an existing tenant, to try paging, search and export limits.

Names are random Vietnamese names (with accents, so "an" / "Ân" search can be tried), phones are random 09x numbers,
emails are hv.<n>@example.com, children get a guardian. Students are spread over the tenant's branches, a few are
inactive. Nothing here is real personal data.

Usage (after seed_review.py):
  python infra/scripts/seed_students.py [--count 1000] [--slug review] [--admin admin@review.example.com]

KONNYAKU_API (default http://localhost:5080) is the API; the admin password defaults to the review seed's.
"""
import argparse
import json
import os
import random
import sys
import urllib.error
import urllib.request

API = os.environ.get("KONNYAKU_API", "http://localhost:5080")

FAMILY = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý"]
MIDDLE = ["Văn", "Thị", "Minh", "Ngọc", "Gia", "Quốc", "Thanh", "Hoài", "Bảo", "Đức", "Thu", "Khánh", "Anh", "Hữu"]
GIVEN = ["An", "Ân", "Anh", "Bình", "Châu", "Dũng", "Đạt", "Giang", "Hà", "Hải", "Hân", "Hiếu", "Hoa", "Huy", "Khang", "Khoa",
         "Lan", "Linh", "Long", "Mai", "Minh", "My", "Nam", "Ngân", "Nhi", "Phúc", "Phương", "Quân", "Quỳnh", "Sơn", "Tâm",
         "Thảo", "Thư", "Trang", "Trí", "Tú", "Tuấn", "Uyên", "Vy", "Yến"]


def call(method, path, body=None, headers=None):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"Content-Type": "application/json; charset=utf-8", **(headers or {})})
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read().decode("utf-8")
            return r.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--count", type=int, default=1000)
    parser.add_argument("--slug", default="review")
    parser.add_argument("--base-domain", default="localhost")
    parser.add_argument("--admin", default="admin@review.example.com")
    parser.add_argument("--password", default="Review-Pass-123")
    parser.add_argument("--seed", type=int, default=13, help="random seed (same seed, same students)")
    args = parser.parse_args()
    rng = random.Random(args.seed)
    host = {"X-Tenant-Host": f"{args.slug}.{args.base_domain}"}

    status, login = call("POST", "/api/identity/staff/login", {"email": args.admin, "password": args.password}, host)
    if status != 200:
        sys.exit(f"login failed: {status} {login}")
    admin = {**host, "Authorization": f"Bearer {login['accessToken']}"}
    status, branches = call("GET", "/api/organization/branches", None, admin)
    branch_ids = [b["id"] for b in branches] if status == 200 else []

    year = 2026
    created = 0
    for n in range(1, args.count + 1):
        family = rng.choice(FAMILY)
        name = f"{family} {rng.choice(MIDDLE)} {rng.choice(GIVEN)}"
        birth = rng.randint(year - 40, year - 6)
        body = {
            "fullName": name,
            "birthYear": birth,
            "phone": f"09{rng.randint(0, 99_999_999):08d}",
            "email": f"hv.{args.seed}.{n:05d}@example.com",
            "homeBranchId": rng.choice(branch_ids + [None]) if branch_ids else None,
            "guardianName": f"{family} {rng.choice(MIDDLE)} {rng.choice(GIVEN)}" if year - birth < 18 else None,
        }
        status, student = call("POST", "/api/crm/students", body, admin)
        if status != 201:
            sys.exit(f"student {n} failed: {status} {student}")
        if rng.random() < 0.08:
            call("POST", f"/api/crm/students/{student['id']}/deactivate", {}, admin)
        created += 1
        if n % 100 == 0:
            print(f"{n} students", flush=True)
    print(f"done: {created} students added to {args.slug}")


if __name__ == "__main__":
    main()
