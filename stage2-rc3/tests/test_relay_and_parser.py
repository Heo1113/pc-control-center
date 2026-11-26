#!/usr/bin/env python3
"""
IRON Stage 2 RC2 — Automated Unit & Negative Security Tests
- Python 표준 라이브러리(unittest)만 사용, 외부 네트워크/CDN/실제 포트 8080/8764/8765 무간섭
- 검증 항목:
  1) 인증 부정 테스트 (무토큰 401, 잘못된 토큰 401, 유효 무작위 토큰 200)
  2) 쓰기 차단 부정 테스트 (POST /api/sensors/filter/apply, filter/reset, logging/start -> 403)
  3) 잘못된 경로 및 디렉터리 트래버설 차단 (404)
  4) 로컬 정적 리소스 서빙 (/, /assets/iron-remote.css) & HTML 내 외부 CDN 링크 0건 검증
  5) SDB 정확한 메타데이터 키 제외 회귀 테스트 (Tctl/Tdie, Total CPU Usage, Total Power, Thermal Grizzly 누락 방지)
  6) Read↔SDB 100% 매핑 및 타임스탬프 단조성 검증
"""
import http.client
import http.server
import json
import os
import re
import secrets
import sys
import threading
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.join(ROOT, "poc"))
import hwinfo_relay_poc as relay

FIXTURES_DIR = os.path.join(ROOT, "tests", "fixtures")
SDB_META_KEYS = {"tSensorLocation", "tReadingLocation", "tReadingType"}


def load_fixture(name: str):
    with open(os.path.join(FIXTURES_DIR, name), "r", encoding="utf-8") as f:
        return json.load(f)


class MockHWiNfoHandler(http.server.BaseHTTPRequestHandler):
    sdb_bytes = json.dumps(load_fixture("synthetic_sdb_v1.json")).encode("utf-8")
    read_bytes = json.dumps(load_fixture("synthetic_read_v1.json")).encode("utf-8")

    def do_GET(self):
        if self.path == "/api/ping":
            body = b'{"pong":true,"t":1795464600100.123}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == "/api/sensors/sdb":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(self.sdb_bytes)))
            self.end_headers()
            self.wfile.write(self.sdb_bytes)
        elif self.path == "/api/sensors/read":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(self.read_bytes)))
            self.end_headers()
            self.wfile.write(self.read_bytes)
        else:
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b"Not Found")

    def log_message(self, fmt, *args):
        pass


class TestIronStage2RC2(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.mock_srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), MockHWiNfoHandler)
        cls.mock_port = cls.mock_srv.server_address[1]
        cls.mock_thread = threading.Thread(target=cls.mock_srv.serve_forever, daemon=True)
        cls.mock_thread.start()

        cls.token = secrets.token_urlsafe(24)
        html_path = os.path.join(ROOT, "IRON_REMOTE_PROTOTYPE.html")
        assets_dir = os.path.join(ROOT, "assets")
        cls.relay_srv = relay.create_server(
            f"http://127.0.0.1:{cls.mock_port}", "127.0.0.1", 0, cls.token, html_path, assets_dir, quiet=True
        )
        cls.relay_port = cls.relay_srv.server_address[1]
        cls.relay_thread = threading.Thread(target=cls.relay_srv.serve_forever, daemon=True)
        cls.relay_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.relay_srv.shutdown()
        cls.relay_srv.server_close()
        cls.mock_srv.shutdown()
        cls.mock_srv.server_close()

    def _req(self, method: str, path: str, headers=None, body=b""):
        conn = http.client.HTTPConnection("127.0.0.1", self.relay_port, timeout=5)
        conn.request(method, path, body=body, headers=headers or {})
        resp = conn.getresponse()
        data = resp.read()
        conn.close()
        return resp.status, data

    def test_01_auth_negative_missing_and_invalid_token(self):
        st1, _ = self._req("GET", "/api/sensors/read")
        self.assertEqual(st1, 401)
        st2, _ = self._req("GET", "/api/sensors/read", headers={"Authorization": "Bearer WRONG-TOKEN"})
        self.assertEqual(st2, 401)

    def test_02_auth_positive_random_token(self):
        st, body = self._req("GET", "/api/sensors/read", headers={"Authorization": f"Bearer {self.token}"})
        self.assertEqual(st, 200)
        parsed = json.loads(body.decode("utf-8"))
        self.assertEqual(parsed["Version"], 1)

    def test_03_write_endpoints_blocked_with_403(self):
        auth = {"Authorization": f"Bearer {self.token}", "Content-Type": "text/plain"}
        for path in ("/api/sensors/filter/apply", "/api/sensors/filter/reset", "/api/sensors/logging/start", "/api/sensors/logging/setup"):
            st, _ = self._req("POST", path, headers=auth, body=b'{"Version":1}')
            self.assertEqual(st, 403, f"Expected 403 for POST {path}")
        st_del, _ = self._req("DELETE", "/api/sensors/read", headers=auth)
        self.assertEqual(st_del, 403)

    def test_04_invalid_path_and_traversal_blocked(self):
        auth = {"Authorization": f"Bearer {self.token}"}
        st1, _ = self._req("GET", "/api/unknown", headers=auth)
        self.assertEqual(st1, 404)
        st2, _ = self._req("GET", "/assets/../../poc/hwinfo_relay_poc.py")
        self.assertEqual(st2, 404)

    def test_05_local_html_and_assets_zero_cdn(self):
        st_html, html_bytes = self._req("GET", "/")
        self.assertEqual(st_html, 200)
        html_str = html_bytes.decode("utf-8")
        # 외부 CDN 스크립트/스타일시트 참조가 0건이어야 함
        self.assertIsNone(re.search(r'<script[^>]+src=["\']https?://', html_str, re.I))
        self.assertIsNone(re.search(r'<link[^>]+href=["\']https?://', html_str, re.I))
        st_css, _ = self._req("GET", "/assets/iron-remote.css")
        self.assertEqual(st_css, 200)

    def test_06_sdb_exact_metadata_exclusion_and_read_mapping(self):
        sdb = load_fixture("synthetic_sdb_v1.json")
        read = load_fixture("synthetic_read_v1.json")
        # Timestamp monotonicity check
        self.assertLessEqual(read["Timestamp RS ms"], read["Timestamp ms"])
        self.assertLessEqual(read["Timestamp ms"], read["Timestamp RE ms"])

        sdb_keys = set()
        def walk_sdb(node, sensor, path):
            for k, v in node.items():
                if not isinstance(v, dict) or k in SDB_META_KEYS:
                    continue
                if "hide" in v:
                    sdb_keys.add(f"{sensor}|{' / '.join(path)}|{k}")
                else:
                    walk_sdb(v, sensor, path + [k])

        for top_k, top_v in sdb.items():
            if isinstance(top_v, dict):
                walk_sdb(top_v, top_k, [])

        # Regression check: t-prefixed sensors/readings MUST be preserved!
        self.assertIn("CPU [#0]: AMD Ryzen 7 9800X3D|Tctl/Tdie|1", sdb_keys)
        self.assertIn("CPU [#0]: AMD Ryzen 7 9800X3D|Total CPU Usage|7", sdb_keys)
        self.assertIn("Thermal Grizzly WireView Pro II|Total Power|5", sdb_keys)

        read_keys = set()
        def walk_read(node, sensor, path):
            for k, v in node.items():
                if not isinstance(v, dict):
                    continue
                if "Value" in v:
                    is_type = k.isdigit()
                    rpath = path if is_type else path + [k]
                    tkey = k if is_type else ""
                    read_keys.add(f"{sensor}|{' / '.join(rpath)}|{tkey}")
                else:
                    walk_read(v, sensor, path + [k])

        for top_k, top_v in read.items():
            if isinstance(top_v, dict):
                walk_read(top_v, top_k, [])

        self.assertTrue(read_keys.issubset(sdb_keys), f"Unmapped read keys: {read_keys - sdb_keys}")


if __name__ == "__main__":
    unittest.main()
