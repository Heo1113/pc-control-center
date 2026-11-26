#!/usr/bin/env python3
"""시험용 모의 HWiNFO REST 서버 (SYNTHETIC). 127.0.0.1 에만 바인딩한다.

공식 문서의 GET 세 개만 흉내 낸다: /api/ping, /api/sensors/read, /api/sensors/sdb.
read 의 타임스탬프는 2초마다 증가(HWiNFO 갱신 주기 흉내). 응답에는 SYNTHETIC 표시(_notice)가 있어
수집기가 출처를 SYNTHETIC 으로 기록해야 한다. 받은 요청(메서드·경로)은 --log 파일에 남긴다.

  python -I tests/mock_hwinfo_server.py --port 18764 --fixtures tests/fixtures --log /tmp/req.log
"""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--fixtures", required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--freeze-after-s", type=float, default=0, help="이 시간 뒤 타임스탬프 정지(STALE 시험)")
    ap.add_argument("--fail-every", type=int, default=0, help="read N번마다 503 일반 텍스트(공식 문서의 오류 형식)")
    ap.add_argument("--bad-json-every", type=int, default=0, help="read N번마다 잘린 JSON")
    ap.add_argument("--sdb-version", type=int, default=1, help="SDB Version 값(불일치 시험)")
    a = ap.parse_args()
    counter = {"read": 0}
    fx = Path(a.fixtures)
    read = json.loads((fx / "synthetic_full_read_v1.json").read_text(encoding="utf-8"))
    sdb_text = (fx / "synthetic_full_sdb_v1.json").read_text(encoding="utf-8")
    start = time.time() * 1000.0
    log = open(a.log, "a", encoding="utf-8")

    class H(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, code, body, ctype="application/json"):
            data = body.encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", ctype)  # charset 없음: 수집기가 UTF-8로 직접 해석하는지 확인
            self.send_header("Access-Control-Allow-Origin", "*")  # 공식 문서: CORS *, 사전 요청 없음
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _log(self):
            log.write(f"{self.command} {self.path}\n")
            log.flush()

        def do_GET(self):
            self._log()
            now = time.time() * 1000.0
            if self.path == "/api/ping":
                return self._send(200, json.dumps({"pong": True, "t": now}))
            if self.path == "/api/sensors/sdb":
                if a.sdb_version != 1:
                    d = json.loads(sdb_text)
                    d["Version"] = a.sdb_version
                    return self._send(200, json.dumps(d, ensure_ascii=False))
                return self._send(200, sdb_text)
            if self.path == "/api/sensors/read":
                counter["read"] += 1
                n = counter["read"]
                if a.fail_every and n % a.fail_every == 0:
                    return self._send(503, "Service Unavailable (synthetic)", "text/plain")
                el = now - start
                if a.freeze_after_s and el > a.freeze_after_s * 1000:
                    el = a.freeze_after_s * 1000
                ts = start + (el // 2000) * 2000 + 5.0
                doc = dict(read)
                doc["Timestamp RS ms"] = ts - 110.0
                doc["Timestamp ms"] = ts
                doc["Timestamp RE ms"] = ts + 25.0
                body = json.dumps(doc, ensure_ascii=False, indent=1)
                if a.bad_json_every and n % a.bad_json_every == 0:
                    body = body[: len(body) // 2]
                return self._send(200, body)
            return self._send(404, "Not found", "text/plain")

        def _deny(self):
            self._log()
            self._send(405, "Method not allowed", "text/plain")

        do_POST = do_PUT = do_DELETE = do_PATCH = _deny

    ThreadingHTTPServer(("127.0.0.1", a.port), H).serve_forever()


if __name__ == "__main__":
    main()
