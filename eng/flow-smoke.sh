#!/bin/bash
# Headless walk through the front-end flow: every --start-scene plus a scripted song (start, escape,
# and a silent full play through results, revival, game over and the attract loop), then one play per
# mode (--flow-smoke: normal, two players, Waiwai, AI battle, dan, online echo, review, training, attract
# demo). Writes each run's log and final screenshot to <out>, and <out>/transitions.txt with the scene
# transitions and play results. Compare two builds' transitions.txt with diff: a flow refactor should not
# change a tick (review and training follow the real frame clock: their ticks are left out).
# See docs/development/app-refactor.md.
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
    timeout 1800 dotnet run --no-build --project src/Waddamburo.App -- "$@" --ticks="$ticks" \
        --screenshot="$out/$name.bmp" --no-crash-screen > "$out/$name.log" 2>&1
    echo "$name exit=$?"
}
# Every run uses a scratch game folder (the game's data and custom songs linked in), so none touches the
# real settings or scores; its settings never pause a song when the window loses focus.
home="$out/home"
mkdir -p "$home/waddamburo"
for linked in data custom_songs cache; do
    [ -e "$root/$linked" ] && ln -sfn "$root/$linked" "$home/$linked"
done
printf 'mode = home\ndev_no_focus_pause = true\n' > "$home/waddamburo/config.cfg"
game() { local name=$1 ticks=$2; shift 2; run "$name" "$ticks" "$home" "$@"; }

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
# Without a sound root, song time follows the ticks instead of the audio clock. --entry-song-select needs a
# font file: the game folder's own, else the system's.
font=$(ls "$root"/data/font/font.{ttf,otf,ttc} 2>/dev/null | head -1)
[ -n "$font" ] || font=$(fc-match -f '%{file}' sans)
run play-full "$full" --entry-song-select --asset-root="$root/data/lumendata/packed" --tja-root="$out" \
    --font="$font" --start-scene=song-select --press=$song

# One play per mode. Charts: the custom_songs/Test Charts ones where a mode allows.
mode() { # name ticks mode [args...]
    local name=$1 ticks=$2 smoke=$3; shift 3
    run "mode-$name" "$ticks" "$home" --start-scene=song-select --flow-smoke="$smoke" "$@"
}
one='--flow-smoke-song=!Result Test'
kinds='--flow-smoke-song=Note Kind Check'
mode normal 2500 normal "$one"
mode two-players 2500 two-players "$one"
WADDAMBURO_ONLINE_ECHO=40 mode online-echo 2500 normal "$one"
mode waiwai 14000 waiwai
run mode-ai-battle 9000 "$home" --start-scene=ai-song-select --flow-smoke=ai-battle "$kinds"
mode dan 40000 dan
mode review 2500 review "$one"
mode training 3000 training "$kinds"
run mode-demo 9000 "$home" --start-scene=attract --flow-smoke=demo

pattern="Showing|Queued rainbow|Rainbow|Loaded covered|Gameplay ended|Activated|Active scene|Coin credited|Smoke|Play results|Demo play|Practice from|xception"
for log in "$out"/*.log; do
    echo "== $(basename "$log" .log)"
    case $(basename "$log") in
        mode-review.log|mode-training.log) grep -E "$pattern" "$log" | grep -v Warning | sed -E 's/(at )?tick [0-9]+//g' ;;
        *) grep -E "$pattern" "$log" | grep -v Warning ;;
    esac
done > "$out/transitions.txt"
grep -l "Smoke failed\|Unhandled exception" "$out"/*.log 2>/dev/null | sed 's/^/FAILED: /'
echo "transitions: $out/transitions.txt"
