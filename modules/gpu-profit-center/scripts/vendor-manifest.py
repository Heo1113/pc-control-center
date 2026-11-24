#!/usr/bin/env python3
"""Recomputes currentSha256 in vendor/local-ai-performance-center/manifest.json (run after a reviewed vendor update)."""
import hashlib, json, pathlib, sys
root = pathlib.Path(__file__).resolve().parent.parent / "vendor" / "local-ai-performance-center"
manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
for f in manifest["files"]:
    data = (root / f["path"]).read_bytes()
    f["currentSha256"] = hashlib.sha256(data).hexdigest()
    f["status"] = "unchanged" if f["currentSha256"] == f["sourceSha256"] else "modified"
(root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("\n".join(f'{f["status"]:9} {f["path"]}' for f in manifest["files"]))
