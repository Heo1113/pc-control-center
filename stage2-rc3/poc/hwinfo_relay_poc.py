#!/usr/bin/env python3
"""
IRON Remote RC3 — Read-Only Authenticated Relay Server PoC (Python 3 stdlib only)
- 바인딩: 127.0.0.1:8765 (루프백 전용). RC3: LAN 노출(--allow-lan)은 사용자 별도 승인 전까지 비활성 — 지정하면 종료
- 상류(HWiNFO) 주소도 루프백만 허용
- /api/ping 은 캐시하지 않음(브라우저가 응답의 t 로 시계 오프셋을 계산하므로)
- 고정 테스트 토큰 제거 -> 실행별 무작위 토큰(secrets.token_urlsafe(24)) 자동 생성
- 포트 8080(Basalt) 사용 금지 및 실행 전 포트 점유 확인 (충돌 시 기존 서비스 종료/변경 없이 Exit 1)
- 읽기 전용 강제: GET /api/ping, /api/sensors/read, /api/sensors/sdb 만 허용, 모든 POST/PUT/DELETE 403 차단
- 로컬 정적 리소스(/, /assets/*) 동일 출처 서빙 (외부 CDN 의존성 0)
"""
import argparse
import hmac
import http.server
import json
import mimetypes
import os
import secrets
import socket
import ssl
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ALLOWED_GET_PATHS = {"/api/ping", "/api/sensors/read", "/api/sensors/sdb"}
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}
FORBIDDEN_PORTS = {8080}  # Basalt


def check_loopback_url(url: str) -> None:
    """상류 HWiNFO 주소: http(s), 루프백, 8080 아님. 위반이면 ValueError."""
    u = urllib.parse.urlparse(url)
    if u.scheme not in ("http", "https") or (u.hostname or "") not in LOOPBACK_HOSTS:
        raise ValueError(f"상류 HWiNFO 주소는 루프백 http(s)만 허용합니다: {url}")
    if (u.port or (443 if u.scheme == "https" else 80)) in FORBIDDEN_PORTS:
        raise ValueError("포트 8080은 Basalt 운영 포트입니다.")


def is_port_in_use(host: str, port: int) -> bool:
    check_host = "127.0.0.1" if host in ("0.0.0.0", "") else host
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.5)
        return s.connect_ex((check_host, port)) == 0


class RelayCache:
    def __init__(self, hwinfo_url: str, ttl_sec: float = 0.5, insecure_tls: bool = True):
        self.hwinfo_url = hwinfo_url.rstrip("/")
        self.ttl_sec = ttl_sec
        self.lock = threading.Lock()
        self.cache = {}
        self.ssl_ctx = ssl._create_unverified_context() if insecure_tls else ssl.create_default_context()

    def get(self, path: str):
        now = time.monotonic()
        with self.lock:
            entry = self.cache.get(path)
            if path != "/api/ping" and entry and (now - entry["ts"]) < self.ttl_sec:
                return entry["status"], entry["content_type"], entry["body"]

        req = urllib.request.Request(f"{self.hwinfo_url}{path}", headers={"Cache-Control": "no-cache"})
        try:
            with urllib.request.urlopen(req, timeout=4.0, context=self.ssl_ctx) as resp:
                body = resp.read()
                status = resp.status
                ctype = resp.headers.get("Content-Type", "application/json; charset=utf-8")
        except urllib.error.HTTPError as e:
            body = e.read()
            status = e.code
            ctype = e.headers.get("Content-Type", "text/plain; charset=utf-8")
        except Exception as e:
            body = json.dumps({"error": f"HWiNFO upstream unreachable: {e}"}).encode("utf-8")
            status = 503
            ctype = "application/json; charset=utf-8"

        with self.lock:
            self.cache[path] = {"ts": now, "status": status, "content_type": ctype, "body": body}
        return status, ctype, body


def make_handler(cache: RelayCache, token: str, html_path: str, assets_dir: str, quiet: bool = False):
    expected_auth = f"Bearer {token}"

    class Handler(http.server.BaseHTTPRequestHandler):
        def _send(self, status: int, ctype: str, body: bytes):
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _check_auth(self) -> bool:
            auth = self.headers.get("Authorization", "")
            return hmac.compare_digest(auth, expected_auth)

        def _reject_write(self):
            self._send(403, "application/json; charset=utf-8", b'{"error":"Forbidden: IRON Relay is strictly read-only"}')

        def do_GET(self):
            path = self.path.split("?", 1)[0]
            if path in ("/", "/index.html"):
                if os.path.isfile(html_path):
                    with open(html_path, "rb") as f:
                        self._send(200, "text/html; charset=utf-8", f.read())
                else:
                    self._send(404, "text/plain; charset=utf-8", b"IRON_REMOTE_PROTOTYPE.html not found")
                return

            if path.startswith("/assets/"):
                rel = os.path.normpath(path[len("/assets/"):]).lstrip(os.sep)
                full = os.path.abspath(os.path.join(assets_dir, rel))
                base = os.path.abspath(assets_dir)
                if full.startswith(base + os.sep) and os.path.isfile(full):
                    ctype = mimetypes.guess_type(full)[0] or "application/octet-stream"
                    with open(full, "rb") as f:
                        self._send(200, ctype, f.read())
                else:
                    self._send(404, "text/plain; charset=utf-8", b"Asset Not Found")
                return

            if path in ALLOWED_GET_PATHS:
                if not self._check_auth():
                    self._send(401, "application/json; charset=utf-8", b'{"error":"Unauthorized: valid Bearer token required"}')
                    return
                status, ctype, body = cache.get(path)
                self._send(status, ctype, body)
                return

            self._send(404, "text/plain; charset=utf-8", b"Not Found")

        def do_POST(self):
            self._reject_write()

        def do_PUT(self):
            self._reject_write()

        def do_DELETE(self):
            self._reject_write()

        def do_PATCH(self):
            self._reject_write()

        def log_message(self, fmt, *args):
            if not quiet:
                print(f"[IRON-Relay] {self.address_string()} - {fmt % args}")

    return Handler


def create_server(hwinfo_url: str, bind: str, port: int, token: str, html_path: str, assets_dir: str, quiet: bool = False):
    if bind not in LOOPBACK_HOSTS:
        raise ValueError("RC3: 릴레이는 루프백에만 바인딩합니다(LAN 노출은 별도 승인 전 금지).")
    if port in FORBIDDEN_PORTS:
        raise ValueError("포트 8080은 Basalt 운영 포트입니다.")
    check_loopback_url(hwinfo_url)
    cache = RelayCache(hwinfo_url)
    return http.server.ThreadingHTTPServer(
        (bind, port),
        make_handler(cache, token, os.path.abspath(html_path), os.path.abspath(assets_dir), quiet=quiet),
    )


def main():
    parser = argparse.ArgumentParser(description="IRON Remote RC3 Read-Only Authenticated Relay PoC (loopback only)")
    parser.add_argument("--hwinfo-url", default="http://127.0.0.1:8764", help="Local HWiNFO REST API URL (default: 127.0.0.1:8764)")
    parser.add_argument("--bind", default="127.0.0.1", help="Bind IP (loopback only: 127.0.0.1, localhost, ::1)")
    parser.add_argument("--allow-lan", action="store_true", help="RC3에서는 비활성(지정하면 종료). LAN 노출은 사용자 별도 승인 전 금지")
    parser.add_argument("--port", type=int, default=8765, help="Relay HTTP port (default: 8765, 8080 forbidden)")
    parser.add_argument("--html", default=os.path.join(os.path.dirname(__file__), "..", "IRON_REMOTE_PROTOTYPE.html"))
    parser.add_argument("--assets-dir", default=os.path.join(os.path.dirname(__file__), "..", "assets"))
    args = parser.parse_args()

    if args.port == 8080:
        print("[ERROR] 포트 8080은 Basalt 운영 포트이므로 사용할 수 없습니다. 8765를 사용하세요.", file=sys.stderr)
        sys.exit(1)

    if args.allow_lan:
        print("[ERROR] RC3: --allow-lan(LAN 노출)은 사용자 별도 승인 전까지 비활성입니다.", file=sys.stderr)
        sys.exit(1)
    bind_host = args.bind
    if bind_host not in LOOPBACK_HOSTS:
        print("[ERROR] 루프백(127.0.0.1/localhost/::1)에만 바인딩합니다.", file=sys.stderr)
        sys.exit(1)
    try:
        check_loopback_url(args.hwinfo_url)
    except ValueError as e:
        print(f"[ERROR] {e}", file=sys.stderr)
        sys.exit(1)

    if is_port_in_use(bind_host, args.port):
        print(f"[ERROR] 포트 {args.port}가 이미 점유 중입니다. 기존 서비스를 종료/변경하지 않고 중단합니다.", file=sys.stderr)
        sys.exit(1)

    runtime_token = secrets.token_urlsafe(24)
    server = create_server(args.hwinfo_url, bind_host, args.port, runtime_token, args.html, args.assets_dir)
    print(f"[IRON Remote RC3 Relay] Listening on http://{bind_host}:{args.port} -> Upstream {args.hwinfo_url}")
    print("[INFO] 루프백 전용 모드. LAN 노출은 사용자 별도 승인 전 금지.")
    print(f"[AUTH] Per-Run Random Bearer Token: {runtime_token}")
    print("[SECURITY] Read-Only proxy (POST/PUT/DELETE -> 403), Bearer auth required on /api/*")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[IRON Remote RC3 Relay] Shutting down cleanly.")
        server.server_close()


if __name__ == "__main__":
    main()
