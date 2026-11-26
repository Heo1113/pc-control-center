#!/usr/bin/env python3
"""config/*.json → assets/iron-config.js (브라우저가 file://에서도 같은 기준을 쓰도록). 직접 수정하지 말 것.

  python -I tools/build_iron_config.py          # 생성/갱신
  python -I tools/build_iron_config.py --check  # config와 다르면 exit 1
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "assets" / "iron-config.js"


def render():
    load = lambda n: json.loads((ROOT / "config" / n).read_text(encoding="utf-8"))
    cfg = {"thresholds": load("iron_stage1_thresholds.json"), "rules": load("mapping_rules.json"), "gates": load("gates.json")}
    body = json.dumps(cfg, ensure_ascii=False, indent=1)
    return ("/* 자동 생성 파일 — 직접 수정 금지. 원본: config/iron_stage1_thresholds.json, config/mapping_rules.json, config/gates.json\n"
            " * 생성: python -I tools/build_iron_config.py */\n"
            "(function (root) { root.IRON_CONFIG = " + body + "; })(typeof self !== 'undefined' ? self : this);\n")


SYN = ROOT / "assets" / "iron-synthetic.js"


def render_synthetic():
    """HTML의 '합성 픽스처' 버튼과 SIMULATION 생성기가 쓰는 SYNTHETIC read/sdb (tests/fixtures 원본)."""
    fx = ROOT / "tests" / "fixtures"
    data = {"read": json.loads((fx / "synthetic_full_read_v1.json").read_text(encoding="utf-8")),
            "sdb": json.loads((fx / "synthetic_full_sdb_v1.json").read_text(encoding="utf-8"))}
    return ("/* 자동 생성 파일 — 직접 수정 금지. SYNTHETIC(실측 아님). 원본: tests/fixtures/synthetic_full_*_v1.json\n"
            " * 생성: python -I tools/build_iron_config.py */\n"
            "(function (root) { root.IRON_SYNTHETIC = " + json.dumps(data, ensure_ascii=False) + "; })(typeof self !== 'undefined' ? self : this);\n")


def main():
    targets = [(OUT, render()), (SYN, render_synthetic())]
    stale = []
    for path, text in targets:
        old = path.read_text(encoding="utf-8") if path.exists() else None
        if old != text:
            stale.append(path.name)
            if "--check" not in sys.argv:
                path.write_text(text, encoding="utf-8")
    if "--check" in sys.argv:
        print("config·픽스처와 일치" if not stale else "다름 — tools/build_iron_config.py 실행 필요: " + ", ".join(stale))
        return 1 if stale else 0
    print("갱신: " + (", ".join(stale) if stale else "없음(최신)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
