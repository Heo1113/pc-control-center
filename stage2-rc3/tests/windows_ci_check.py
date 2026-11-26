#!/usr/bin/env python3
"""
IRON RC3 — Windows 호환성 검증 하네스 (GitHub Actions windows 러너용, 모의 HWiNFO 서버만 사용 = SYNTHETIC)

Linux에서 이미 확인한 내용(정책 로직, 모델 일치 등)은 반복하지 않고, Windows에서만 달라지는 경로를 본다.
  - Windows PowerShell 5.1(JavaScriptSerializer, Invoke-WebRequest 바이트 해석, BOM 스크립트)과 PowerShell 7
  - Get-NetTCPConnection 포트 소유자 확인, Get-Process CPU 시간, PnP·레지스트리 조회
  - 정상 · 오류(503, 잘린 JSON, SDB Version 2) · 연결 해제 · 재연결
  - C# PoC의 Windows 실행(selftest, live 정상, live 연결 해제·재연결)

  python -I tests/windows_ci_check.py --suite main    --out ci-out --poc <HWiNfoRestPoC.dll>
  python -I tests/windows_ci_check.py --suite culture --out ci-out --poc <HWiNfoRestPoC.dll>

모의 서버를 'HWiNFO64.exe'(python.exe 복사본)로 실행해 수집기의 소유자 확인을 통과시키되, 응답의 SYNTHETIC 표시 때문에
출처는 SYNTHETIC이 되어야 한다(어떤 경우에도 PASS가 나오면 안 됨). 실제 HWiNFO·WireView는 사용하지 않는다.
"""
import argparse
import glob
import json
import os
import shutil
import socket
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PS1 = os.path.join(ROOT, "poc", "Collect-HWiNfoRest.ps1")
MOCK = os.path.join(ROOT, "tests", "mock_hwinfo_server.py")
FX = os.path.join(ROOT, "tests", "fixtures")
IS_WIN = os.name == "nt"
HW_PORT = 8764
RESULTS = []


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def read_text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def jsonl(path):
    with open(path, encoding="utf-8-sig") as f:
        return [json.loads(x) for x in f if x.strip()]


def complete_lines(path):
    """쓰는 중인 파일에서 줄바꿈으로 끝난(완성된) 줄 수만 센다."""
    try:
        with open(path, "rb") as f:
            return f.read().count(b"\n")
    except OSError:
        return 0


def port_open(port):
    try:
        socket.create_connection(("127.0.0.1", port), 0.3).close()
        return True
    except OSError:
        return False


def wait_port(port, up=True, timeout=10):
    end = time.time() + timeout
    while time.time() < end:
        if port_open(port) == up:
            return True
        time.sleep(0.1)
    return False


class Env:
    def __init__(self, out, poc):
        self.out = os.path.abspath(out)
        self.poc = poc
        os.makedirs(self.out, exist_ok=True)
        self.base_py = os.path.join(sys.base_prefix, "python.exe") if IS_WIN else sys.executable
        self.procs = []

    def fake(self, name):
        """python 실행 파일을 다른 이름으로 실행(프로세스 이름 흉내). Windows는 같은 폴더에 복사(DLL 탐색),
        그 밖에는 심볼릭 링크(프로세스 이름은 링크 이름, 표준 라이브러리는 원래 위치에서 찾음)."""
        if IS_WIN:
            dst = os.path.join(os.path.dirname(self.base_py), name + ".exe")
            if not os.path.exists(dst):
                shutil.copy2(self.base_py, dst)
            return dst
        d = os.path.join(self.out, "_bin")
        os.makedirs(d, exist_ok=True)
        dst = os.path.join(d, name)
        if not os.path.lexists(dst):
            os.symlink(os.path.realpath(self.base_py), dst)
        return dst

    def env(self):
        return dict(os.environ, DOTNET_ROLL_FORWARD="Major", DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")

    def mock(self, exe, port, *extra, tag="mock"):
        log = os.path.join(self.out, f"{tag}_{port}_{int(time.time() * 1000)}.log")
        p = subprocess.Popen([exe, "-I", MOCK, "--port", str(port), "--fixtures", FX, "--log", log, *extra], env=self.env())
        self.procs.append(p)
        if not wait_port(port, True):
            raise RuntimeError(f"모의 서버가 {port}에서 열리지 않음")
        return p

    def idle(self, exe):
        p = subprocess.Popen([exe, "-I", "-c", "import time; time.sleep(120)"], env=self.env())
        self.procs.append(p)
        time.sleep(0.5)
        return p

    @staticmethod
    def stop(p, port=None):
        if p.poll() is None:
            p.terminate()
            try:
                p.wait(10)
            except subprocess.TimeoutExpired:
                p.kill()
        if port:
            wait_port(port, False)

    def cleanup(self):
        for p in self.procs:
            self.stop(p)


def ps_cmd(edition):
    exe = "powershell.exe" if edition == "ps51" else "pwsh"
    return [exe, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", PS1]


def run(cmd, env, timeout=300):
    r = subprocess.run(cmd, capture_output=True, timeout=timeout, env=env)
    out = (r.stdout + r.stderr).decode("utf-8", errors="replace")
    return r.returncode, out


def newest(root, pattern):
    c = sorted(glob.glob(os.path.join(root, pattern)), key=os.path.getmtime)
    return c[-1] if c else None


class Scenario:
    def __init__(self, suite, edition, name):
        self.suite, self.edition, self.name = suite, edition, name
        self.checks = []
        self.info = {}

    def check(self, label, ok, detail=""):
        self.checks.append({"check": label, "ok": bool(ok), "detail": str(detail)[:400]})
        return ok

    def done(self):
        ok = bool(self.checks) and all(c["ok"] for c in self.checks)
        RESULTS.append({"suite": self.suite, "edition": self.edition, "scenario": self.name, "pass": ok, "checks": self.checks, "info": self.info})
        print(("PASS " if ok else "FAIL ") + f"[{self.suite}/{self.edition}] {self.name}")
        for c in self.checks:
            if not c["ok"]:
                print("   - " + c["check"] + ": " + c["detail"])


def analyze_and_compare(env, sc, run_dir):
    """C# analyze 실행 후 PowerShell G1과 값 비교."""
    rc, out = run(["dotnet", env.poc, "analyze", run_dir, "--root", ROOT], env.env(), 120)
    sc.check("C# analyze 종료 코드 0", rc == 0, out[-600:])
    rep_path = os.path.join(run_dir, "gate_report.json")
    if not os.path.isfile(rep_path):
        sc.check("gate_report.json 생성", False)
        return None
    rep = load(rep_path)
    g1 = load(os.path.join(run_dir, "g1_report.json"))
    sc.check("PowerShell G1 == C# G1", g1 == rep["G1"], json.dumps({"ps": g1, "cs": rep["G1"]}, ensure_ascii=False)[:400])
    sc.check("합성 데이터는 PASS/FAIL 없음", all(rep[g]["verdict"] not in ("PASS", "FAIL") for g in ("G1", "G2", "G3A")), {g: rep[g]["verdict"] for g in ("G1", "G2", "G3A")})
    return rep


def collect(env, edition, out_root, *args, timeout=240):
    os.makedirs(out_root, exist_ok=True)
    rc, out = run(ps_cmd(edition) + ["-OutRoot", out_root, *args], env.env(), timeout)
    return rc, out


# ---------------- 시나리오 ----------------

def s_env(env, suite, editions):
    sc = Scenario(suite, "-", "환경 정보")
    for ed in editions:
        exe = "powershell.exe" if ed == "ps51" else "pwsh"
        rc, out = run([exe, "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString() + ' ' + $PSVersionTable.PSEdition + ' ' + [Globalization.CultureInfo]::CurrentCulture.Name + ' ' + [Console]::OutputEncoding.WebName"], env.env(), 60)
        sc.info[ed] = out.strip()
        sc.check(f"{exe} 실행", rc == 0, out)
    rc, out = run(["dotnet", "--version"], env.env(), 60)
    sc.info["dotnet"] = out.strip()
    sc.info["python"] = sys.version.split()[0]
    sc.info["os"] = f"{os.name} {sys.platform}"
    sc.done()


def s_policy(env, suite, ed):
    sc = Scenario(suite, ed, "정책: 포트 8080 거부")
    root = os.path.join(env.out, suite, ed, "policy")
    rc, out = collect(env, ed, root, "-Mode", "Collect", "-BaseUrl", "http://127.0.0.1:8080")
    sc.check("종료 코드 1", rc == 1, out[-400:])
    sc.check("결과 폴더 없음", not os.listdir(root), os.listdir(root))
    sc.done()


def s_non_hwinfo_owner(env, suite, ed):
    sc = Scenario(suite, ed, "포트 소유자가 HWiNFO가 아니면 중단(Get-NetTCPConnection)")
    if not IS_WIN:
        sc.check("Windows 전용", True, "skip")
        return sc.done()
    p = env.mock(env.base_py, HW_PORT, tag="owner_python")
    root = os.path.join(env.out, suite, ed, "owner")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Collect", "-Samples", "3", "-IntervalMs", "500")
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 1", rc == 1, out[-400:])
    sc.check("수집 폴더 없음", not glob.glob(os.path.join(root, "*", "samples_all.jsonl")))
    sc.done()


def s_inventory(env, suite, ed):
    sc = Scenario(suite, ed, "Inventory (버전·포트 소유자·REST·센서 저장)")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, tag="inv")
    root = os.path.join(env.out, suite, ed, "inventory")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Inventory")
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 0", rc == 0, out[-600:])
    d = newest(root, "inventory_*")
    if not sc.check("inventory 폴더", d):
        return sc.done()
    inv = load(os.path.join(d, "inventory.json"))
    sc.check("REST ping 성공", inv["rest"]["pingOk"] is True, inv["rest"])
    sc.check("sdb.json·read_once.json 저장", os.path.isfile(os.path.join(d, "sdb.json")) and os.path.isfile(os.path.join(d, "read_once.json")))
    sc.check("SDB 단위 '°C' 보존(UTF-8 해석)", '"Unit": "°C"' in read_text(os.path.join(d, "sdb.json")))
    sc.check("한글 문자열 보존", inv["notes"][0].startswith("읽기 전용"), inv["notes"][0])
    if IS_WIN:
        port = inv["ports"].get(str(HW_PORT)) or []
        sc.check("8764 소유 프로세스 = HWiNFO64", bool(port) and port[0]["name"] == "HWiNFO64", port)
        sc.check("HWiNFO 프로세스 감지", inv["hwinfo"]["process"] and inv["hwinfo"]["process"]["name"] == "HWiNFO64", inv["hwinfo"]["process"])
        sc.check("실행 파일 버전 읽기", inv["hwinfo"]["runningVersion"] is not None, inv["hwinfo"]["runningVersion"])
        sc.info["pnp"] = inv["pnp"]
        sc.info["installRegistry"] = inv["hwinfo"]["install"]
    sc.done()


def s_baseline(env, suite, ed):
    sc = Scenario(suite, ed, "Baseline (REST 꺼짐, HWiNFO64 CPU 시간)")
    hw = env.fake("HWiNFO64")
    root = os.path.join(env.out, suite, ed, "baseline")
    idle = env.idle(hw)
    try:
        rc, out = collect(env, ed, root, "-Mode", "Baseline", "-BaselineSeconds", "5")
        # REST가 켜져 있으면 거부
        p = env.mock(hw, HW_PORT, tag="baseline_on")
        try:
            rc_on, out_on = collect(env, ed, os.path.join(root, "_on"), "-Mode", "Baseline", "-BaselineSeconds", "3")
        finally:
            env.stop(p, HW_PORT)
    finally:
        env.stop(idle)
    sc.check("종료 코드 0", rc == 0, out[-600:])
    sc.check("REST 켜짐이면 종료 코드 1", rc_on == 1, out_on[-300:])
    d = newest(root, "baseline_*")
    path = os.path.join(d, "baseline.json") if d else None
    if sc.check("baseline.json", path and os.path.isfile(path)):
        b = load(path)
        sc.check("표본 5개·평균 CPU 숫자", len(b["samples"]) == 5 and isinstance(b["meanCpuPct"], (int, float)), b.get("meanCpuPct"))
        sc.info["meanCpuPct"] = b["meanCpuPct"]
    sc.done()
    return path if path and os.path.isfile(path) else None


def s_collect_normal(env, suite, ed, baseline, samples=12):
    sc = Scenario(suite, ed, f"Collect 정상 ({samples}샘플)")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, tag="normal")
    root = os.path.join(env.out, suite, ed, "normal")
    args = ["-Mode", "Collect", "-Samples", str(samples), "-IntervalMs", "500"]
    if baseline:
        args += ["-BaselineFile", baseline]
    try:
        rc, out = collect(env, ed, root, *args)
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 4(UNVERIFIED)", rc == 4, out[-600:])
    d = newest(root, "run_G1_*")
    if not sc.check("수집 폴더", d):
        return sc.done()
    man = load(os.path.join(d, "run_manifest.json"))
    lines = jsonl(os.path.join(d, "samples_all.jsonl"))
    sc.check("출처 SYNTHETIC(소유자가 HWiNFO64여도)", man["source"] == "SYNTHETIC", man["source"])
    sc.check("한글 sourceReason 보존", "모의 서버" in man["sourceReason"], man["sourceReason"])
    sc.check(f"샘플 {samples}개 모두 성공", len(lines) == samples and all(x["ok"] for x in lines), [x["ok"] for x in lines])
    sc.check("SDB '°C' 보존", '"Unit": "°C"' in read_text(os.path.join(d, "sdb.json")))
    if IS_WIN:
        sc.check("endpointOwner = HWiNFO64", (man["endpointOwner"] or {}).get("name") == "HWiNFO64", man["endpointOwner"])
        cpu = [x["cpuPct"] for x in lines if x["cpuPct"] is not None]
        sc.check("샘플별 HWiNFO CPU 측정", len(cpu) >= samples - 1, cpu)
    rep = analyze_and_compare(env, sc, d)
    if rep and IS_WIN and baseline:
        ov = {c["name"]: c for c in rep["G1"]["checks"]}["hwinfoCpuOverheadPct"]
        sc.check("CPU 오버헤드 계산됨(기준선 대비)", ov["value"] is not None, ov)
    sc.info["runDir"] = os.path.relpath(d, env.out)
    sc.done()
    return d


def s_collect_g3a(env, suite, ed):
    sc = Scenario(suite, ed, "Collect G3A 형식(5샘플, 운영자 확인) → analyze의 증거 읽기")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, tag="g3a")
    root = os.path.join(env.out, suite, ed, "g3a")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Collect", "-Gate", "G3A", "-OperatorAttestsHWiNFOExclusive", "-Samples", "5", "-IntervalMs", "500")
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 4", rc == 4, out[-600:])
    d = newest(root, "run_G3A_*")
    if sc.check("수집 폴더", d):
        rep = analyze_and_compare(env, sc, d)
        if rep:
            sc.check("G3A 판정 UNVERIFIED(합성·600 미만)", rep["G3A"]["verdict"] == "UNVERIFIED", rep["G3A"]["verdict"])
            sc.check("소유권 운영자 확인 기록", rep["G3A"]["ownership"]["state"] == "OPERATOR_ATTESTED", rep["G3A"]["ownership"]["state"])
    sc.done()


def s_errors(env, suite, ed):
    sc = Scenario(suite, ed, "오류 응답(503 일반 텍스트, 잘린 JSON)")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, "--fail-every", "3", "--bad-json-every", "5", tag="errors")
    root = os.path.join(env.out, suite, ed, "errors")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Collect", "-Samples", "12", "-IntervalMs", "500")
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 4", rc == 4, out[-600:])
    d = newest(root, "run_G1_*")
    if sc.check("수집 폴더", d):
        lines = jsonl(os.path.join(d, "samples_all.jsonl"))
        sc.check("샘플 12개 기록", len(lines) == 12, len(lines))
        s503 = [x for x in lines if x["status"] == 503 and not x["ok"]]
        bad = [x for x in lines if x["ok"] is False and (x["error"] or "").startswith("JSON 해석 실패")]
        sc.check("503 실패 4건 기록", len(s503) == 4, [(x["i"], x["status"], x["error"]) for x in lines if not x["ok"]])
        sc.check("잘린 JSON 실패 2건 기록", len(bad) == 2, [(x["i"], x["error"]) for x in lines if not x["ok"]])
        rep = analyze_and_compare(env, sc, d)
        if rep:
            g1 = {c["name"]: c for c in rep["G1"]["checks"]}
            sc.check("성공률 50%", g1["successRatePct"]["value"] == 50, g1["successRatePct"])
    sc.done()


def s_sdb_version(env, suite, ed):
    sc = Scenario(suite, ed, "SDB Version 2 거부")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, "--sdb-version", "2", tag="sdbv2")
    root = os.path.join(env.out, suite, ed, "sdbv2")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Collect", "-Samples", "3", "-IntervalMs", "500")
    finally:
        env.stop(p, HW_PORT)
    sc.check("종료 코드 2", rc == 2, out[-400:])
    sc.check("샘플 없음", not glob.glob(os.path.join(root, "*", "samples_all.jsonl")))
    sc.done()


def s_disconnect(env, suite, ed):
    sc = Scenario(suite, ed, "연결 해제 → 재연결 (수집 중 서버 종료 후 재시작)")
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, HW_PORT, tag="disc1")
    root = os.path.join(env.out, suite, ed, "disconnect")
    os.makedirs(root, exist_ok=True)
    proc = subprocess.Popen(ps_cmd(ed) + ["-OutRoot", root, "-Mode", "Collect", "-Samples", "24", "-IntervalMs", "500"], env=env.env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    timeline = {}
    try:
        t0 = time.time()
        # 첫 10샘플이 기록(10샘플마다 flush)된 뒤 끊는다
        while time.time() - t0 < 60:
            f = newest(root, "run_G1_*")
            if f and complete_lines(os.path.join(f, "samples_all.jsonl")) >= 10:
                break
            time.sleep(0.2)
        timeline["killAt_s"] = round(time.time() - t0, 1)
        env.stop(p, HW_PORT)
        time.sleep(4.0)
        p2 = env.mock(hw, HW_PORT, tag="disc2")
        timeline["restartAt_s"] = round(time.time() - t0, 1)
        out = proc.communicate(timeout=180)[0].decode("utf-8", errors="replace")
        rc = proc.returncode
        env.stop(p2, HW_PORT)
    finally:
        if proc.poll() is None:
            proc.kill()
    sc.info["timeline"] = timeline
    sc.check("종료 코드 4", rc == 4, out[-600:])
    d = newest(root, "run_G1_*")
    if sc.check("수집 폴더", d):
        lines = jsonl(os.path.join(d, "samples_all.jsonl"))
        oks = [x["ok"] for x in lines]
        sc.info["okPattern"] = "".join("o" if o else "x" for o in oks)
        sc.check("샘플 24개 기록(중단 없음)", len(lines) == 24, len(lines))
        sc.check("처음 10개 성공", all(oks[:10]), sc.info["okPattern"])
        sc.check("끊긴 동안 실패 기록(1개 이상, 오류 문구 포함)", any((not x["ok"]) and x["error"] for x in lines[10:]), sc.info["okPattern"])
        sc.check("재연결 후 마지막 3개 성공", all(oks[-3:]), sc.info["okPattern"])
        analyze_and_compare(env, sc, d)
    sc.done()


def s_wireview_client(env, suite, ed):
    sc = Scenario(suite, ed, "다른 WireView 클라이언트 실행 중 → 수집 중단(종료 코드 5)")
    hw = env.fake("HWiNFO64")
    wv = env.fake("WireView2")
    fake = env.idle(wv)
    p = env.mock(hw, HW_PORT, tag="wv")
    root = os.path.join(env.out, suite, ed, "wireview")
    try:
        rc, out = collect(env, ed, root, "-Mode", "Collect", "-Samples", "3", "-IntervalMs", "500")
        alive = fake.poll() is None
    finally:
        env.stop(p, HW_PORT)
        env.stop(fake)
    sc.check("종료 코드 5", rc == 5, out[-400:])
    sc.check("다른 프로세스는 종료하지 않음", alive)
    sc.check("샘플 없음", not glob.glob(os.path.join(root, "*", "samples_all.jsonl")))
    sc.done()


def s_analyze_fixture(env, suite, ed):
    sc = Scenario(suite, ed, "Analyze 모드(기존 합성 수집 폴더)")
    d = os.path.join(env.out, suite, ed, "analyze_fixture")
    shutil.copytree(os.path.join(FX, "run_synthetic"), d, dirs_exist_ok=True)
    rc, out = run(ps_cmd(ed) + ["-Mode", "Analyze", "-RunDir", d], env.env(), 120)
    sc.check("종료 코드 4", rc == 4, out[-400:])
    analyze_and_compare(env, sc, d)
    sc.done()


def p_selftest(env, suite):
    sc = Scenario(suite, "dotnet", "PoC selftest (Windows)")
    out_dir = os.path.join(env.out, suite, "dotnet", "selftest")
    rc, out = run(["dotnet", env.poc, "selftest", "--root", ROOT, "--out", out_dir], env.env(), 120)
    sc.check("종료 코드 0", rc == 0, out[-800:])
    for f in glob.glob(os.path.join(FX, "expected", "*.json")):
        got = os.path.join(out_dir, os.path.basename(f))
        sc.check("JS 기대값과 같음: " + os.path.basename(f), os.path.isfile(got) and load(got) == load(f))
    sc.done()


def p_live(env, suite, disconnect):
    name = "PoC live 연결 해제 → 재연결" if disconnect else "PoC live 정상"
    sc = Scenario(suite, "dotnet", name)
    port = 18790 if disconnect else 18789
    p = env.mock(env.base_py, port, tag="poc")
    out_dir = os.path.join(env.out, suite, "dotnet", "live_disconnect" if disconnect else "live_normal")
    n = 16 if disconnect else 8
    proc = subprocess.Popen(["dotnet", env.poc, "live", f"http://127.0.0.1:{port}", str(n), out_dir, "--interval-ms", "500", "--root", ROOT], env=env.env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        if disconnect:
            time.sleep(3.5)
            env.stop(p, port)
            time.sleep(3.5)
            p = env.mock(env.base_py, port, tag="poc2")
        out = proc.communicate(timeout=180)[0].decode("utf-8", errors="replace")
        rc = proc.returncode
    finally:
        if proc.poll() is None:
            proc.kill()
        env.stop(p, port)
    lines = jsonl(os.path.join(out_dir, "samples_all.jsonl")) if os.path.isfile(os.path.join(out_dir, "samples_all.jsonl")) else []
    oks = [x["ok"] for x in lines]
    sc.info["okPattern"] = "".join("o" if o else "x" for o in oks)
    sc.check(f"샘플 {n}개 기록", len(lines) == n, len(lines))
    if disconnect:
        sc.check("종료 코드 3(수집 중 실패 있음)", rc == 3, out[-800:])
        sc.check("끊긴 동안 실패 기록", any(not o for o in oks), sc.info["okPattern"])
        sc.check("재연결 후 마지막 3개 성공", len(oks) >= 3 and all(oks[-3:]), sc.info["okPattern"])
    else:
        sc.check("종료 코드 0", rc == 0, out[-800:])
        sc.check("모두 성공", oks and all(oks), sc.info["okPattern"])
    rp = os.path.join(out_dir, "gate_report.json")
    if sc.check("gate_report.json 생성", os.path.isfile(rp)):
        rep = load(rp)
        man = load(os.path.join(out_dir, "run_manifest.json"))
        sc.check("출처 SYNTHETIC(모의 서버 표시 감지)", man["source"] == "SYNTHETIC", man["source"])
        sc.check("PASS/FAIL 없음", rep["G1"]["verdict"] == "UNVERIFIED", rep["G1"]["verdict"])
    sc.done()


def safe(fn, *args, label=None):
    """시나리오 하나가 예외로 죽어도 나머지는 계속한다(예외는 실패로 기록)."""
    try:
        return fn(*args)
    except Exception as e:  # noqa: BLE001 - 보고용
        sc = Scenario(args[1] if len(args) > 1 else "?", args[2] if len(args) > 2 and isinstance(args[2], str) else "-", label or fn.__name__)
        sc.check("예외 없이 실행", False, f"{type(e).__name__}: {e}")
        sc.done()
        return None


def write_report(env, suite):
    path = os.path.join(env.out, f"windows_ci_{suite}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(RESULTS, f, ensure_ascii=False, indent=1)
    md = [f"# RC3 Windows 호환성 검증 — {suite} (SYNTHETIC, 모의 HWiNFO 서버)", "", "| 묶음 | 실행기 | 시나리오 | 결과 |", "|---|---|---|---|"]
    for r in RESULTS:
        md.append(f"| {r['suite']} | {r['edition']} | {r['scenario']} | {'PASS' if r['pass'] else 'FAIL'} |")
    md += ["", "## 실패 세부", ""]
    for r in RESULTS:
        for c in r["checks"]:
            if not c["ok"]:
                md.append(f"- [{r['edition']}] {r['scenario']} — {c['check']}: {c['detail']}")
    md += ["", "## 정보", ""]
    for r in RESULTS:
        if r["info"]:
            md.append(f"- [{r['edition']}] {r['scenario']}: `{json.dumps(r['info'], ensure_ascii=False)[:600]}`")
    with open(os.path.join(env.out, f"windows_ci_{suite}.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
            f.write("\n".join(md) + "\n")


def main():
    # python -I 는 PYTHONUTF8 환경 변수를 무시하므로 콘솔(cp1252)에서 한글 출력이 실패하지 않게 직접 맞춘다
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--suite", choices=["main", "culture"], default="main")
    ap.add_argument("--out", required=True)
    ap.add_argument("--poc", required=True, help="빌드된 HWiNfoRestPoC.dll")
    ap.add_argument("--editions", default="ps51,ps7" if IS_WIN else "ps7")
    a = ap.parse_args()
    env = Env(a.out, os.path.abspath(a.poc))
    eds = a.editions.split(",")
    try:
        if port_open(HW_PORT):
            raise SystemExit(f"포트 {HW_PORT}가 이미 사용 중 — 시험을 시작하지 않음")
        safe(s_env, env, a.suite, eds)
        if a.suite == "main":
            safe(p_selftest, env, a.suite)
            safe(p_live, env, a.suite, False, label="PoC live 정상")
            safe(p_live, env, a.suite, True, label="PoC live 연결 해제 → 재연결")
            for ed in eds:
                full = ed == "ps51"  # 5.1은 처음 실행이므로 전부, 7은 Windows 전용 경로와 새 시나리오만
                if full:
                    safe(s_policy, env, a.suite, ed)
                safe(s_non_hwinfo_owner, env, a.suite, ed)
                safe(s_inventory, env, a.suite, ed)
                baseline = safe(s_baseline, env, a.suite, ed)
                safe(s_collect_normal, env, a.suite, ed, baseline)
                safe(s_collect_g3a, env, a.suite, ed)
                if full:
                    safe(s_errors, env, a.suite, ed)
                    safe(s_sdb_version, env, a.suite, ed)
                    safe(s_analyze_fixture, env, a.suite, ed)
                safe(s_disconnect, env, a.suite, ed)
                safe(s_wireview_client, env, a.suite, ed)
        else:
            for ed in eds:
                safe(s_collect_normal, env, a.suite, ed, None, 8)
                if ed == "ps51":
                    safe(s_analyze_fixture, env, a.suite, ed)
    finally:
        env.cleanup()
        write_report(env, a.suite)
    failed = [r for r in RESULTS if not r["pass"]]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} 시나리오 통과")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
