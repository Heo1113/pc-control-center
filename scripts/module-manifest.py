#!/usr/bin/env python3
"""Import manifest for the four source programs integrated into PC Control Center.

  generate <clones-dir>   record origin commit + SHA-256 of every imported file (run right after a pristine import)
  verify                  compare modules/ against the manifest: unchanged / modified / added / removed

The origin repositories are only read. Nothing here writes outside this repository.
"""
import hashlib, json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODULES = os.path.join(ROOT, "modules")
MANIFEST = os.path.join(MODULES, "IMPORTS.json")

SOURCES = [
    {"id": "A", "dir": "local-ai-performance-center", "clone": "A",
     "repository": "https://github.com/Heo1113/local-ai-performance-center", "branch": "main",
     "commit": "5b6aae12c28efbdbebe9a7df280c02f5494b037e"},
    {"id": "B", "dir": "gpu-profit-center", "clone": "B",
     "repository": "https://github.com/Heo1113/gpu-profit-center", "branch": "main",
     "commit": "5af1902c59fbcb8f559920b69645a411cb8c0bf7"},
    {"id": "C", "dir": "windows-diagnostics-lab", "clone": "C",
     "repository": "https://github.com/Heo1113/windows-diagnostics-lab", "branch": "claude/ecstatic-newton-cfosml",
     "commit": "0b4aa14ccf674a54285e7ae1d7c85b2fdbb4859c"},
    {"id": "E", "dir": "windows-ai-operator", "clone": "E",
     "repository": "https://github.com/Heo1113/windows-ai-operator", "branch": "claude/nifty-pascal-8te367",
     "commit": "a0562eec6284e71589128b763db26b7e236476d8"},
]

# Imported top-level entries. Root build files (props, slnx, global.json, NuGet.config, CI) are unified at the
# repository root instead. B's and C's vendor/ copies of A are NOT imported: B and C reference A's single copy.
INCLUDE = ["src", "tests", "docs", "scripts", "README.md", "THIRD-PARTY-NOTICES.md"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def walk(base):
    out = []
    for entry in INCLUDE:
        p = os.path.join(base, entry)
        if os.path.isfile(p):
            out.append(entry)
        elif os.path.isdir(p):
            for d, dirs, files in os.walk(p):
                dirs[:] = [x for x in dirs if x not in ("bin", "obj", ".git")]
                for f in files:
                    out.append(os.path.relpath(os.path.join(d, f), base).replace(os.sep, "/"))
    return sorted(out)


def generate(clones):
    manifest = {"note": "Pristine import of the four source programs. 'sha256' is the origin file at 'commit'.",
                "sources": []}
    for s in SOURCES:
        clone = os.path.join(clones, s["clone"])
        head = subprocess.check_output(["git", "-C", clone, "rev-parse", "HEAD"], text=True).strip()
        if head != s["commit"]:
            sys.exit(f"{s['id']}: clone is at {head}, expected {s['commit']}")
        files = {}
        for rel in walk(clone):
            files[rel] = sha256(os.path.join(clone, rel))
        entry = {k: v for k, v in s.items() if k != "clone"}
        entry["files"] = files
        manifest["sources"].append(entry)
    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=1, sort_keys=False)
        f.write("\n")
    print(f"wrote {MANIFEST}")


def verify(verbose):
    with open(MANIFEST, encoding="utf-8") as f:
        manifest = json.load(f)
    total = {"unchanged": 0, "modified": 0, "removed": 0, "added": 0}
    for s in manifest["sources"]:
        base = os.path.join(MODULES, s["dir"])
        present = set(walk(base))
        recorded = s["files"]
        rows = []
        for rel, digest in recorded.items():
            if rel not in present:
                rows.append(("removed", rel))
            elif sha256(os.path.join(base, rel)) != digest:
                rows.append(("modified", rel))
            else:
                total["unchanged"] += 1
        for rel in sorted(present - set(recorded)):
            rows.append(("added", rel))
        for kind, rel in rows:
            total[kind] += 1
            if verbose or kind != "unchanged":
                print(f"{s['id']} {kind:9} {s['dir']}/{rel}")
    print("summary:", ", ".join(f"{k} {v}" for k, v in total.items()))


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "generate":
        generate(sys.argv[2])
    elif len(sys.argv) >= 2 and sys.argv[1] == "verify":
        verify("-v" in sys.argv)
    else:
        sys.exit(__doc__)
