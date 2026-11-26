#!/usr/bin/env python3
"""
IRON Stage 2 RC3 — 기준 일관성·교차 구현 일치·수집기 정책 테스트 (모두 SYNTHETIC 데이터, 실측 아님)

  python -I -m unittest discover -s tests -p "test_*.py" -v

도구가 없으면 해당 테스트는 건너뛴다(SKIP 사유를 출력): node, dotnet(.NET 10 SDK), pwsh/powershell.
Stage 1 기준 추출 비교는 저장소 경로를 IRON_STAGE1_SRC 환경 변수로 주면 실행한다.
"""
import glob
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PY = [sys.executable, "-I"]
ENV = dict(os.environ, DOTNET_ROLL_FORWARD="Major", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")


def run(cmd, timeout=300, **kw):
    return subprocess.run(cmd, cwd=kw.pop("cwd", ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, env=kw.pop("env", ENV), **kw)


def find_pwsh():
    for c in ("pwsh", os.path.expanduser("~/.dotnet/tools/pwsh"), "powershell"):
        p = shutil.which(c) or (c if os.path.isfile(c) else None)
        if p:
            return p
    return None


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def read_text(path, encoding="utf-8"):
    with open(path, encoding=encoding) as f:
        return f.read()


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


NODE = shutil.which("node")
DOTNET = shutil.which("dotnet")
PWSH = find_pwsh()
_POC = {}


def poc_dll():
    """PoC를 한 번만 빌드해 dll 경로를 돌려준다."""
    if "dll" not in _POC:
        out = os.path.join(tempfile.mkdtemp(prefix="iron_rc3_poc_"), "bin")
        r = run([DOTNET, "build", os.path.join(ROOT, "poc", "HWiNfoRestPoC.csproj"), "-c", "Release", "-o", out, "-nologo"], timeout=600)
        if r.returncode != 0:
            raise AssertionError("PoC 빌드 실패:\n" + r.stdout[-3000:] + r.stderr[-2000:])
        if " 0 Warning(s)" not in r.stdout:
            raise AssertionError("PoC 빌드 경고가 있음:\n" + r.stdout[-3000:])
        _POC["dll"] = os.path.join(out, "HWiNfoRestPoC.dll")
    return _POC["dll"]


class ConfigConsistency(unittest.TestCase):
    def test_browser_config_matches_json(self):
        r = run(PY + ["tools/build_iron_config.py", "--check"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fixtures_are_reproducible(self):
        r = run(PY + ["tools/make_synthetic_fixtures.py", "--check"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_thresholds_are_stage1_values(self):
        th = load(os.path.join(ROOT, "config", "iron_stage1_thresholds.json"))
        self.assertEqual(th["gpuTemperatureC"], {"default": 72.0, "min": 55.0, "max": 95.0, "decimals": 0})
        self.assertEqual(th["wireViewPinCurrentA"], {"default": 7.0, "min": 4.0, "max": 9.0, "decimals": 1})
        self.assertEqual((th["pinApproachingShare"], th["pinLatchReadings"], th["pinScaleMaxA"]), (0.72, 3, 9.5))
        # RC2가 쓰던 Stage 1에 없는 값이 남아 있지 않아야 한다
        html = read_text(os.path.join(ROOT, "IRON_REMOTE_PROTOTYPE.html"))
        for stale in ("pinWarnAmp", "pinCritAmp", "imbalanceWarnPct", "connTempWarnC"):
            self.assertFalse(stale in html, f"HTML에 Stage 1에 없는 기준 이름 {stale} 이 남아 있음")

    @unittest.skipUnless(os.environ.get("IRON_STAGE1_SRC"), "IRON_STAGE1_SRC(Stage 1 소스 경로) 미지정")
    def test_thresholds_match_stage1_source(self):
        r = run(PY + ["tools/extract_stage1_thresholds.py", "--stage1-src", os.environ["IRON_STAGE1_SRC"]])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_collector_has_no_hardcoded_gate_numbers(self):
        ps = read_text(os.path.join(ROOT, "poc", "Collect-HWiNfoRest.ps1"), "utf-8-sig")
        for literal in ("300", "250", "6000", "99.0", "3.0", "600"):
            self.assertNotIn(" " + literal + " ", ps.replace(")", " ").replace("(", " "), f"수집기에 기준값 {literal} 이 직접 적혀 있음")
        with open(os.path.join(ROOT, "poc", "Collect-HWiNfoRest.ps1"), "rb") as f:
            self.assertTrue(f.read(3) == b"\xef\xbb\xbf", "Windows PowerShell 5.1용 UTF-8 BOM 필요")

    def test_relay_is_loopback_only(self):
        sys.path.insert(0, os.path.join(ROOT, "poc"))
        import hwinfo_relay_poc as relay
        with self.assertRaises(ValueError):
            relay.create_server("http://127.0.0.1:8764", "0.0.0.0", free_port(), "t", "x", ROOT)
        with self.assertRaises(ValueError):
            relay.create_server("http://192.168.0.10:8764", "127.0.0.1", free_port(), "t", "x", ROOT)
        with self.assertRaises(ValueError):
            relay.create_server("http://127.0.0.1:8080", "127.0.0.1", free_port(), "t", "x", ROOT)
        r = run(PY + ["poc/hwinfo_relay_poc.py", "--allow-lan", "--port", str(free_port())], timeout=30)
        self.assertEqual(r.returncode, 1)
        self.assertIn("--allow-lan", r.stderr)


@unittest.skipUnless(NODE, "node 없음")
class JsModel(unittest.TestCase):
    def test_node_unit_tests(self):
        r = run([NODE, "--test", "tests/js/model.test.js"])
        self.assertEqual(r.returncode, 0, r.stdout[-4000:] + r.stderr[-2000:])

    def test_expected_outputs_up_to_date(self):
        r = run([NODE, "tools/run_model.js", "--check"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


@unittest.skipUnless(DOTNET, "dotnet(.NET 10 SDK) 없음")
class DotnetPoc(unittest.TestCase):
    def test_selftest_matches_js_expected(self):
        out = tempfile.mkdtemp(prefix="iron_rc3_self_")
        r = run([DOTNET, poc_dll(), "selftest", "--root", ROOT, "--out", out])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        # 독립 비교(파이썬): .NET 출력 == JS 기대값
        for f in glob.glob(os.path.join(ROOT, "tests", "fixtures", "expected", "*.json")):
            self.assertEqual(load(os.path.join(out, os.path.basename(f))), load(f), os.path.basename(f))

    def test_synthetic_run_is_never_pass(self):
        d = tempfile.mkdtemp(prefix="iron_rc3_run_")
        shutil.copytree(os.path.join(ROOT, "tests", "fixtures", "run_synthetic"), d, dirs_exist_ok=True)
        r = run([DOTNET, poc_dll(), "analyze", d, "--root", ROOT])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        rep = load(os.path.join(d, "gate_report.json"))
        for g in ("G1", "G2", "G3A"):
            self.assertEqual(rep[g]["verdict"], "UNVERIFIED", g)
        self.assertEqual(rep["G3B"]["verdict"], "NOT_RUN")
        self.assertTrue(rep["G2"]["criteriaMet"])
        g1 = {c["name"]: c for c in rep["G1"]["checks"]}
        self.assertEqual((g1["samples"]["value"], g1["successRatePct"]["value"], g1["rttP95Ms"]["value"]), (12, 91.67, 250))
        self.assertEqual(g1["hwinfoCpuOverheadPct"]["value"], 0.44)
        self.assertTrue(os.path.isfile(os.path.join(d, "sensor_inventory.csv")))

    def test_live_refuses_basalt_port_and_lan(self):
        d = tempfile.mkdtemp(prefix="iron_rc3_live_")
        for url in ("http://127.0.0.1:8080", "http://192.168.0.10:8764"):
            r = run([DOTNET, poc_dll(), "live", url, "3", d, "--root", ROOT], timeout=60)
            self.assertEqual(r.returncode, 1, url + r.stdout + r.stderr)


@unittest.skipUnless(PWSH, "pwsh/powershell 없음")
class Collector(unittest.TestCase):
    PS = os.path.join(ROOT, "poc", "Collect-HWiNfoRest.ps1")

    def ps(self, *args, timeout=180):
        return run([PWSH, "-NoProfile", "-File", self.PS, *args], timeout=timeout)

    def test_policy_refusals(self):
        out = tempfile.mkdtemp(prefix="iron_rc3_ps_")
        self.assertEqual(self.ps("-Mode", "Collect", "-BaseUrl", "http://127.0.0.1:8080", "-OutRoot", out).returncode, 1)
        self.assertEqual(self.ps("-Mode", "Collect", "-BaseUrl", "http://192.168.0.10:8764", "-OutRoot", out).returncode, 1)
        self.assertEqual(self.ps("-Mode", "Collect", "-Gate", "G3A", "-BaseUrl", f"http://127.0.0.1:{free_port()}", "-OutRoot", out).returncode, 1)
        self.assertEqual(os.listdir(out), [], "거부된 실행은 결과 폴더를 만들지 않아야 함")

    @unittest.skipUnless(DOTNET, "dotnet 없음")
    def test_analyze_matches_dotnet_on_synthetic_run(self):
        d = tempfile.mkdtemp(prefix="iron_rc3_run_")
        shutil.copytree(os.path.join(ROOT, "tests", "fixtures", "run_synthetic"), d, dirs_exist_ok=True)
        r = self.ps("-Mode", "Analyze", "-RunDir", d)
        self.assertEqual(r.returncode, 4, r.stdout + r.stderr)  # SYNTHETIC → UNVERIFIED
        run([DOTNET, poc_dll(), "analyze", d, "--root", ROOT])
        self.assertEqual(load(os.path.join(d, "g1_report.json")), load(os.path.join(d, "gate_report.json"))["G1"])

    def _mock(self, log):
        port = free_port()
        p = subprocess.Popen(PY + [os.path.join(ROOT, "tests", "mock_hwinfo_server.py"), "--port", str(port), "--fixtures", os.path.join(ROOT, "tests", "fixtures"), "--log", log])
        for _ in range(50):
            try:
                socket.create_connection(("127.0.0.1", port), 0.2).close()
                break
            except OSError:
                time.sleep(0.1)
        self.addCleanup(lambda: (p.terminate(), p.wait(5)))
        return port

    def test_collect_against_mock_server(self):
        tmp = tempfile.mkdtemp(prefix="iron_rc3_e2e_")
        log = os.path.join(tmp, "requests.log")
        port = self._mock(log)
        out = os.path.join(tmp, "results")
        r = self.ps("-Mode", "Collect", "-BaseUrl", f"http://127.0.0.1:{port}", "-Samples", "8", "-IntervalMs", "500", "-OutRoot", out)
        self.assertEqual(r.returncode, 4, r.stdout + r.stderr)
        runs = glob.glob(os.path.join(out, "run_G1_*"))
        self.assertEqual(len(runs), 1)
        d = runs[0]
        man = load(os.path.join(d, "run_manifest.json"))
        self.assertEqual(man["source"], "SYNTHETIC")
        with open(os.path.join(d, "samples_all.jsonl"), encoding="utf-8") as f:
            lines = [json.loads(x) for x in f if x.strip()]
        self.assertEqual(len(lines), 8)
        self.assertTrue(all(x["ok"] for x in lines))
        # 응답에 charset이 없어도 '°C'가 깨지지 않아야 한다(SDB 단위 매핑의 전제)
        self.assertIn('"Unit": "°C"', read_text(os.path.join(d, "sdb.json")))
        with open(log, encoding="utf-8") as f:
            reqs = [x.split() for x in f if x.strip()]
        self.assertTrue(all(m == "GET" for m, _ in reqs), reqs)
        self.assertLessEqual({p for _, p in reqs}, {"/api/ping", "/api/sensors/sdb", "/api/sensors/read"})
        if DOTNET:
            run([DOTNET, poc_dll(), "analyze", d, "--root", ROOT])
            rep = load(os.path.join(d, "gate_report.json"))
            self.assertEqual(load(os.path.join(d, "g1_report.json")), rep["G1"])
            self.assertEqual(rep["G1"]["verdict"], "UNVERIFIED")
            self.assertTrue(rep["G2"]["criteriaMet"])

    @unittest.skipIf(os.name == "nt", "가짜 프로세스 이름 시험은 Linux/macOS에서만")
    def test_wireview_client_running_blocks_collection(self):
        tmp = tempfile.mkdtemp(prefix="iron_rc3_wv_")
        fake = os.path.join(tmp, "WireView")
        shutil.copy(shutil.which("sleep"), fake)
        proc = subprocess.Popen([fake, "60"])
        self.addCleanup(lambda: (proc.kill(), proc.wait(5)))
        port = self._mock(os.path.join(tmp, "requests.log"))
        out = os.path.join(tmp, "results")
        r = self.ps("-Mode", "Collect", "-BaseUrl", f"http://127.0.0.1:{port}", "-Samples", "3", "-IntervalMs", "500", "-OutRoot", out)
        self.assertEqual(r.returncode, 5, r.stdout + r.stderr)
        self.assertIsNone(proc.poll(), "수집기는 다른 프로세스를 종료하면 안 됨")
        self.assertEqual(glob.glob(os.path.join(out, "*", "samples_all.jsonl")), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
