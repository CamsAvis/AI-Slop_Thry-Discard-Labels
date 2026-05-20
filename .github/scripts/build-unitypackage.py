#!/usr/bin/env python3
"""Build a Unity .unitypackage from this repo's source files.

A .unitypackage is a gzipped tar where each asset gets a directory named by its
Unity GUID, containing:
  - `pathname`   the path the asset should land at inside the importer's Unity project
  - `asset.meta` the .meta file (preserves the GUID Unity uses for references)
  - `asset`      the asset file content (omitted for folder assets)

This repo doesn't track .meta files, so we generate them deterministically:
GUID = md5(unity_path) so every build emits the same GUIDs and the package
stays referentially stable across builds.
"""

import gzip
import hashlib
import sys
import tarfile
import tempfile
from pathlib import Path

# Where this tool should land inside the importer's Unity project.
ASSET_ROOT = "Assets/!Cam/Tools/PoiyomiTileLabels"

# Skip dotfiles/dot-folders (e.g. .git, .github, .gitignore) anywhere in the tree.
def is_asset_path(path: Path, repo_root: Path) -> bool:
    rel = path.relative_to(repo_root)
    for part in rel.parts:
        if part.startswith("."):
            return False
    if path.suffix in {".meta", ".unitypackage"}:
        return False
    return True


def guid_for(unity_path: str) -> str:
    return hashlib.md5(unity_path.encode("utf-8")).hexdigest()


FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

SCRIPT_META = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

DEFAULT_META = """fileFormatVersion: 2
guid: {guid}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def make_meta(asset_path: Path, guid: str) -> str:
    if asset_path.is_dir():
        return FOLDER_META.format(guid=guid)
    if asset_path.suffix == ".cs":
        return SCRIPT_META.format(guid=guid)
    return DEFAULT_META.format(guid=guid)


def collect_assets(repo_root: Path):
    """Return (asset_path, unity_path) for every folder and file we want in the package.

    Includes the ASSET_ROOT folder itself as a folder asset (Unity's own exporter
    does this for every parent folder under the export root, and importers expect
    a folder-asset entry for the root or they may skip recreating it).
    """
    assets = [(repo_root, ASSET_ROOT)]

    def walk(dir_path: Path):
        for entry in sorted(dir_path.iterdir(), key=lambda p: p.name):
            if not is_asset_path(entry, repo_root):
                continue
            rel = entry.relative_to(repo_root)
            unity_path = f"{ASSET_ROOT}/{rel.as_posix()}"
            assets.append((entry, unity_path))
            if entry.is_dir():
                walk(entry)

    walk(repo_root)
    return assets


def main() -> int:
    repo_root = Path(__file__).resolve().parents[2]
    output = repo_root / f"{repo_root.name}.unitypackage"

    assets = collect_assets(repo_root)
    if not assets:
        print("no assets found — nothing to package", file=sys.stderr)
        return 1

    print(f"packaging {len(assets)} asset(s) into {output.name}")

    seen_guids: dict[str, str] = {}
    with tempfile.TemporaryDirectory() as staging_dir:
        staging = Path(staging_dir)
        for asset_path, unity_path in assets:
            guid = guid_for(unity_path)
            if guid in seen_guids:
                print(
                    f"ERROR: GUID collision {guid} between {unity_path} and {seen_guids[guid]}",
                    file=sys.stderr,
                )
                return 1
            seen_guids[guid] = unity_path

            entry_dir = staging / guid
            entry_dir.mkdir()

            # write_bytes (not write_text) so Python's universal-newline translation
            # on Windows doesn't slip \r\n into pathname/asset.meta. Unity's pathname
            # field is the raw path string with NO trailing newline — adding one (or
            # CRLF) makes Unity's importer skip the entry as malformed.
            (entry_dir / "pathname").write_bytes(unity_path.encode("utf-8"))
            (entry_dir / "asset.meta").write_bytes(
                make_meta(asset_path, guid).encode("utf-8")
            )
            if asset_path.is_file():
                (entry_dir / "asset").write_bytes(asset_path.read_bytes())

            kind = "folder" if asset_path.is_dir() else "file"
            print(f"  {guid}  [{kind}]  {unity_path}")

        # Unity's .unitypackage tar reader is the old SharpZipLib-derived parser
        # and chokes on two things tarfile does by default:
        #   1. PAX extended headers (default in Python 3.14) — force GNU_FORMAT.
        #   2. A gzip FNAME header ending in ".unitypackage" — tarfile.open("w:gz")
        #      copies the output filename into the gzip FNAME field, so naming the
        #      output ".unitypackage" poisons its own gzip wrapper. Unity's importer
        #      sees that and refuses to extract anything (returns 0 items, no error).
        #      Wrap an explicit GzipFile with filename="archtemp.tar" (what Unity's
        #      own ExportPackage uses) to dodge this.
        def _normalize(info: tarfile.TarInfo) -> tarfile.TarInfo:
            info.mtime = 0
            info.uid = 0
            info.gid = 0
            info.uname = ""
            info.gname = ""
            info.mode = 0o777
            return info

        with open(output, "wb") as out_fp, \
             gzip.GzipFile(filename="archtemp.tar", mode="wb", fileobj=out_fp, mtime=0) as gz, \
             tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tar:
            for entry in sorted(staging.iterdir(), key=lambda p: p.name):
                tar.add(entry, arcname=entry.name, filter=_normalize)

    size_kb = output.stat().st_size / 1024
    print(f"wrote {output} ({size_kb:.1f} KB, {len(assets)} assets)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
