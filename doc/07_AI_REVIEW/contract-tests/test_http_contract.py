"""HTTP contract tests (mục 6 của review prompt) — CHƯA CHẠY vì chưa có server.

Mã hóa hành vi MỤC TIÊU của hợp đồng (gồm các sửa đổi đề xuất AIR-xxx), tập trung
401/403/409/412/422/429, comment OFF, sponsored publication OFF, unpublish media,
expired rights. Toàn bộ test tự SKIP khi chưa đặt TT_API_BASE_URL.

Chạy trên staging/sandbox (KHÔNG chạy trên production):
    pip install pytest requests
    TT_API_BASE_URL=https://staging.example.invalid/api/v1 pytest -v test_http_contract.py

Biến môi trường (seed data do QA chuẩn bị theo WBS-068; giá trị phiên lấy từ secret store, không commit):
    TT_MEMBER_COOKIE, TT_MEMBER_CSRF          phiên member đủ điều kiện
    TT_AUTHOR_COOKIE, TT_AUTHOR_CSRF          staff vai trò author (tác giả bài seed)
    TT_APPROVER_COOKIE, TT_APPROVER_CSRF      staff vai trò approver
    TT_FLAGS                                  trạng thái cờ trên môi trường, vd "comments=off,sponsored=off,ad_slots=off"
    TT_SEED_VIDEO_ID                          video PUBLISHED, rights hợp lệ
    TT_SEED_ARTICLE_ID                        bài DRAFT của author seed
    TT_SEED_SPONSORED_ARTICLE_ID              bài SPONSORED đã APPROVED, đủ sponsor (dùng khi sponsored=off)
    TT_SEED_UNPUBLISHED_VIDEO_ID              video vừa unpublish/takedown
    TT_SEED_EXPIRED_RIGHTS_VIDEO_ID           video có rights hết hạn
    TT_RATE_LIMIT_PROBE                       số request tối đa để chạm 429 (mặc định 40)
    TT_PLACEMENT_CODES                        mã slot quảng cáo seed, phân tách bằng dấu phẩy (mặc định article_bottom)

Bám theo hợp đồng 07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml.
"""
import os
import uuid

import pytest

requests = pytest.importorskip("requests")

BASE = os.environ.get("TT_API_BASE_URL", "").rstrip("/")
pytestmark = pytest.mark.skipif(not BASE, reason="TT_API_BASE_URL chưa đặt — chưa có server để chạy contract test")

FLAGS = dict(kv.split("=", 1) for kv in os.environ.get("TT_FLAGS", "").split(",") if "=" in kv)


def env(name):
    value = os.environ.get(name)
    if not value:
        pytest.skip(f"thiếu biến môi trường {name}")
    return value


def session(role=None):
    s = requests.Session()
    if role:
        cookie_name = "__Host-tt_member_session" if role == "MEMBER" else "__Host-tt_cms_session"  # ADR-0001 D4
        s.cookies.set(cookie_name, env(f"TT_{role}_COOKIE"))
        s.headers["X-CSRF-Token"] = env(f"TT_{role}_CSRF")
    return s


def call(s, method, path, *, idem=None, if_match=None, **kw):
    headers = kw.pop("headers", {})
    if method in ("POST", "PUT", "PATCH", "DELETE"):
        headers.setdefault("Idempotency-Key", idem or str(uuid.uuid4()))
    if if_match:
        headers["If-Match"] = if_match
    return s.request(method, BASE + path, headers=headers, timeout=15, **kw)


def assert_problem(resp, status, code=None):
    assert resp.status_code == status, f"{resp.status_code} != {status}: {resp.text[:300]}"
    assert resp.headers.get("Content-Type", "").startswith("application/problem+json")
    body = resp.json()
    for field in ("type", "title", "status", "code", "traceId"):
        assert field in body, f"problem+json thiếu '{field}'"
    if code:
        assert body["code"] == code, f"code={body['code']} != {code}"
    return body


# ---------- Công khai (FR-ID-001, FR-DISC-004) ----------

def test_public_read_never_requires_login():
    for path in ("/articles", "/videos", "/home", "/site/config"):
        assert requests.get(BASE + path, timeout=15).status_code == 200, path


# ---------- Community P0* (FR-COM-002/008/009/012, AIR-009) ----------

def test_comment_write_unauthenticated_returns_401():
    resp = call(requests.Session(), "POST", f"/videos/{env('TT_SEED_VIDEO_ID')}/comments", json={"text": "xin chào"})
    assert_problem(resp, 401)


def test_comment_write_without_csrf_returns_403_csrf_invalid():
    s = session("MEMBER")
    s.headers.pop("X-CSRF-Token")
    resp = call(s, "POST", f"/videos/{env('TT_SEED_VIDEO_ID')}/comments", json={"text": "xin chào"})
    assert_problem(resp, 403, "CSRF_INVALID")


@pytest.mark.skipif(FLAGS.get("comments") != "off", reason="chỉ chạy khi cờ comments OFF")
def test_comment_write_denied_when_flag_off():
    resp = call(session("MEMBER"), "POST", f"/videos/{env('TT_SEED_VIDEO_ID')}/comments", json={"text": "xin chào"})
    assert_problem(resp, 403, "FEATURE_DISABLED")


@pytest.mark.skipif(FLAGS.get("comments") != "on", reason="chỉ chạy trong sandbox bật comments")
def test_comment_idempotent_replay_and_key_reuse_conflict():
    s, key = session("MEMBER"), str(uuid.uuid4())
    path = f"/videos/{env('TT_SEED_VIDEO_ID')}/comments"
    first = call(s, "POST", path, idem=key, json={"text": "bình luận A"})
    second = call(s, "POST", path, idem=key, json={"text": "bình luận A"})
    assert first.status_code == 201 and second.status_code in (200, 201)
    assert first.json()["id"] == second.json()["id"], "retry cùng Idempotency-Key tạo bình luận thứ hai"
    assert_problem(call(s, "POST", path, idem=key, json={"text": "nội dung khác"}), 409)


@pytest.mark.skipif(FLAGS.get("comments") != "on", reason="chỉ chạy trong sandbox bật comments")
def test_comment_rate_limit_returns_429_with_retry_after():
    s = session("MEMBER")
    path = f"/videos/{env('TT_SEED_VIDEO_ID')}/comments"
    for i in range(int(os.environ.get("TT_RATE_LIMIT_PROBE", "40"))):
        resp = call(s, "POST", path, json={"text": f"spam probe {i}"})
        if resp.status_code == 429:
            assert_problem(resp, 429, "RATE_LIMITED")
            assert resp.headers.get("Retry-After"), "429 thiếu Retry-After (AIR-009)"
            return
    pytest.fail("không chạm rate limit trong số request probe")


# ---------- CMS workflow (FR-CMS-002/007/010, FR-NEWS-004, AIR-004) ----------

def test_reject_without_reason_returns_422():
    s, article = session("APPROVER"), env("TT_SEED_ARTICLE_ID")
    etag = call(s, "GET", f"/admin/articles/{article}").headers.get("ETag", '"x"')
    resp = call(s, "POST", f"/admin/articles/{article}/reject", if_match=etag, json={})
    assert_problem(resp, 422)


def test_versioned_write_without_if_match_returns_428():
    resp = call(session("AUTHOR"), "PATCH", f"/admin/articles/{env('TT_SEED_ARTICLE_ID')}", json={"title": "Thiếu If-Match"})
    assert_problem(resp, 428, "PRECONDITION_REQUIRED")


def test_patch_article_with_stale_if_match_returns_412():
    s, article = session("AUTHOR"), env("TT_SEED_ARTICLE_ID")
    current = call(s, "GET", f"/admin/articles/{article}")
    assert current.status_code == 200
    assert current.headers.get("ETag"), "GET admin article không trả ETag (AIR-004)"
    resp = call(s, "PATCH", f"/admin/articles/{article}", if_match='"stale-0"', json={"title": "Sửa đè"})
    assert_problem(resp, 412, "REVISION_CONFLICT")


def test_author_cannot_approve_own_revision():
    s, article = session("AUTHOR"), env("TT_SEED_ARTICLE_ID")
    current = call(s, "GET", f"/admin/articles/{article}")
    resp = call(s, "POST", f"/admin/articles/{article}/approve", if_match=current.headers.get("ETag", '"x"'),
                json={"revisionId": current.json().get("workingRevisionId", str(uuid.uuid4()))})
    assert resp.status_code in (403, 409), "tự duyệt trái SoD phải bị chặn"


# ---------- P0-ADS (FR-NEWS-008, FR-ADS-001/002, AIR-002/011/013) ----------

def test_sponsored_without_sponsor_name_rejected():
    s, article = session("AUTHOR"), env("TT_SEED_ARTICLE_ID")
    etag = call(s, "GET", f"/admin/articles/{article}").headers.get("ETag", '"x"')
    resp = call(s, "PATCH", f"/admin/articles/{article}", if_match=etag,
                json={"contentKind": "SPONSORED", "sponsorship": None})
    assert_problem(resp, 422, "SPONSORSHIP_LABEL_REQUIRED")


@pytest.mark.skipif(FLAGS.get("sponsored") != "off", reason="chỉ chạy khi sponsored publication OFF")
def test_sponsored_publish_blocked_when_flag_off():
    s, article = session("APPROVER"), env("TT_SEED_SPONSORED_ARTICLE_ID")
    current = call(s, "GET", f"/admin/articles/{article}")
    resp = call(s, "POST", f"/admin/articles/{article}/publish", if_match=current.headers.get("ETag", '"x"'),
                json={"revisionId": current.json().get("workingRevisionId", str(uuid.uuid4()))})
    assert_problem(resp, 403, "FEATURE_DISABLED")


@pytest.mark.skipif(FLAGS.get("ad_slots") != "off", reason="chỉ chạy khi display_ad_slots OFF")
def test_ad_slots_flag_off_returns_empty_list():
    resp = requests.get(BASE + "/ads/placements", params={"placementCodes": os.environ.get("TT_PLACEMENT_CODES", "article_bottom")}, timeout=15)
    assert resp.status_code == 200 and resp.json()["items"] == []


def test_public_sponsored_items_always_carry_label():
    for path in ("/articles", "/videos", "/home"):
        body = requests.get(BASE + path, timeout=15).json()
        items = body.get("items", []) or body.get("featuredArticles", []) + body.get("featuredVideos", [])
        for item in items:
            assert "contentKind" in item, f"{path}: item thiếu contentKind"
            if item["contentKind"] == "SPONSORED":
                assert item.get("sponsorLabel"), f"{path}: item SPONSORED thiếu sponsorLabel"


# ---------- Takedown / rights (FR-VID-010, FR-DISC-004, AIR-018) ----------

def test_unpublished_video_not_reachable_anywhere():
    vid = env("TT_SEED_UNPUBLISHED_VIDEO_ID")
    assert requests.get(f"{BASE}/videos/{vid}", timeout=15).status_code in (404, 410)
    assert requests.get(f"{BASE}/videos/{vid}/playback", timeout=15).status_code in (404, 410)
    feed = requests.get(f"{BASE}/videos", params={"limit": 100}, timeout=15).json()["items"]
    assert vid not in {v["id"] for v in feed}, "video đã gỡ vẫn nằm trong feed"


def test_expired_rights_video_has_no_playback_grant():
    resp = requests.get(f"{BASE}/videos/{env('TT_SEED_EXPIRED_RIGHTS_VIDEO_ID')}/playback", timeout=15)
    assert resp.status_code in (403, 404, 410)
    if resp.status_code == 403:
        assert_problem(resp, 403, "RIGHTS_EXPIRED")
