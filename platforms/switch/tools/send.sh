#!/usr/bin/env bash
# Sends out/package/Waddamburo.nro to hbmenu's netloader (press Y in hbmenu) at $SWITCH_IP, and keeps the matching
# out/switch/sent.elf for tools/hitch.sh. The port also answers while the game runs: only nxlink's result says it was sent.
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
: "${SWITCH_IP:?set SWITCH_IP to the address of the console}"
nro=$root/out/package/Waddamburo.nro
[[ $nro -nt $root/out/switch/link.log ]] || { echo "error: $nro is older than the last link (the build failed?)" >&2; exit 1; }
image=devkitpro/devkita64@sha256:1fc388c3a0d34bd2045a6dadcb1020e069d5f876a187fd705de14b4440c00282
nxlink=${DEVKITPRO:+$DEVKITPRO/tools/bin/nxlink}
if [[ -n $nxlink ]]; then result=$("$nxlink" -a "$SWITCH_IP" "$nro" 2>&1 || true)
else result=$(timeout 200 docker run --rm --network host -v "$root:$root:z" "$image" /opt/devkitpro/tools/bin/nxlink -a "$SWITCH_IP" "$nro" 2>&1 || true); fi
tail -1 <<<"$result"
grep -q ' sent (' <<<"$result" || { echo "NOT SENT (is the netloader open?)" >&2; exit 1; }
cp "$root/out/switch/app.elf" "$root/out/switch/sent.elf"
