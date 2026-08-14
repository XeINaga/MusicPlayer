#!/usr/bin/env python3
"""Generate a WiX v4 fragment (installer/files.wxs) listing every file in the
self-contained publish output, with nested <Directory> elements preserved.

WiX v4 dropped the `heat` subcommand, so we generate the file list directly.
Usage: gen_files_wxs.py <publish_dir> [output_wxs]
"""
import os
import sys
import hashlib

NS = "http://wixtoolset.org/schemas/v4/wxs"
SKIP_EXT = {".pdb"}


def esc(s: str) -> str:
    return (s.replace("&", "&amp;")
             .replace("<", "&lt;")
             .replace(">", "&gt;")
             .replace('"', "&quot;"))


def sanitize(s: str) -> str:
    return "".join(c if (c.isalnum() or c in "-_") else "_" for c in s)


def main():
    publish_dir = sys.argv[1] if len(sys.argv) > 1 else \
        r"bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
    out = sys.argv[2] if len(sys.argv) > 2 else "installer/files.wxs"
    publish_dir = os.path.abspath(publish_dir)

    if not os.path.isdir(publish_dir):
        sys.exit(f"publish dir not found: {publish_dir}")

    parts = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<Wix xmlns="{NS}">',
        '  <Fragment>',
        '    <DirectoryRef Id="INSTALLFOLDER">',
    ]
    comp_ids = []

    def emit_dir(rel_dir: str):
        abs_dir = os.path.join(publish_dir, rel_dir) if rel_dir else publish_dir
        try:
            entries = sorted(os.listdir(abs_dir))
        except OSError:
            return
        # Files first.
        for name in entries:
            abs_path = os.path.join(abs_dir, name)
            if not os.path.isfile(abs_path):
                continue
            if os.path.splitext(name)[1].lower() in SKIP_EXT:
                continue
            rel = os.path.join(rel_dir, name) if rel_dir else name
            h = hashlib.md5(rel.encode("utf-8")).hexdigest()[:12]
            comp_id = "cmp_" + h
            file_id = "file_" + h
            src = esc(abs_path.replace("/", "\\"))
            parts.append(f'      <Component Id="{comp_id}" Guid="*">')
            parts.append(f'        <File Id="{file_id}" Source="{src}" KeyPath="yes" />')
            parts.append('      </Component>')
            comp_ids.append(comp_id)
        # Then subdirectories.
        for name in entries:
            abs_path = os.path.join(abs_dir, name)
            if not os.path.isdir(abs_path):
                continue
            rel = os.path.join(rel_dir, name) if rel_dir else name
            h = hashlib.md5(rel.encode("utf-8")).hexdigest()[:12]
            dir_id = "dir_" + h
            parts.append(f'      <Directory Id="{dir_id}" Name="{esc(name)}">')
            emit_dir(rel)
            parts.append('      </Directory>')

    emit_dir("")

    parts.append('    </DirectoryRef>')
    parts.append('    <ComponentGroup Id="AppFiles">')
    for cid in comp_ids:
        parts.append(f'      <ComponentRef Id="{cid}" />')
    parts.append('    </ComponentGroup>')
    parts.append('  </Fragment>')
    parts.append('</Wix>')

    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(parts) + "\n")

    print(f"Wrote {out}: {len(comp_ids)} files from {publish_dir}")


if __name__ == "__main__":
    main()
