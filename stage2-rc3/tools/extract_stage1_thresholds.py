#!/usr/bin/env python3
"""
Stage 1 UI 주의 기준을 Stage 1 소스에서 직접 추출합니다 (복사·추정 금지).

  python tools/extract_stage1_thresholds.py --stage1-src <pc-control-center 저장소 경로> [--write]

- 대상: feat/iron-ui-stage1 의 src/Iron.App/Presentation/{ShellViewModel,Alerts,DeviceViewModels}.cs
- --write 없이 실행하면 config/iron_stage1_thresholds.json 과 비교만 하고, 다르면 종료 코드 1.
- Stage 1은 값을 파일에 저장하지 않습니다(메모리 내 기본값·허용 범위). 따라서 "실제 설정" = 이 소스의 기본값과 범위입니다.
"""
import argparse
import hashlib
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CONFIG = os.path.join(ROOT, "config", "iron_stage1_thresholds.json")
FILES = {
    "shell": "src/Iron.App/Presentation/ShellViewModel.cs",
    "alerts": "src/Iron.App/Presentation/Alerts.cs",
    "devices": "src/Iron.App/Presentation/DeviceViewModels.cs",
}
NUM = r"(-?\d+(?:\.\d+)?)"


def numeric_setting(text, label, unit):
    m = re.search(r'new NumericSetting\("' + re.escape(label) + r'",\s*"' + re.escape(unit) + r'",\s*' + NUM + r",\s*" + NUM + r",\s*" + NUM + r",\s*(\d+)", text)
    if not m:
        raise SystemExit(f"Stage 1 소스에서 '{label}' 정의를 찾지 못했습니다.")
    lo, hi, default, decimals = float(m.group(1)), float(m.group(2)), float(m.group(3)), int(m.group(4))
    return {"default": default, "min": lo, "max": hi, "decimals": decimals}


def const(text, name):
    m = re.search(r"const\s+(?:int|double)\s+" + re.escape(name) + r"\s*=\s*" + NUM + ";", text)
    if not m:
        raise SystemExit(f"Stage 1 소스에서 상수 '{name}'을(를) 찾지 못했습니다.")
    return float(m.group(1))


def extract(src_root):
    texts = {}
    digests = {}
    for key, rel in FILES.items():
        with open(os.path.join(src_root, rel), "rb") as f:
            raw = f.read()
        texts[key] = raw.decode("utf-8")
        digests[rel] = hashlib.sha256(raw).hexdigest()
    return {
        "gpuTemperatureC": numeric_setting(texts["shell"], "GPU 온도 UI 주의 기준", "°C"),
        "wireViewPinCurrentA": numeric_setting(texts["shell"], "WireView 핀 전류 UI 주의 기준", "A"),
        "pinApproachingShare": const(texts["alerts"], "ApproachingShare"),
        "pinLatchReadings": int(const(texts["alerts"], "PinLatchReadings")),
        "pinScaleMaxA": const(texts["devices"], "PinScaleA"),
    }, digests


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage1-src", required=True)
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()
    values, digests = extract(args.stage1_src)
    with open(CONFIG, encoding="utf-8") as f:
        current = json.load(f)
    if args.write:
        current.update(values)
        current["_provenance"]["sha256"] = digests
        with open(CONFIG, "w", encoding="utf-8") as f:
            json.dump(current, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print("written", CONFIG)
        return 0
    diffs = {k: (current.get(k), v) for k, v in values.items() if current.get(k) != v}
    if diffs:
        print("Stage 1 기준과 다릅니다:", json.dumps(diffs, ensure_ascii=False))
        return 1
    print("Stage 1 기준과 일치:", json.dumps(values, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
