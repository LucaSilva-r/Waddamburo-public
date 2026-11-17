#!/usr/bin/env python3
"""Writes the list of Green's game data files that the Android setup checks a copy against.

Usage: eng/green-data-list.py <Green USRDIR> <out.txt.gz>

From the game's own install manifest of the release, USRDIR/hash/S111_00.md5: what every Green has (S111_01 to _13
are its updates, which add songs; an un-updated Green plays too). Names only, no hashes or sizes: language packs and
other mods change files' contents, never which files there are; and a folder updated in place for years holds
leftovers and tool files the manifest leaves out.
"""
import glob, gzip, os, sys

usrdir, out = sys.argv[1], sys.argv[2]
manifests = sorted(glob.glob(os.path.join(usrdir, "hash", "S111_00.md5")))
if not manifests:
    sys.exit(f"no hash/S111_00.md5 in {usrdir}")
paths = set()
for manifest in manifests:
    with open(manifest, encoding="utf-8", errors="replace") as f:
        for line in f:
            parts = line.strip().split(None, 1)
            # @-folders (lumendata/packed/@ASB) are variants the manifest lists and an updated Green no longer has.
            if len(parts) == 2 and "/@" not in parts[1].replace("\\", "/"):
                paths.add(parts[1].replace("\\", "/"))
paths = sorted(paths)
with gzip.open(out, "wt", encoding="utf-8", newline="\n") as f:
    f.writelines(path + "\n" for path in paths)
print(f"{len(paths)} files from {len(manifests)} manifests")
