#!/bin/bash
# Headless walk through the front-end flow: every --start-scene plus a scripted song (start, escape,
# and a silent full play through results, revival, game over and the attract loop). Writes each
# run's log and final screenshot to <out>, and <out>/transitions.txt with the scene transitions.
# Compare two builds' transitions.txt with diff: a flow refactor should not change a tick.
#
# usage: eng/flow-smoke.sh <game-root> <out> [full-play ticks, default 14000]
set -u
root=$(realpath "$1"); out=$(realpath -m "$2"); full=${3:-14000}
mkdir -p "$out"
cd "$(dirname "$0")/.."
dotnet build src/Waddamburo.App >/dev/null || exit 1
# Song Select: open the first folder, move to its first song, pick it, confirm the course.
song=F@200,K@350,F@450,F@650,F@800,F@950

run() { # name ticks args...
    local name=$1 ticks=$2; shift 2
    timeout 900 dotnet run --no-build --project src/Waddamburo.App -- "$@" --ticks="$ticks" \
        --screenshot="$out/$name.bmp" > "$out/$name.log" 2>&1
    echo "$name exit=$?"
}
game() { local name=$1 ticks=$2; shift 2; run "$name" "$ticks" "$root" "$@"; }

game boot 1500 --start-scene=boot
game attract-coin 400 --start-scene=attract --press=F2@200
game entry 300 --start-scene=entry
game song-select 400 --start-scene=song-select --press=K@200,K@230
game result-clear 1500 --start-scene=result-clear
game result-fail 1500 --start-scene=result-fail
game retry 1200 --start-scene=retry
game gameover 1200 --start-scene=gameover
WADDAMBURO_WAIWAI_RESULT=30,70,10 game waiwai-result 1500 --start-scene=waiwai-result
game play-escape 1600 --start-scene=song-select --press=$song,ESCAPE@1300
# Without a sound root, song time follows the ticks instead of the audio clock.
run play-full "$full" --entry-song-select --asset-root="$root/data/lumendata/packed" --tja-root="$out" \
    --font="$root/data/font/font.ttf" --start-scene=song-select --press=$song

for log in "$out"/*.log; do
    echo "== $(basename "$log" .log)"
    grep -E "Showing|Queued rainbow|Rainbow|Loaded covered|Gameplay ended|Activated|Active scene|Coin credited|xception" "$log" \
        | grep -v Warning
done > "$out/transitions.txt"
echo "transitions: $out/transitions.txt"
