#!/usr/bin/env python3
"""Writes the list of Green's game data files that the Android setup checks a copy against.

Usage: eng/green-data-list.py <Green USRDIR> <out.txt.gz>

Names only, no sizes or content: language packs and other mods change the files' sizes, never which files there are.
Only the files as installed (their 2020-08-27 date) are listed, so backups and tool files added later are left out;
a file some tools patch keeps its installed name and stays listed.
"""
import datetime, gzip, os, sys

usrdir, out = sys.argv[1], sys.argv[2]
INSTALLED = datetime.date(2020, 8, 27)
PATCHED = {"data/lumendata/packed/entry/packeddata.ddp", "data/config/S11100-1/chassisinfo.xml"}
paths = []
for folder, _, files in os.walk(os.path.join(usrdir, "data")):
    for name in files:
        full = os.path.join(folder, name)
        path = os.path.relpath(full, usrdir).replace(os.sep, "/")
        if path in PATCHED or datetime.date.fromtimestamp(os.stat(full).st_mtime) == INSTALLED:
            paths.append(path)
paths.sort()
with gzip.open(out, "wt", encoding="utf-8", newline="\n") as f:
    f.writelines(path + "\n" for path in paths)
print(f"{len(paths)} files")
