#!/usr/bin/env python3
"""
HWiNFO REST 연결이 끊긴 동안 요청이 어떻게 쌓이는지 시간표로 재는 도구 (Windows 러너, 모의 서버 = SYNTHETIC).

같은 시나리오(수집 시작 2.5초 뒤 서버 종료, 4초 뒤 재시작, 500 ms 간격 24샘플)를 수정 전(RC3) / 수정 후(RC3.2)
클라이언트에 똑같이 걸고, 샘플마다 [예정 시각, 실제 요청 시작, 걸린 시간, 결과]를 출력한다. 요약:
  longestFailMs   끊긴 동안 요청 하나가 실패까지 걸린 최대 시간
  burstMax        한 간격(500 ms) 안에 시작된 요청 수의 최댓값 (따라잡기 몰림 = 2 이상)
  skipped         요청하지 않고 빈칸으로 기록한 샘플 수 (RC3.2)
또 닫힌 포트에 GET 하나가 실패하기까지 걸리는 시간을 PowerShell 5.1·7, .NET(RC3·RC3.2 설정), Python으로 잰다.

  python -I tests/disconnect_timeline.py --out DIR --label rc3   --poc OLD.dll --collector OLD.ps1
  python -I tests/disconnect_timeline.py --out DIR --label rc3.2 --poc NEW.dll --collector NEW.ps1 --probe
"""
import argparse
import importlib.util
import json
import os
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("wcc", os.path.join(HERE, "windows_ci_check.py"))
wcc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(wcc)

INTERVAL = 500
SAMPLES = 24


def timeline(lines, t_kill, t_restart):
    """lines: samples_all.jsonl rows. Times relative to the first request start (ms)."""
    starts = [x["t0Ms"] for x in lines if x.get("t0Ms")]
    if not starts:
        return {"rows": [], "summary": {"error": "no request made"}}
    base = min(starts)
    rows = []
    for x in lines:
        planned = (x["i"] - 1) * INTERVAL
        if x.get("t0Ms"):
            start = x["t0Ms"] - base
            dur = x.get("rttMs") if x.get("rttMs") is not None else ((x.get("t3Ms") or x["t0Ms"]) - x["t0Ms"])
        else:
            start, dur = None, None
        err = str(x.get("error") or "")
        kind = "ok" if x["ok"] else ("skip" if err.startswith("SKIPPED_LATE") else "fail")
        rows.append({"i": x["i"], "plannedMs": planned, "startMs": None if start is None else round(start), "durMs": None if dur is None else round(dur),
                     "result": kind, "error": err.split(":")[0][:40] if kind == "fail" else ""})
    made = sorted(r["startMs"] for r in rows if r["startMs"] is not None)
    # requests started within 0.8 interval of each other: a catch-up burst (normal spacing is one interval)
    burst = max((sum(1 for t in made if s <= t < s + INTERVAL * 0.8) for s in made), default=0)
    fails = [r["durMs"] for r in rows if r["result"] == "fail" and r["durMs"] is not None]
    summary = {
        "requests": len(made),
        "ok": sum(1 for r in rows if r["result"] == "ok"),
        "failed": sum(1 for r in rows if r["result"] == "fail"),
        "skipped": sum(1 for r in rows if r["result"] == "skip"),
        "longestFailMs": max(fails, default=0),
        "burstMax": burst,
        "lastStartMs": made[-1] if made else None,
        "scheduleEndMs": (SAMPLES - 1) * INTERVAL,
        "serverDownMs": [t_kill, t_restart],
    }
    return {"rows": rows, "summary": summary}


def run_client(env, kind, client, label, port):
    """kind: poc | ps51 | ps7. Returns timeline dict."""
    out = os.path.join(env.out, label, kind)
    os.makedirs(out, exist_ok=True)
    hw = env.fake("HWiNFO64")
    p = env.mock(hw, port, tag=f"{label}-{kind}")
    if kind == "poc":
        cmd = ["dotnet", client, "live", f"http://127.0.0.1:{port}", str(SAMPLES), os.path.join(out, "run"), "--interval-ms", str(INTERVAL), "--root", wcc.ROOT]
        run_dir = lambda: os.path.join(out, "run")  # noqa: E731
    else:
        exe = "powershell.exe" if kind == "ps51" else "pwsh"
        cmd = [exe, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", client, "-OutRoot", out, "-Mode", "Collect", "-Samples", str(SAMPLES),
               "-IntervalMs", str(INTERVAL), "-BaseUrl", f"http://127.0.0.1:{port}"]
        run_dir = lambda: wcc.newest(out, "run_G1_*")  # noqa: E731
    log = open(os.path.join(env.out, f"timeline_{label}_{kind}.log"), "wb")
    proc = subprocess.Popen(cmd, env=env.env(), stdout=log, stderr=subprocess.STDOUT)
    t_kill = t_restart = None
    p2 = None
    try:
        t0 = time.time()
        while time.time() - t0 < 90:
            d = run_dir()
            if d and os.path.isfile(os.path.join(d, "samples_all.jsonl")):
                break
            time.sleep(0.05)
        loop = time.time()
        time.sleep(2.5)
        env.stop(p, port)
        t_kill = round((time.time() - loop) * 1000)
        time.sleep(4.0)
        p2 = env.mock(hw, port, tag=f"{label}-{kind}-2")
        t_restart = round((time.time() - loop) * 1000)
        proc.wait(180)
    finally:
        if proc.poll() is None:
            proc.kill()
        log.close()
        if p2 is not None:
            env.stop(p2, port)
    d = run_dir()
    lines = wcc.jsonl(os.path.join(d, "samples_all.jsonl")) if d and os.path.isfile(os.path.join(d, "samples_all.jsonl")) else []
    t = timeline(lines, t_kill, t_restart)
    t["exitCode"] = proc.returncode
    return t


def probe(env, poc, port):
    """How long one GET to a closed loopback port takes to fail."""
    url = f"http://127.0.0.1:{port}/api/ping"
    res = {}
    for ed, exe in (("ps51", "powershell.exe"), ("ps7", "pwsh")):
        for tmo in (5, 1):
            script = ("$t=[Diagnostics.Stopwatch]::StartNew(); try { Invoke-WebRequest -Uri '%s' -TimeoutSec %d -UseBasicParsing | Out-Null } catch { $e=$_.Exception.GetType().Name }; "
                      "'{0:0}|{1}' -f $t.Elapsed.TotalMilliseconds, $e") % (url, tmo)
            times = []
            last = ""
            for _ in range(3):
                rc, out = wcc.run([exe, "-NoProfile", "-Command", script], env.env(), 60)
                line = out.strip().splitlines()[-1] if out.strip() else ""
                ms, _, last = line.partition("|")
                try:
                    times.append(float(ms))
                except ValueError:
                    times.append(None)
            res[f"{ed} Invoke-WebRequest -TimeoutSec {tmo}"] = {"ms": times, "error": last}
    rc, out = wcc.run(["dotnet", poc, "probe-refused", url], env.env(), 120)
    res["dotnet"] = [l for l in out.splitlines() if l.startswith("probe-refused")]
    times = []
    for _ in range(3):
        s = time.perf_counter()
        try:
            socket.create_connection(("127.0.0.1", port), 10).close()
        except OSError:
            pass
        times.append(round((time.perf_counter() - s) * 1000))
    res["python socket.create_connection"] = times
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--poc", required=True)
    ap.add_argument("--collector", required=True)
    ap.add_argument("--probe", action="store_true")
    a = ap.parse_args()
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8")
        except AttributeError:
            pass
    env = wcc.Env(a.out, a.poc)
    report = {"label": a.label, "intervalMs": INTERVAL, "samples": SAMPLES}
    try:
        if a.probe:
            report["refusedConnect"] = probe(env, os.path.abspath(a.poc), 18797)
            print("refused connect (ms): " + json.dumps(report["refusedConnect"], ensure_ascii=False))
        for i, kind in enumerate(("poc", "ps51", "ps7")):
            client = os.path.abspath(a.poc if kind == "poc" else a.collector)
            t = report[kind] = run_client(env, kind, client, a.label, 18800 + i)
            print(f"== {a.label} {kind}: " + json.dumps(t["summary"], ensure_ascii=False) + f" exit {t['exitCode']}")
            print("   i  plan  start  dur  result")
            for r in t["rows"]:
                print(f"   {r['i']:>2} {r['plannedMs']:>5} {'' if r['startMs'] is None else r['startMs']:>6} {'' if r['durMs'] is None else r['durMs']:>4}  {r['result']} {r['error']}")
    finally:
        env.cleanup()
    with open(os.path.join(a.out, f"timeline_{a.label}.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
