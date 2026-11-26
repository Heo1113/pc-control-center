#!/usr/bin/env python3
"""RC3 합성 픽스처 생성기 (결정적). 모든 산출물은 SYNTHETIC — 실측 데이터가 아니다.

센서·리딩 이름은 HWiNFO에서 흔히 보이는 형태를 흉내 낸 것일 뿐이며, 사용자 PC의 실제 이름은
실기 인벤토리(sdb.json)로 확정해야 한다. WireView 핀 리딩 이름은 특히 확인되지 않았다.

  python -I tools/make_synthetic_fixtures.py          # tests/fixtures/ 에 생성(같은 내용이면 그대로)
  python -I tools/make_synthetic_fixtures.py --check  # 다시 만들면 달라지는지 확인만(달라지면 exit 1)
"""
import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "fixtures"
NOTICE = "SYNTHETIC TEST FIXTURE — RC3 합성 데이터(실측 아님). 이름은 예시이며 실기 SDB로 확정해야 함."
T0 = 1795464600000  # 임의의 기준 시각(epoch ms)

UNIT_TYPE = {"°C": 1, "V": 2, "RPM": 3, "A": 4, "W": 5, "MHz": 6, "%": 7, "MB": 8, "GB": 8}

# (센서, 리딩, 단위, 값 문자열)
FULL = [
    ("System: Synthetic Test", "Physical Memory Used", "MB", "36,864"),
    ("System: Synthetic Test", "Physical Memory Available", "MB", "94,208"),
    ("System: Synthetic Test", "Physical Memory Load", "%", "28.1"),
    ("CPU [#0]: AMD Ryzen 7 9800X3D", "CPU (Tctl/Tdie)", "°C", "64.8"),
    ("CPU [#0]: AMD Ryzen 7 9800X3D", "Total CPU Usage", "%", "41.2"),
    ("CPU [#0]: AMD Ryzen 7 9800X3D", "CPU Package Power", "W", "86.4"),
    ("CPU [#0]: AMD Ryzen 7 9800X3D", "Core Clocks (avg)", "MHz", "5,125.3"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Temperature", "°C", "66.5"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Hot Spot Temperature", "°C", "78.9"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Memory Junction Temperature", "°C", "70.0"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Power", "W", "332.5"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Core Load", "%", "97.0"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Memory Allocated", "MB", "12,450"),
    ("GPU [#0]: NVIDIA GeForce RTX 5080", "GPU Clock", "MHz", "2,805"),
    ("GPU [#1]: NVIDIA GeForce RTX 5060 Ti", "GPU Temperature", "°C", "44.0"),
    ("GPU [#1]: NVIDIA GeForce RTX 5060 Ti", "GPU Power", "W", "61.2"),
    ("GPU [#1]: NVIDIA GeForce RTX 5060 Ti", "GPU Core Load", "%", "12.0"),
    ("GPU [#1]: NVIDIA GeForce RTX 5060 Ti", "GPU Memory Allocated", "GB", "1.5"),
    ("DDR5 DIMM [#0] (SPD)", "SPD Hub Temperature", "°C", "46.5"),
    ("S.M.A.R.T.: Samsung SSD 990 PRO 2TB", "Drive Temperature", "°C", "48"),
    ("S.M.A.R.T.: Samsung SSD 990 PRO 2TB", "Drive Remaining Life", "%", "99"),
    ("Thermal Grizzly WireView Pro II", "Total Power", "W", "334.8"),
    ("Thermal Grizzly WireView Pro II", "Connector Temperature", "°C", "41.5"),
]
PIN_A = [4.62, 4.71, 4.55, 4.80, 4.49, 4.66]
PIN_V = [12.08, 12.07, 12.08, 12.06, 12.08, 12.07]
for n in range(6):
    FULL.append(("Thermal Grizzly WireView Pro II", f"Pin {n + 1} Current", "A", f"{PIN_A[n]:.2f}"))
    FULL.append(("Thermal Grizzly WireView Pro II", f"Pin {n + 1} Voltage", "V", f"{PIN_V[n]:.2f}"))

WV = "Thermal Grizzly WireView Pro II"
GPU0 = "GPU [#0]: NVIDIA GeForce RTX 5080"
GPU1 = "GPU [#1]: NVIDIA GeForce RTX 5060 Ti"


def ts_fields(ts):
    return {"Timestamp RS ms": ts - 110.0, "Timestamp ms": ts, "Timestamp RE ms": ts + 25.0}


def make_read(rows, ts):
    doc = {"_notice": NOTICE, "Version": 1, "Timestamp": "synthetic"}
    doc.update(ts_fields(ts))
    for sensor, reading, unit, value in rows:
        doc.setdefault(sensor, {})[reading] = {str(UNIT_TYPE[unit]): {"Value": value}}
    return doc


def make_sdb(rows):
    doc = {"_notice": NOTICE, "Version": 1}
    for sensor, reading, unit, _ in rows:
        t = UNIT_TYPE[unit]
        doc.setdefault(sensor, {})[reading] = {str(t): {
            "hide": 0, "monitoring": 1, "logging": 1, "Unit": unit, "Tooltip": reading,
            "tSensorLocation": {}, "tReadingLocation": {}, "tReadingType": {"eType": t, "eTypeUnion": 0}}}
    return doc


def with_values(rows, changes=None, drop_sensors=()):
    changes = changes or {}
    out = []
    for sensor, reading, unit, value in rows:
        if sensor in drop_sensors:
            continue
        out.append((sensor, reading, unit, changes.get((sensor, reading), value)))
    return out


def frame(i, ts, rows):
    return {"i": i, "receivedAtMs": T0 + 1000 * i + 150, "read": make_read(rows, ts)}


def normal_ts(i):
    return float(T0 + 2000 * (i // 2) + 100)  # HWiNFO가 2초마다 갱신, 1초마다 폴링 → 같은 측정이 두 번씩 온다


def build():
    files = {}
    files["synthetic_full_sdb_v1.json"] = make_sdb(FULL)
    files["synthetic_full_read_v1.json"] = make_read(FULL, float(T0 + 100))

    # 단위 불일치: GPU #1의 'GPU Power'가 % 단위(TDP 대비)로만 있음 → 채택 거부 → 합계 PARTIAL.
    # 또 read에만 있고 SDB에 없는 리딩 하나 → 매핑률 하락.
    # 또 DIMM 온도가 소수점 쉼표("46,5")로 온 경우: 쉼표 제거 규칙대로면 465가 되므로 의심 값으로 잡혀야 한다.
    mism = [(s, r, ("%" if (s, r) == (GPU1, "GPU Power") else u), ("38.5" if (s, r) == (GPU1, "GPU Power") else v)) for s, r, u, v in FULL]
    mism = [(s, r, u, ("46,5" if r == "SPD Hub Temperature" else v)) for s, r, u, v in mism]
    files["synthetic_mismatch_sdb_v1.json"] = make_sdb(mism)
    files["synthetic_mismatch_read_v1.json"] = make_read(mism + [(GPU0, "GPU Fan", "RPM", "1,450")], float(T0 + 100))

    over = with_values(FULL, {(WV, "Pin 3 Current"): "7.40", (GPU0, "GPU Temperature"): "73.0"})
    back = with_values(FULL, {(GPU0, "GPU Temperature"): "73.0"})
    seq = []
    for i in range(6):
        seq.append(frame(i, normal_ts(i), over))
    seq.append(frame(6, normal_ts(6), with_values(over, drop_sensors=(WV,))))  # WireView 해제(센서 사라짐)
    seq.append(frame(7, normal_ts(7), over))  # 같은 타임스탬프의 두 번째 응답
    seq.append(frame(8, normal_ts(8), over))
    seq.append(frame(9, normal_ts(9), over))  # i=8과 같은 타임스탬프(중복 응답)
    seq.append(frame(10, normal_ts(10), back))  # 새 측정에서 정상 → 즉시 해제
    files["seq_latch_release.json"] = {
        "_notice": NOTICE, "source": "SYNTHETIC", "sdb": "synthetic_full_sdb_v1.json",
        "clock": {"pingT": float(T0), "t0": float(T0 - 15), "t3": float(T0 + 15)},
        "expect": "핀3 7.40 A(기준 7.0) — 서로 다른 HWiNFO 타임스탬프 3회째(i=4)에 래치. i=6 WireView 사라짐 → 핀 경고만 해제, GPU#0 73 °C 경고 유지. i=10 정상 → 해제",
        "frames": seq}

    frozen = []
    for i in range(10):
        ts = normal_ts(i) if i <= 2 else normal_ts(2)
        frozen.append(frame(i, ts, FULL))
    files["seq_frozen.json"] = {
        "_notice": NOTICE, "source": "SYNTHETIC", "sdb": "synthetic_full_sdb_v1.json", "clock": None,
        "expect": "i=2 이후 타임스탬프 정지. 응답은 계속 오지만 정지 7000 ms(i=9)에서 STALE(기준 6000 ms 초과)",
        "frames": frozen}

    inval = []
    for i in range(6):
        ts = normal_ts(i)
        f = frame(i, ts, FULL)
        if i == 2:
            f["read"]["Timestamp RS ms"] = ts + 50.0  # RS > TS
        if i == 4:
            f["read"].update(ts_fields(float(T0 + 100)))  # 역행
        inval.append(f)
    files["seq_invalid.json"] = {
        "_notice": NOTICE, "source": "SYNTHETIC", "sdb": "synthetic_full_sdb_v1.json", "clock": None,
        "expect": "i=2 RS>TS, i=4 타임스탬프 역행 → INVALID(폐기). 나머지는 정상", "frames": inval}

    lag = []
    for i in range(4):
        f = frame(i, normal_ts(i) - 8000.0, FULL)
        f["receivedAtMs"] -= 3000  # 클라이언트 시계가 서버보다 3초 느림
        lag.append(f)
    files["seq_age.json"] = {
        "_notice": NOTICE, "source": "SYNTHETIC", "sdb": "synthetic_full_sdb_v1.json",
        "clock": {"pingT": float(T0), "t0": float(T0 - 3020), "t3": float(T0 - 2980)},
        "expect": "시계 오프셋 +3000 ms 보정 후 측정 나이 8050~9050 ms > 6000 → 매 프레임 STALE (타임스탬프는 계속 증가)",
        "frames": lag}

    # 합성 수집 폴더: 수집기 출력 형식과 같은 파일들. 출처 SYNTHETIC → 모든 게이트 UNVERIFIED여야 한다.
    # G1 계산 정의(C# analyze ↔ PowerShell Analyze) 비교용. 12샘플 중 i=7 실패, RTT 이상치 1개.
    rtts = [12.5, 14.1, 11.8, 250.0, 13.3, 12.9, None, 15.2, 12.2, 13.7, 12.0, 14.4]
    cpus = [1.4, 1.6, 1.5, 1.7, 1.5, 1.4, 1.6, 1.5, 1.8, 1.4, 1.5, 1.6]
    lines = []
    for i in range(1, 13):
        t0 = float(T0 + 1000 * (i - 1) + 20)
        ts = float(T0 + 2000 * ((i - 1) // 2) - 500)
        if rtts[i - 1] is None:
            lines.append({"i": i, "t0Ms": t0, "t3Ms": t0 + 5000.0, "rttMs": 5000.0, "ok": False, "status": None,
                          "error": "TaskCanceledException: timeout (synthetic)", "cpuPct": cpus[i - 1], "read": None})
            continue
        lines.append({"i": i, "t0Ms": t0, "t3Ms": t0 + rtts[i - 1], "rttMs": rtts[i - 1], "ok": True, "status": 200,
                      "error": None, "cpuPct": cpus[i - 1], "read": make_read(FULL, ts)})
    pings = []
    for k in range(5):
        t0 = float(T0 - 1000 + 120 * k)
        rtt = [3.0, 2.0, 4.0, 2.5, 3.5][k]
        pings.append({"t0Ms": t0, "t3Ms": t0 + rtt, "rttMs": rtt, "ok": True, "t": t0 + rtt / 2})
    files["run_synthetic/run_manifest.json"] = {
        "_notice": NOTICE, "schema": "iron.rc3.run/1", "source": "SYNTHETIC",
        "sourceReason": "tools/make_synthetic_fixtures.py 가 만든 합성 수집 폴더", "collector": "synthetic", "gate": "G3A",
        "url": "http://127.0.0.1:8764", "startedAtUtc": "synthetic", "samplesRequested": 12, "intervalMs": 1000,
        "endpointOwner": None, "operatorAttestation": {"hwinfoIsOnlyWireViewClient": True, "note": "synthetic"},
        "evidence": {"wireViewClientProcesses": [], "pnp": [], "ports": []}, "baselineFile": "baseline.json"}
    files["run_synthetic/baseline.json"] = {"_notice": NOTICE, "mode": "Baseline", "restListening": False,
                                            "samples": [{"cpuPct": 1.1}, {"cpuPct": 1.0}, {"cpuPct": 1.2}], "meanCpuPct": 1.1}
    files["run_synthetic/sdb.json"] = make_sdb(FULL)
    files["run_synthetic/pings.jsonl"] = "".join(json.dumps(x) + "\n" for x in pings)
    files["run_synthetic/samples_all.jsonl"] = "".join(json.dumps(x, ensure_ascii=False) + "\n" for x in lines)
    return files


def dump(obj):
    return json.dumps(obj, ensure_ascii=False, indent=1) + "\n"


def main():
    check = "--check" in sys.argv
    changed = []
    for name, obj in build().items():
        p = OUT / name
        p.parent.mkdir(parents=True, exist_ok=True)
        text = obj if isinstance(obj, str) else dump(obj)
        old = p.read_text(encoding="utf-8") if p.exists() else None
        if old != text:
            changed.append(name)
            if not check:
                p.write_text(text, encoding="utf-8")
    if check:
        print("변경 없음" if not changed else "다시 만들면 달라짐: " + ", ".join(changed))
        return 1 if changed else 0
    print("생성/갱신: " + (", ".join(changed) if changed else "없음(이미 최신)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
