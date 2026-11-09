"""Static contract conformance tests for Remediation 07 v0.2 (Tinhte.com.vn).

Kiểm tra TĨNH hợp đồng OpenAPI + CSV truy vết theo chính sách trong
07_API_IMPLEMENTATION_GUIDE_v0.2.md. Không gọi HTTP, không cần server.
Mỗi test ghi mã đề xuất AIR-xxx tương ứng trong 07_AI_TECH_REVIEW_PROPOSALS.md.

Một test FAIL = hợp đồng v0.2 chưa đạt chính sách; danh sách vi phạm nằm trong
thông điệp lỗi. Mục tiêu: toàn bộ PASS trước khi đóng băng hợp đồng v0.3 và codegen.

Chạy (Python 3.11+):
    pip install pyyaml openapi-spec-validator
    python -m unittest -v test_static_contract.py

Biến môi trường tùy chọn:
    TT_DOC_DIR        thư mục gói 07 (mặc định: ../../ so với file này)
    TT_SPEC_FILE      spec cần kiểm (mặc định: v0.2 trong TT_DOC_DIR)
    TT_INVENTORY_FILE inventory tương ứng spec (mặc định: inventory v0.2)

Kiểm hợp đồng v0.3:
    TT_SPEC_FILE=../v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml \
    TT_INVENTORY_FILE=../v0.3/07_API_ENDPOINT_INVENTORY_v0.3_DRAFT.csv \
    python -m unittest -v test_static_contract.py
Các test Traceability T23–T25, T31–T33 kiểm CSV FR84/WBS (việc của BA). Kiểm bản truy vết v0.3:
    TT_FR84_FILE=../v0.3/trace/07_FR84_TRACEABILITY_v0.3_DRAFT.csv \
    TT_WBS_FILE=../v0.3/trace/07_WBS_EXTENDED_v0.3_DRAFT.csv  (kèm hai biến spec/inventory v0.3 ở trên)
"""
import csv
import json
import os
import re
import unittest
from pathlib import Path

import yaml

DOC = Path(os.environ.get("TT_DOC_DIR", Path(__file__).resolve().parents[2]))
SPEC_V02 = DOC / "07_OPENAPI_TINHTE_EXPANDED_v0.2.yaml"
SPEC_FILE = Path(os.environ.get("TT_SPEC_FILE", SPEC_V02))
INVENTORY = Path(os.environ.get("TT_INVENTORY_FILE", DOC / "07_API_ENDPOINT_INVENTORY_v0.2.csv"))
FR84 = Path(os.environ.get("TT_FR84_FILE", DOC / "07_FR84_TRACEABILITY_v0.2.csv"))
WBS = Path(os.environ.get("TT_WBS_FILE", DOC / "07_WBS_EXTENDED_135_v0.2.csv"))
SQL = DOC / "07_DB_ADS_LITE_REFERENCE_v0.2.sql"
PARALLEL = DOC / "07_PARALLEL_WORKSTREAM_RELEASE_GATES_v0.2.md"

METHODS = ("get", "post", "put", "patch", "delete")
SPEC = yaml.safe_load(SPEC_FILE.read_text(encoding="utf-8"))
COMP = SPEC["components"]
IS_V02 = SPEC_FILE.resolve() == SPEC_V02.resolve()
REGISTRY = {f["key"]: f for f in SPEC.get("x-feature-flags", {}).get("flags", [])}
CATALOG = {c["code"]: c["status"] for c in SPEC.get("x-error-catalog", {}).get("codes", [])}


def read_csv(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def operations():
    for path, item in SPEC["paths"].items():
        for method, op in item.items():
            if method in METHODS:
                yield method.upper(), path, op


def pnames(op):
    return [p["$ref"].split("/")[-1] if "$ref" in p else p["name"] for p in op.get("parameters", [])]


def resolve(node):
    while isinstance(node, dict) and "$ref" in node:
        kind, name = node["$ref"].split("/")[-2:]
        node = COMP[kind][name]
    return node


def is_authenticated(op):
    return bool(op.get("security", SPEC.get("security")))


def fmt(items):
    return "\n  - " + "\n  - ".join(" ".join(map(str, i)) if isinstance(i, tuple) else str(i) for i in items)


OPS = list(operations())
MUTATIONS = [(m, p, op) for m, p, op in OPS if m != "GET"]
# PUT/DELETE dạng "toggle" idempotent tự nhiên, không phải tài nguyên có phiên bản.
TOGGLES = {("PUT", "/me/bookmarks/{videoId}"), ("PUT", "/comments/{commentId}/like"),
           ("PUT", "/admin/comments/{commentId}/pin")}


class SpecStructure(unittest.TestCase):
    def test_T01_openapi_31_schema_valid(self):
        """Spec hợp lệ theo JSON Schema OpenAPI 3.1 (validator chuyên dụng)."""
        try:
            from openapi_spec_validator import validate
        except ImportError:
            self.skipTest("openapi-spec-validator chưa cài")
        validate(SPEC)

    def test_T02_counts_and_unique_operation_ids(self):
        """v0.2: đúng 93/106. Bản sau: không được âm thầm xóa thao tác nào của v0.2."""
        ids = [op["operationId"] for _, _, op in OPS]
        self.assertEqual(len(ids), len(set(ids)), "operationId trùng")
        if IS_V02:
            self.assertEqual(len(SPEC["paths"]), 93)
            self.assertEqual(len(OPS), 106)
        else:
            v02 = yaml.safe_load(SPEC_V02.read_text(encoding="utf-8"))["paths"]
            old = {(m.upper(), p) for p, it in v02.items() for m in it if m in METHODS}
            removed = sorted(old - {(m, p) for m, p, _ in OPS})
            self.assertEqual(removed, [], "Thao tác v0.2 bị xóa:" + fmt(removed))

    def test_T16_no_orphan_components(self):
        """AIR-027: component khai báo nhưng không được tham chiếu."""
        text = json.dumps(SPEC)
        orphans = [f"{kind}/{name}" for kind in ("schemas", "parameters", "responses")
                   for name in COMP[kind] if f'"#/components/{kind}/{name}"' not in text]
        self.assertEqual(orphans, [], "Component mồ côi:" + fmt(orphans))

    def test_T17_no_unknown_schema_keywords(self):
        """AIR-027: phát hiện khóa gõ sai (vd 'defaults false') bị JSON Schema bỏ qua âm thầm."""
        known = {"type", "format", "enum", "const", "items", "properties", "required", "description",
                 "minLength", "maxLength", "minimum", "maximum", "pattern", "default", "example",
                 "additionalProperties", "allOf", "oneOf", "anyOf", "discriminator", "$ref",
                 "minItems", "maxItems", "nullable", "readOnly", "writeOnly", "examples", "title",
                 "if", "then", "else", "not", "uniqueItems", "deprecated", "minProperties", "maxProperties"}
        bad = []

        def walk(node, where):
            if isinstance(node, dict):
                if "properties" in node:
                    for prop, sch in node["properties"].items():
                        if isinstance(sch, dict):
                            for k in sch:
                                if k not in known and not k.startswith("x-"):
                                    bad.append((where + "." + prop, repr(k)))
                for k, v in node.items():
                    if k != "properties":
                        walk(v, where)
                    else:
                        for prop, sch in v.items():
                            walk(sch, where + "." + prop)
            elif isinstance(node, list):
                for v in node:
                    walk(v, where)

        for name, sch in COMP["schemas"].items():
            walk(sch, name)
        self.assertEqual(bad, [], "Khóa schema không hợp lệ:" + fmt(bad))

    def test_T18_row_version_type_consistent(self):
        """AIR-004: rowVersion phải cùng kiểu ở mọi DTO để map sang ETag/If-Match."""
        types = {name: sch["properties"]["rowVersion"].get("type")
                 for name, sch in COMP["schemas"].items()
                 if isinstance(sch, dict) and "rowVersion" in sch.get("properties", {})}
        self.assertEqual(len(set(map(str, types.values()))), 1, f"rowVersion không đồng nhất: {types}")

    def test_T19_every_operation_links_fr(self):
        """AIR-021: thao tác không có x-requirements thì không có owner nghiệp vụ / test trace."""
        missing = [(m, p, op.get("x-scope")) for m, p, op in OPS if not op.get("x-requirements")]
        self.assertEqual(missing, [], f"{len(missing)} thao tác thiếu FR:" + fmt(missing))

    def test_T20_errors_use_problem_json(self):
        bad = []
        for m, p, op in OPS:
            for code, resp in op["responses"].items():
                if code.startswith(("4", "5")):
                    content = resolve(resp).get("content", {})
                    if "application/problem+json" not in content:
                        bad.append((m, p, code))
        self.assertEqual(bad, [], "Lỗi không dùng problem+json:" + fmt(bad))


class SecurityAndConcurrency(unittest.TestCase):
    def test_T03_cookie_mutations_require_csrf(self):
        bad = [(m, p) for m, p, op in MUTATIONS if is_authenticated(op) and "XCsrfToken" not in pnames(op)]
        self.assertEqual(bad, [], "Thiếu X-CSRF-Token:" + fmt(bad))

    def test_T04_versioned_writes_require_if_match(self):
        """AIR-004: PATCH/PUT tài nguyên có phiên bản phải có If-Match (FR-CMS-007)."""
        bad = [(m, p) for m, p, op in MUTATIONS
               if m in ("PATCH", "PUT") and (m, p) not in TOGGLES and "IfMatch" not in pnames(op)]
        self.assertEqual(bad, [], f"{len(bad)} thao tác ghi thiếu If-Match:" + fmt(bad))

    def test_T05_admin_get_exposes_etag(self):
        """AIR-004: client phải lấy được ETag từ GET trước khi gửi If-Match."""
        bad = []
        for path in ("/admin/articles/{articleId}", "/admin/videos/{videoId}"):
            ok = resolve(SPEC["paths"][path]["get"]["responses"]["200"])
            if "ETag" not in ok.get("headers", {}):
                bad.append(path)
        self.assertEqual(bad, [], "GET admin không trả ETag:" + fmt(bad))

    def test_T12_create_posts_require_idempotency_key(self):
        """AIR-023: POST tạo mới (201) có rủi ro retry phải có Idempotency-Key."""
        bad = [(m, p) for m, p, op in MUTATIONS
               if m == "POST" and "201" in op["responses"] and is_authenticated(op)
               and "IdempotencyKey" not in pnames(op)]
        self.assertEqual(bad, [], "POST 201 thiếu Idempotency-Key:" + fmt(bad))


class StatusCodeMatrix(unittest.TestCase):
    def test_T06_authenticated_ops_declare_401(self):
        """AIR-009."""
        bad = [(m, p) for m, p, op in OPS if is_authenticated(op) and "401" not in op["responses"]]
        self.assertEqual(bad, [], f"{len(bad)} thao tác cần đăng nhập thiếu 401:" + fmt(bad))

    def test_T07_path_param_ops_declare_404(self):
        """AIR-009."""
        bad = [(m, p) for m, p, op in OPS if "{" in p and "404" not in op["responses"]]
        self.assertEqual(bad, [], f"{len(bad)} thao tác có path param thiếu 404:" + fmt(bad))

    def test_T08_mutations_declare_429_with_retry_after(self):
        """AIR-009."""
        bad = [(m, p) for m, p, op in MUTATIONS if "429" not in op["responses"]]
        if "Retry-After" not in COMP["responses"]["TooManyRequests"].get("headers", {}):
            bad.append(("components/responses/TooManyRequests", "thiếu header Retry-After"))
        self.assertEqual(bad, [], "Thiếu 429/Retry-After:" + fmt(bad))

    def test_T09_p0star_ops_gated_with_403(self):
        bad = [(m, p) for m, p, op in OPS if op.get("x-scope") == "P0*"
               and (not op.get("x-feature-gate") or "403" not in op["responses"])]
        self.assertEqual(bad, [], "P0* thiếu gate/403:" + fmt(bad))

    def test_T10_error_codes_consistent_with_guide(self):
        """AIR-009: guide quy định FEATURE_DISABLED/ELIGIBILITY_REQUIRED; không dùng mã khác cho cùng ý nghĩa."""
        guide_codes = set(CATALOG) or {"FEATURE_DISABLED", "ELIGIBILITY_REQUIRED", "SPONSORSHIP_LABEL_REQUIRED",
                                       "RIGHTS_EXPIRED", "REVISION_CONFLICT", "CSRF_INVALID", "RATE_LIMITED"}
        problems = []
        enum = set(COMP["schemas"]["Problem"]["properties"]["code"].get("enum", []))
        if CATALOG and enum != set(CATALOG):
            problems.append(("Problem.code enum lệch x-error-catalog", sorted(enum ^ set(CATALOG))))
        for m, p, op in OPS:
            found = set(re.findall(r"\b[A-Z][A-Z_]{5,}\b", op.get("description", "") + op.get("summary", "")))
            found |= set(op.get("x-error-codes", []))
            found -= {"SPONSORED", "EDITORIAL", "APPROVED", "PUBLISHED", "RECEIVED", "TRIAGED", "ACTIONED", "REJECTED", "CLOSED",
                       "CLEARED", "ACTIVE", "PENDING"}
            if found - guide_codes:
                problems.append((m, p, sorted(found - guide_codes)))
            for code in op.get("x-error-codes", []):
                if code in CATALOG and str(CATALOG[code]) not in op["responses"]:
                    problems.append((m, p, code, "status", CATALOG[code], "không khai báo"))
        self.assertEqual(problems, [], "Mã lỗi:" + fmt(problems))


class FeatureGatesAndAds(unittest.TestCase):
    def test_T11_feature_gate_keys_match_registry(self):
        """AIR-001: khóa gate phải thuộc registry; mọi khóa cũ ở DB/cấu hình phải được ánh xạ (legacyKeys)."""
        sql_keys = set(re.findall(r"\('([a-z_.]+)',\s*(?:true|false)\)", SQL.read_text(encoding="utf-8")))
        block = re.search(r"```yaml\n(.*?)```", PARALLEL.read_text(encoding="utf-8"), re.S).group(1)
        prod_keys = set(yaml.safe_load(block)["production"].keys())
        problems = []
        if REGISTRY:
            registry = set(REGISTRY)
            mapped = {k for f in REGISTRY.values() for k in f.get("legacyKeys", [])}
            problems += [("khóa DB/cấu hình chưa ánh xạ", k) for k in sorted((sql_keys | prod_keys) - mapped)]
            fields = {f["publicConfigField"] for f in REGISTRY.values() if f.get("publicConfigField")}
            site = set(COMP["schemas"]["SitePublicConfig"]["properties"]) - {"flagsVersion"}
            if fields != site:
                problems.append(("SitePublicConfig lệch registry", sorted(fields ^ site)))
        else:
            registry = sql_keys | prod_keys
        api_keys = {op["x-feature-gate"] for _, _, op in OPS if op.get("x-feature-gate")}
        api_keys |= {op["x-conditional-gate"]["flag"] for _, _, op in OPS if op.get("x-conditional-gate")}
        problems += [("gate không có trong registry", k) for k in sorted(api_keys - registry)]
        self.assertEqual(problems, [], f"Registry cờ {sorted(registry)}:" + fmt(problems))

    def test_T10b_sponsored_publication_gate_enforced_on_publish(self):
        """AIR-002: publish/schedule bài/video phải có gate điều kiện sponsored + SPONSORSHIP_LABEL_REQUIRED."""
        bad = []
        for path in ("/admin/articles/{articleId}/publish", "/admin/videos/{videoId}/publish",
                     "/admin/articles/{articleId}/schedule", "/admin/videos/{videoId}/schedule"):
            op = SPEC["paths"][path]["post"]
            if op.get("x-conditional-gate", {}).get("flag") not in ("ads.sponsored_publication", "sponsored_content_publication"):
                bad.append((path, "thiếu x-conditional-gate sponsored"))
            if "SPONSORSHIP_LABEL_REQUIRED" not in op.get("x-error-codes", []):
                bad.append((path, "thiếu mã SPONSORSHIP_LABEL_REQUIRED"))
        self.assertEqual(bad, [], "Publish chưa ràng buộc chính sách sponsored:" + fmt(bad))

    def test_T14_public_content_dtos_require_content_kind(self):
        """AIR-011: mọi DTO công khai phải bắt buộc contentKind để FE không thể bỏ nhãn tài trợ."""
        bad = []
        for name in ("ArticleSummary", "Video", "SearchHit"):
            sch = COMP["schemas"][name]
            if "contentKind" not in sch.get("properties", {}):
                bad.append((name, "thiếu contentKind"))
            elif "contentKind" not in sch.get("required", []):
                bad.append((name, "contentKind không required"))
        self.assertEqual(bad, [], "DTO công khai:" + fmt(bad))

    def test_T29_ad_placement_has_dimensions(self):
        """AIR-013: slot quảng cáo cần kích thước dự phòng để chống CLS."""
        props = COMP["schemas"]["AdPlacement"]["properties"]
        self.assertTrue({"width", "height"} <= set(props) or "aspectRatio" in props,
                        "AdPlacement không có width/height/aspectRatio")


class DomainContract(unittest.TestCase):
    def test_T13_cursor_lists_accept_cursor_param(self):
        """AIR-003: response có nextCursor thì request phải nhận cursor/limit."""
        bad = []
        for m, p, op in OPS:
            if m != "GET":
                continue
            ok = resolve(op["responses"].get("200", {}))
            sch = resolve(ok.get("content", {}).get("application/json", {}).get("schema", {}))
            if "nextCursor" in sch.get("properties", {}) and "Cursor" not in pnames(op):
                bad.append((m, p))
        self.assertEqual(bad, [], f"{len(bad)} list có nextCursor nhưng không nhận cursor:" + fmt(bad))

    def test_T15_moderation_action_enum_consistent(self):
        """AIR-006: enum hành động phải thống nhất và đủ APPROVE/LOCK cho FR-COM-007."""
        op = SPEC["paths"]["/admin/moderation/cases/{caseId}/actions"]["post"]
        inline = set(resolve(op["requestBody"]["content"]["application/json"]["schema"])
                     ["properties"]["action"]["enum"])
        component = set(COMP["schemas"]["ModerationActionRequest"]["properties"]["action"]["enum"])
        problems = []
        if inline != component:
            problems.append(f"inline={sorted(inline)} != component={sorted(component)}")
        if not {"APPROVE", "LOCK"} <= inline:
            problems.append(f"thiếu {sorted({'APPROVE', 'LOCK'} - inline)} trong enum đang dùng")
        self.assertEqual(problems, [], "; ".join(problems))

    def test_T27_takedown_list_returns_takedown_schema(self):
        """AIR-008."""
        op = SPEC["paths"]["/admin/takedown-requests"]["get"]
        ref = op["responses"]["200"]["content"]["application/json"]["schema"]["$ref"]
        self.assertNotIn("ModerationCase", ref, f"GET /admin/takedown-requests trả {ref}")

    def test_T28_revision_targeted_operations(self):
        """AIR-005: rollback và publish video phải chỉ rõ revisionId."""
        bad = []
        rb = resolve(SPEC["paths"]["/admin/articles/{articleId}/rollback"]["post"]["requestBody"]
                     ["content"]["application/json"]["schema"])
        if not any("revision" in k.lower() for k in rb.get("properties", {})):
            bad.append("rollback không có target revisionId")
        vp = SPEC["paths"]["/admin/videos/{videoId}/publish"]["post"]
        if "requestBody" not in vp:
            bad.append("publish video không có body revisionId")
        if "IfMatch" not in pnames(vp):
            bad.append("publish video không có If-Match")
        self.assertEqual(bad, [], "; ".join(bad))


class SchemaBehaviour(unittest.TestCase):
    """T30 — AIR-011/012/013/018/027: quy tắc điều kiện của schema chặn đúng dữ liệu sai (JSON Schema 2020-12)."""

    def check(self, name, instance):
        from jsonschema import Draft202012Validator
        validator = Draft202012Validator({"$ref": f"#/components/schemas/{name}", "components": COMP})
        return not list(validator.iter_errors(instance))

    def test_T30_conditional_rules(self):
        try:
            import jsonschema  # noqa: F401
        except ImportError:
            self.skipTest("jsonschema chưa cài")
        if "Sponsorship" not in COMP["schemas"]:
            self.skipTest("spec chưa có schema Sponsorship (v0.2)")
        sp = {"sponsorName": "ACME", "sponsorReference": "HD-01", "evidenceRef": "dms://hd-01",
              "validFrom": "2026-11-01T00:00:00Z", "validTo": "2026-12-01T00:00:00Z"}
        art = {"id": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "slug": "a", "title": "t", "publishedAt": "2026-11-09T00:00:00Z"}
        slot = {"code": "article_bottom", "label": "Quảng cáo", "width": 728, "height": 90}
        cases = [
            ("ArticleSummary", {**art, "contentKind": "EDITORIAL"}, True),
            ("ArticleSummary", {**art, "contentKind": "SPONSORED"}, False),
            ("ArticleSummary", {**art, "contentKind": "SPONSORED", "sponsorLabel": ""}, False),
            ("ArticleSummary", {**art, "contentKind": "SPONSORED", "sponsorLabel": "Nội dung tài trợ"}, True),
            ("ArticleSummary", art, False),
            ("SearchHit", {"type": "ARTICLE", "id": art["id"], "title": "t", "url": "/a", "contentKind": "SPONSORED"}, False),
            ("ArticlePatchRequest", {"contentKind": "SPONSORED"}, False),
            ("ArticlePatchRequest", {"contentKind": "SPONSORED", "sponsorship": None}, False),
            ("ArticlePatchRequest", {"contentKind": "SPONSORED", "sponsorship": {k: v for k, v in sp.items() if k != "evidenceRef"}}, False),
            ("ArticlePatchRequest", {"contentKind": "SPONSORED", "sponsorship": sp}, True),
            ("ArticlePatchRequest", {}, False),
            ("ArticlePatchRequest", {"slug": "Bai Viet"}, False),
            ("AdPlacementCreate", {**slot, "targetUrl": "javascript:alert(1)"}, False),
            ("AdPlacementCreate", {**slot, "targetUrl": "https://acme.vn"}, True),
            ("ExternalEmbedPlaybackGrant", {"deliveryType": "EXTERNAL_EMBED", "embedUrl": "https://x", "provider": "yt"}, False),
            ("ModerationActionRequest", {"action": "LOCK", "reason": "spam lặp"}, True),
            ("Problem", {"type": "about:blank", "title": "x", "status": 403, "code": "COMMENT_DISABLED", "traceId": "t"}, False),
        ]
        wrong = [(name, json.dumps(inst, ensure_ascii=False)[:80], "kỳ vọng hợp lệ" if exp else "kỳ vọng bị chặn")
                 for name, inst, exp in cases if self.check(name, inst) != exp]
        self.assertEqual(wrong, [], "Quy tắc schema sai:" + fmt(wrong))


class Traceability(unittest.TestCase):
    INV = read_csv(INVENTORY)
    FRS = read_csv(FR84)
    W = {r["WBS"]: r for r in read_csv(WBS)}

    def test_T21_inventory_matches_spec(self):
        spec_ops = {(m, p): op for m, p, op in OPS}
        bad = []
        for r in self.INV:
            op = spec_ops.get((r["Method"], r["Path"]))
            if op is None:
                bad.append((r["Method"], r["Path"], "không có trong spec"))
            elif (op.get("x-scope") or "") != r["Scope"] or (op.get("x-feature-gate") or "") != r["FeatureGate"]:
                bad.append((r["Method"], r["Path"], "lệch scope/gate"))
        self.assertEqual(len(self.INV), len(OPS), "số dòng inventory khác số thao tác trong spec")
        self.assertEqual(bad, [], fmt(bad))

    API_COL = "API endpoints (v0.3)" if "API endpoints (v0.3)" in FRS[0] else "API endpoints"
    IS_V03_TRACE = "Child coverage" in FRS[0]

    def test_T22_fr84_apis_exist_in_spec(self):
        spec_ops = {f"{m} {p}" for m, p, _ in OPS}
        bad = []
        for r in self.FRS:
            for api in filter(None, (a.strip() for a in r[self.API_COL].split(";"))):
                if api not in spec_ops:
                    bad.append((r["Requirement ID"], api))
        self.assertEqual(len(self.FRS), 84)
        self.assertEqual(bad, [], fmt(bad))

    def test_T23_child_scope_compatible_with_parent(self):
        """AIR-014: task P1/P2 không treo dưới cha P0; task P0*/P0 không treo dưới cha P1."""
        rank = {"P0": 0, "P0*": 0, "P0-ADS": 0, "P1": 1, "P2": 2}
        bad = []
        for wid, r in self.W.items():
            for parent in filter(None, (x.strip() for x in r["Dependencies"].split(";"))):
                if r["Group"].startswith("G-TRACE") and parent in self.W:
                    ps, cs = self.W[parent]["Scope"], r["Scope"]
                    if rank[cs] != rank[ps]:
                        bad.append((wid, cs, "-> parent", parent, ps))
        self.assertEqual(bad, [], "Scope con/cha lệch:" + fmt(bad))

    @staticmethod
    def expand_trace(text):
        out, prefix = set(), None
        for token in re.split(r"[,/;]", text):
            token = token.strip()
            m = re.match(r"^(FR-[A-Z]+)-(\d{3})(?:\.\.(\d{3}))?$", token)
            n = re.match(r"^(\d{3})(?:\.\.(\d{3}))?$", token)
            if m:
                prefix, lo, hi = m.group(1), int(m.group(2)), int(m.group(3) or m.group(2))
            elif n and prefix:
                lo, hi = int(n.group(1)), int(n.group(2) or n.group(1))
            else:
                prefix = None if not token.startswith("FR-") else prefix
                continue
            out |= {f"{prefix}-{i:03d}" for i in range(lo, hi + 1)}
        return out

    def test_T24_child_fr_present_in_parent_trace(self):
        """AIR-015: child '0 additional estimate' chỉ an toàn nếu FR đã nằm trong Trace của WBS cha.
        Bản v0.3 có cột ChildCoverage: INCLUDED_IN_PARENT (FR phải nằm trong Trace cha) · NEEDS_OWN_ESTIMATE
        (phải có ProposedPERTPD > 0) · BACKLOG_UNESTIMATED (chỉ cho P1/P2, ngoài MVP)."""
        bad = []
        for wid, r in self.W.items():
            if not r["Group"].startswith("G-TRACE"):
                continue
            parents = [x.strip() for x in r["Dependencies"].split(";") if x.strip()]
            covered = set().union(*(self.expand_trace(self.W[p]["Trace"]) for p in parents)) if parents else set()
            coverage = r.get("ChildCoverage", "")
            if coverage == "NEEDS_OWN_ESTIMATE":
                if not float(r.get("ProposedPERTPD") or 0) > 0:
                    bad.append((wid, r["Trace"], "NEEDS_OWN_ESTIMATE nhưng không có ProposedPERTPD"))
            elif coverage == "BACKLOG_UNESTIMATED":
                if r["Scope"].split()[0] not in ("P1", "P2"):
                    bad.append((wid, r["Trace"], "BACKLOG_UNESTIMATED chỉ dành cho P1/P2", r["Scope"]))
            elif r["Trace"] not in covered:
                bad.append((wid, r["Trace"], "parents", "+".join(parents) or "(không cha)"))
        self.assertEqual(bad, [], f"{len(bad)} child có FR không nằm trong Trace cha:" + fmt(bad))

    def test_T31_every_fr_has_test_ids_api_and_package(self):
        """AIR-025: mỗi FR có TC POS + NEG, ít nhất một API, gói phát hành hợp lệ (chỉ áp dụng FR84 v0.3)."""
        if not self.IS_V03_TRACE:
            self.skipTest("FR84 v0.2 chưa có cột TC ID / gói phát hành")
        bad = []
        for r in self.FRS:
            fr = r["Requirement ID"]
            ids = r["Test case IDs"]
            if f"TC-{fr}-POS" not in ids or f"TC-{fr}-NEG" not in ids:
                bad.append((fr, "thiếu TC POS/NEG"))
            if not r[self.API_COL].strip():
                bad.append((fr, "không có API"))
            if r["Release package"] not in ("P0", "P0*", "P0-ADS", "P0-ADS + P1", "P1", "P2"):
                bad.append((fr, "gói phát hành lạ", r["Release package"]))
        self.assertEqual(bad, [], fmt(bad))

    def test_T32_fr_ids_consistent_between_spec_and_fr84(self):
        """FR trong x-requirements của spec phải có trong FR84 và ngược lại (không FR mồ côi)."""
        spec_frs = {fr for _, _, op in OPS for fr in op.get("x-requirements", [])}
        fr84 = {r["Requirement ID"] for r in self.FRS}
        self.assertEqual((sorted(spec_frs - fr84), sorted(fr84 - spec_frs)), ([], []), "(chỉ trong spec, chỉ trong FR84)")

    def test_T33_wbs_trace_and_fr84_agree_both_ways(self):
        """AIR-014: Trace của WBS có FR ⇔ FR84 liệt kê WBS đó (bỏ qua dòng đề xuất chưa duyệt)."""
        fr_wbs = {r["Requirement ID"]: {x.strip() for x in r["WBS IDs"].split(";") if x.strip()} for r in self.FRS}
        bad = []
        for wid, r in self.W.items():
            if r["Status"] == "PROPOSED_PENDING_PM_SIGNOFF":
                continue
            for fr in sorted(self.expand_trace(r["Trace"])):
                if fr in fr_wbs and wid not in fr_wbs[fr]:
                    bad.append(("WBS→FR84", wid, fr, "FR84 thiếu WBS"))
        for fr, wids in fr_wbs.items():
            for wid in sorted(wids):
                if wid in self.W and fr not in self.expand_trace(self.W[wid]["Trace"]):
                    bad.append(("FR84→WBS", fr, wid, "Trace WBS thiếu FR"))
        self.assertEqual(bad, [], f"{len(bad)} lệch hai chiều:" + fmt(bad))

    def test_T25_ads_tasks_traced_in_fr84(self):
        """AIR-014: FR được WBS-131..135 thực hiện phải liệt kê các WBS đó trong FR84."""
        fr_wbs = {r["Requirement ID"]: r["WBS IDs"] for r in self.FRS}
        bad = []
        for wid in ("WBS-131", "WBS-132", "WBS-133", "WBS-134", "WBS-135"):
            for fr in (x.strip() for x in self.W[wid]["Trace"].split(";")):
                if wid not in fr_wbs.get(fr, ""):
                    bad.append((fr, "thiếu", wid))
        self.assertEqual(bad, [], fmt(bad))

    def test_T26_ads_estimate_arithmetic(self):
        rows = [self.W[f"WBS-13{i}"] for i in range(1, 6)]
        pd = sum(float(r["PERTPD"]) for r in rows)
        cost = sum(int(r["EstimatedLaborVND"]) for r in rows)
        self.assertAlmostEqual(pd, 17.51, places=2)
        self.assertAlmostEqual(cost / 1e6, 42.97, places=2)
        self.assertAlmostEqual(cost * 1.18 / 1e6, 50.70, places=2)
        for r in rows:
            o, ml, p = (float(r[k]) for k in ("OptimisticPD", "MostLikelyPD", "PessimisticPD"))
            self.assertAlmostEqual((o + 4 * ml + p) / 6, float(r["PERTPD"]), places=2, msg=r["WBS"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
