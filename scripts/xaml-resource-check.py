#!/usr/bin/env python3
"""Checks that every {StaticResource} the integrated window loads can be found while the XAML loads.

WPF resolves {StaticResource} while a XAML file loads. A module view (UserControl) or window loads in its constructor,
before it is inside its module's container, so at that moment only the file's own resources and Application.Resources
are searched. PC Control Center therefore has (see src/PcControlCenter.App/Hosting/ModuleResourceScope.cs):

  application scope = src/PcControlCenter.App/App.xaml (with A's palette and Themes/Shell.xaml)
                    + every string key that exactly one module's ModuleResources.xaml defines and App.xaml does not
                      (copied at startup)

Rules checked here:
  * a module's XAML may use: its own keys, application keys, and keys copied from its own module;
  * a key that several modules define (not in App.xaml) is never copied, so no module XAML may use it statically;
  * the shell's own XAML may use its own keys and App.xaml keys only (explicit names for module converters);
  * where a module XAML gets an App.xaml definition that differs from its own module's definition, the difference must be
    reviewed (listed in REVIEWED with the reason), so a silent change of meaning cannot slip in.

Exit code 1 on any problem. Run from anywhere: python3 scripts/xaml-resource-check.py
"""
from __future__ import annotations

import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"
SHELL = ROOT / "src/PcControlCenter.App"
MODULES = {
    "local-ai": ("LocalAiPerf.App", ROOT / "modules/local-ai-performance-center/src/LocalAiPerf.App"),
    "gpu-economics": ("GpuProfit.App", ROOT / "modules/gpu-profit-center/src/GpuProfit.App"),
    "diagnostics": ("WinDiag.App", ROOT / "modules/windows-diagnostics-lab/src/WinDiag.App"),
    "operator": ("WindowsAiOperator.App", ROOT / "modules/windows-ai-operator/src/WindowsAiOperator.App"),
}
ASSEMBLY_DIRS = {name: path for name, path in MODULES.values()} | {"PcControlCenter": SHELL}

# Module XAML files the integrated window never loads (the shell replaces them).
NOT_LOADED = {"App.xaml", "MainWindow.xaml"}

# App.xaml definitions a module view gets instead of its own module's definition, reviewed one by one.
REVIEWED = {
    "BoolToVisibility": "shell converter accepts true and counts > 0 (A/B/C) and true (E): Converters.cs",
    "InverseBoolToVisibility": "same converter, inverted",
    "NotEmptyToVisibility": "shell converter hides null, blank text, count 0 (E) and empty collections; "
                            "A/B/C showed a text of only spaces: Converters.cs",
    "BigValue": "A's style (font size 28); B and C used 24 - shared theme, cosmetic",
    "CardTitle": "A's style; E's adds a 4 px bottom margin - shared theme, cosmetic",
    "ToolButton": "shell style (padding 10,3); E's uses 10,4 - cosmetic",
}

STATIC = re.compile(r"\{StaticResource\s+(?:ResourceKey=)?([^\s,}]+)\s*\}")
STATIC_ELEMENT = re.compile(r"<StaticResource\s+[^>]*ResourceKey=\"([^\"]+)\"")
PACK = re.compile(r"(?:pack://application:,,,)?/([^;]+);component/(.+)")


def resolve(source: str, owner: pathlib.Path) -> pathlib.Path:
    match = PACK.match(source)
    if match:
        return ASSEMBLY_DIRS[match.group(1)] / match.group(2)
    return (owner.parent / source).resolve()


def definitions(path: pathlib.Path, seen: set[pathlib.Path] | None = None) -> dict[str, str]:
    """key -> normalized definition, for a file and the dictionaries it merges (own keys win, later merges win)."""
    seen = set() if seen is None else seen
    if path in seen:
        return {}
    seen.add(path)
    root = ET.parse(path).getroot()
    found: dict[str, str] = {}
    for element in root.iter():
        if element.tag.split("}")[-1] == "ResourceDictionary" and element.get("Source"):
            found.update(definitions(resolve(element.get("Source"), path), seen))
    for element in root.iter():
        key = element.get(XAML_NS + "Key")
        if key and not key.startswith("{"):
            found[key] = normalize(element)
    return found


def normalize(element: ET.Element) -> str:
    text = ET.tostring(element, encoding="unicode")
    text = re.sub(r";assembly=[^\"}]+", "", text)
    return re.sub(r"\s+", " ", text).strip()


def references(path: pathlib.Path) -> set[str]:
    text = path.read_text(encoding="utf-8-sig")
    return {k for k in STATIC.findall(text) + STATIC_ELEMENT.findall(text) if not k.startswith("{")}


def xaml_files(directory: pathlib.Path):
    for path in sorted(directory.rglob("*.xaml")):
        parts = set(path.relative_to(directory).parts)
        if "bin" in parts or "obj" in parts:
            continue
        yield path


def main() -> int:
    application = definitions(SHELL / "App.xaml")
    module_definitions = {module: definitions(path / "ModuleResources.xaml") for module, (_, path) in MODULES.items()}

    owners: dict[str, list[str]] = {}
    for module, defs in module_definitions.items():
        for key in defs:
            if key not in application:
                owners.setdefault(key, []).append(module)
    copied = {key: mods[0] for key, mods in owners.items() if len(mods) == 1}
    several = {key: mods for key, mods in owners.items() if len(mods) > 1}

    problems: list[str] = []
    reviewed_seen: dict[str, set[str]] = {}
    checked = 0
    for module, (_, directory) in MODULES.items():
        own = module_definitions[module]
        for path in xaml_files(directory):
            if path.name in NOT_LOADED or path.name == "ModuleResources.xaml" or path.parent.name == "Themes":
                continue
            checked += 1
            local = definitions(path)
            for key in sorted(references(path)):
                where = path.relative_to(ROOT)
                if key in local:
                    continue
                if key in application:
                    if key in own and own[key] != application[key]:
                        if key in REVIEWED:
                            reviewed_seen.setdefault(key, set()).add(module)
                        else:
                            problems.append(f"{where}: '{key}' gets the shell's definition, which differs from {module}'s own; "
                                            "review it and add it to REVIEWED with the reason")
                    continue
                if copied.get(key) == module:
                    continue
                if key in several:
                    problems.append(f"{where}: '{key}' is defined by several modules ({', '.join(several[key])}) and is "
                                    "therefore not visible while the view loads")
                elif key in copied:
                    problems.append(f"{where}: '{key}' comes from module {copied[key]}, not from {module}")
                else:
                    problems.append(f"{where}: '{key}' is not defined anywhere it can be found while loading")

    for path in xaml_files(SHELL):
        if path.name == "App.xaml" or path.parent.name == "Themes":
            continue
        checked += 1
        local = definitions(path)
        for key in sorted(references(path)):
            if key not in local and key not in application:
                hint = f" (module key of {copied.get(key) or ', '.join(several.get(key, []))}: give it an explicit name in App.xaml)" \
                    if key in owners else ""
                problems.append(f"{path.relative_to(ROOT)}: '{key}' is not in App.xaml or the file itself{hint}")

    print(f"checked {checked} XAML files; copied to the application at startup: {len(copied)} keys; "
          f"defined by several modules (containers only): {', '.join(sorted(several)) or 'none'}")
    for key, modules in sorted(reviewed_seen.items()):
        print(f"  reviewed difference '{key}' ({', '.join(sorted(modules))}): {REVIEWED[key]}")
    unused = sorted(set(REVIEWED) - set(reviewed_seen))
    if unused:
        problems.append(f"REVIEWED lists keys that no longer differ: {', '.join(unused)} (remove them)")
    for problem in problems:
        print("ERROR " + problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
