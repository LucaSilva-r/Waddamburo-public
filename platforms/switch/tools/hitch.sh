#!/usr/bin/env bash
# Symbolizes the shim's [hitch] profile (frames over 25 ms: both threads sampled every 1 ms, one block per 10 s) from
# a saved stderr log (tools/log.sh switch/taiko/shim.log > file), against out/switch/sent.elf (what tools/send.sh sent).
# usage: platforms/switch/tools/hitch.sh log [first-line] [last-line]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../../.." && pwd)
export OUT=$root/out/switch
log=$1 first=${2:-1} last=${3:-99999999}
sed -n "${first},${last}p" "$log" | grep -a '^\[hitch\] \(incl\|leaf\):' | grep -oP '@\K[0-9a-f]+' | sort -u > "$OUT/hitch-addr.txt"
image=devkitpro/devkita64@sha256:1fc388c3a0d34bd2045a6dadcb1020e069d5f876a187fd705de14b4440c00282
if [[ -n ${DEVKITPRO:-} ]]; then
    "$DEVKITPRO/devkitA64/bin/aarch64-none-elf-addr2line" -f -C -s -e "$OUT/sent.elf" < "$OUT/hitch-addr.txt"
else
    docker run --rm -i -v "$root:$root:z" "$image" /opt/devkitpro/devkitA64/bin/aarch64-none-elf-addr2line -f -C -s \
        -e "$OUT/sent.elf" < "$OUT/hitch-addr.txt"
fi 2>/dev/null | paste - - > "$OUT/hitch-names.txt"
paste "$OUT/hitch-addr.txt" "$OUT/hitch-names.txt" > "$OUT/hitch-sym.txt"
exec python3 -I "$here/hitch.py" "$log" "$first" "$last"
