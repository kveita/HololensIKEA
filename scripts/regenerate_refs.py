"""
regenerate_refs.py
==================
Rewrites the <Reference> ItemGroup in HololensIKEA.csproj so that every
HintPath points at the UWP NuGet packages on the current machine.

Run this once after cloning if Visual Studio is installed to a drive or
path other than the default (C:\Program Files\...).

Usage:
    python scripts/regenerate_refs.py

Requirements:
    - Python 3.6+  (stdlib only, no pip packages needed)
    - Visual Studio 2022 with the Universal Windows Platform workload
      (installs UWPNuGetPackages and the framework lock file)
"""

import json
import os
import glob
import xml.etree.ElementTree as ET

# ?? Paths ?????????????????????????????????????????????????????????????????????

SCRIPT_DIR  = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT   = os.path.dirname(SCRIPT_DIR)
CSPROJ_PATH = os.path.join(REPO_ROOT, "HololensIKEA.csproj")

# Installed by the UWP workload of VS 2022
UWP_NUGET_PACKAGES = r"C:\Program Files (x86)\Microsoft SDKs\UWPNuGetPackages"

# Installed by the .NET Framework reference assemblies component
LOCK_FILE = (
    r"C:\Program Files (x86)\Reference Assemblies"
    r"\Microsoft\Framework\.NETCore\v5.0\project.lock.json"
)

NS = "http://schemas.microsoft.com/developer/msbuild/2003"
ET.register_namespace("", NS)

# ?? Helpers ???????????????????????????????????????????????????????????????????

def check_prerequisites():
    ok = True
    for path, label in [
        (UWP_NUGET_PACKAGES, "UWP NuGet packages"),
        (LOCK_FILE,          ".NETCore v5.0 project.lock.json"),
        (CSPROJ_PATH,        "HololensIKEA.csproj"),
    ]:
        exists = os.path.exists(path)
        print(f"{'OK  ' if exists else 'MISS'} {label}")
        print(f"     {path}")
        if not exists:
            ok = False
    return ok


def load_canonical_refs():
    """Read the 103 canonical compile-time refs from the framework lock file."""
    with open(LOCK_FILE, encoding="utf-8") as fh:
        data = json.load(fh)
    target = data["targets"].get(".NETCore,Version=v5.0", {})
    canonical = {}
    for pkg_id, pkg_data in target.items():
        for rel_path in pkg_data.get("compile", {}).keys():
            name = os.path.splitext(os.path.basename(rel_path))[0]
            if name.startswith("_"):
                continue
            canonical[name] = (pkg_id, rel_path)
    return canonical


def resolve_to_local_paths(canonical):
    """Map each canonical ref to an absolute path inside UWPNuGetPackages."""
    resolved = {}
    missing  = []
    tfm_priority = [
        "netcore50", "netstandard1.5", "netstandard1.3",
        "netstandard1.2", "netstandard1.0", "net462",
    ]

    for name, (pkg_id, rel_path) in sorted(canonical.items()):
        pkg_lower = pkg_id.split("/")[0].lower()
        rel_norm  = rel_path.replace("/", os.sep)
        pattern   = os.path.join(UWP_NUGET_PACKAGES, pkg_lower, "*", rel_norm)
        matches   = glob.glob(pattern)
        if matches:
            resolved[name] = matches[0]
            continue
        # Fallback: scan by TFM priority
        for tfm in tfm_priority:
            p2 = os.path.join(UWP_NUGET_PACKAGES, pkg_lower, "*",
                              "ref", tfm, name + ".dll")
            m2 = glob.glob(p2)
            if m2:
                resolved[name] = m2[0]
                break
        else:
            missing.append((name, pkg_id))

    return resolved, missing


def update_csproj(resolved):
    """Replace all <Reference> ItemGroups in the .csproj with new HintPaths."""
    tree = ET.parse(CSPROJ_PATH)
    root = tree.getroot()

    # Remove existing Reference ItemGroups
    to_remove = [
        ig for ig in root
        if ig.tag == "{%s}ItemGroup" % NS
        and ig.find("{%s}Reference" % NS) is not None
    ]
    for ig in to_remove:
        root.remove(ig)

    # Build new ItemGroup
    new_ig = ET.Element("{%s}ItemGroup" % NS)
    for name in sorted(resolved):
        ref  = ET.SubElement(new_ig, "{%s}Reference" % NS)
        ref.set("Include", name)
        ET.SubElement(ref, "{%s}HintPath" % NS).text = resolved[name]
        ET.SubElement(ref, "{%s}Private"  % NS).text = "false"

    # Insert before the first Compile ItemGroup
    children = list(root)
    idx = next(
        (i for i, c in enumerate(children)
         if c.tag == "{%s}ItemGroup" % NS
         and c.find("{%s}Compile" % NS) is not None),
        len(children),
    )
    root.insert(idx, new_ig)

    tree.write(CSPROJ_PATH, encoding="unicode", xml_declaration=True)


# ?? Main ??????????????????????????????????????????????????????????????????????

def main():
    print("=== HololensIKEA reference regenerator ===\n")

    print("Checking prerequisites...")
    if not check_prerequisites():
        print("\nERROR: one or more paths are missing.")
        print("Install VS 2022 with the Universal Windows Platform workload,")
        print("then re-run this script.")
        raise SystemExit(1)

    print("\nReading canonical references from framework lock file...")
    canonical = load_canonical_refs()
    print(f"  {len(canonical)} canonical refs found")

    print("\nResolving to local UWPNuGetPackages paths...")
    resolved, missing = resolve_to_local_paths(canonical)
    print(f"  Resolved: {len(resolved)}   Missing: {len(missing)}")
    if missing:
        print("\nWARNING — could not resolve:")
        for n, p in missing:
            print(f"  {n}  (from {p})")

    print(f"\nUpdating {CSPROJ_PATH} ...")
    update_csproj(resolved)
    print(f"Done — {len(resolved)} <Reference> entries written.\n")
    print("Next step: re-run the build.")
    print("  msbuild HololensIKEA.csproj /p:Configuration=Release /p:Platform=x86")


if __name__ == "__main__":
    main()
