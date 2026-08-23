#!/usr/bin/env python3
"""[SUPERSEDED] Group the language satellite folders of the self-contained
publish output into a single `locales\\` subfolder.

This script is no longer needed: MusicPlayer.csproj now defines a
`TrimLocaleFolders` / `TrimLocaleFoldersPublish` post-build Target that
DELETES every culture folder except `en-us` (load-bearing neutral resources)
and `zh-CN` right after Build and Publish — so both the dev output and the
publish folder are already tidy before the installer runs. `gen_files_wxs.py`
then harvests the trimmed publish folder directly.

Kept only as a fallback / reference for the relocate-instead-of-delete
approach. A folder is treated as a language folder iff it contains ONLY
satellite resource files (*.mui and/or *.resources.dll); the framework falls
back to its embedded neutral resources when a specific culture folder is not
found at the default location, so relocating them does not break anything.

Usage: group_locales.py <publish_dir>
Run BEFORE gen_files_wxs.py so the MSI packages the grouped layout.
"""
import os
import shutil
import sys

SATELLITE_EXT = {".mui", ".resources.dll"}

# en-us holds the LOAD-BEARING neutral resources (Microsoft.ui.xaml.dll.mui):
# moving it away crashes the app at startup with a stowed XAML exception
# (0xC000027B) before any managed handler runs. zh-CN stays at the root as
# well so framework strings keep showing in Chinese on Chinese systems.
KEEP_AT_ROOT = {"en-us", "zh-CN"}


def is_language_dir(path: str) -> bool:
    try:
        entries = os.listdir(path)
    except OSError:
        return False
    if not entries:
        return False
    for name in entries:
        full = os.path.join(path, name)
        if os.path.isdir(full):
            return False
        if os.path.splitext(name)[1].lower() not in SATELLITE_EXT:
            return False
    return True


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    publish_dir = sys.argv[1]
    dest = os.path.join(publish_dir, "locales")
    moved = 0

    for name in sorted(os.listdir(publish_dir)):
        full = os.path.join(publish_dir, name)
        if name in KEEP_AT_ROOT or name == "locales" or not os.path.isdir(full):
            continue
        if is_language_dir(full):
            os.makedirs(dest, exist_ok=True)
            shutil.move(full, os.path.join(dest, name))
            moved += 1

    print(f"Moved {moved} language folder(s) into {dest}")


if __name__ == "__main__":
    main()
