#!/bin/bash
# Headless walk through the front-end flow: every --start-scene plus a scripted song (start, escape,
# and a silent full play through results, revival, game over and the attract loop), then one play per
# mode (--flow-smoke: normal, two players, Waiwai, AI battle, dan, online echo, review, training, attract
# demo). Writes each run's log and final screenshot to <out>, and <out>/transitions.txt with the scene
# transitions and play results. Compare two builds' outputs with eng/flow-smoke-compare.py: a flow
# refactor should not change a scene or a result. Ticks may move by one or two (the loop drops ticks
# while loading); review and training follow the real frame clock, so their ticks are left out.
# Runs go four at a time. See docs/development/app-refactor.md.
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
    # Its scratch game folder goes at once: each builds its own caches (tens of MB), 20 runs fill a small /tmp.
    rm -rf -- "${out:?}/home/$name"
}
# Every run gets its own scratch game folder (the game's data and custom songs linked in), so none touches
# the real settings or scores, parallel runs don't share a score database, and a song never pauses when
# the window loses focus.
home() { # name -> the folder
    local home="$out/home/$1"
    mkdir -p "$home/waddamburo"
    for linked in data custom_songs cache; do
        [ -e "$root/$linked" ] && ln -sfn "$root/$linked" "$home/$linked"
    done
    printf 'mode = home\ndev_no_focus_pause = true\nmaster_volume = 0\n' > "$home/waddamburo/config.cfg"
    echo "$home"
}
game() { local name=$1 ticks=$2; shift 2; run "$name" "$ticks" "$(home "$name")" "$@"; }
mode() { # name ticks mode [args...]
    local name=$1 ticks=$2 smoke=$3; shift 3
    run "mode-$name" "$ticks" "$(home "mode-$name")" --start-scene=song-select --flow-smoke="$smoke" "$@"
}
# --entry-song-select needs a font file: the game folder's own, else the system's.
font=$(ls "$root"/data/font/font.{ttf,otf,ttc} 2>/dev/null | head -1)
[ -n "$font" ] || font=$(fc-match -f '%{file}' sans)
one='--flow-smoke-song=!Result Test'
kinds='--flow-smoke-song=Note Kind Check'

# Longest first, four at a time. Tick limits end each run a little after its last scene change.
mode dan 24500 dan &
run mode-ai-battle 9500 "$(home mode-ai-battle)" --start-scene=ai-song-select --flow-smoke=ai-battle "$kinds" &
run mode-demo 9000 "$(home mode-demo)" --start-scene=attract --flow-smoke=demo &
mode waiwai 8500 waiwai &
wait
# Without a sound root, song time follows the ticks instead of the audio clock.
run play-full "$full" --entry-song-select --asset-root="$root/data/lumendata/packed" --tja-root="$out" \
    --font="$font" --start-scene=song-select --press=$song &
mode training 3500 training "$kinds" &
mode review 3000 review "$one" &
mode normal 3200 normal "$one" &
wait
mode two-players 3200 two-players "$one" &
WADDAMBURO_ONLINE_ECHO=40 mode online-echo 3200 normal "$one" &
game play-escape 1600 --start-scene=song-select --press=$song,ESCAPE@1300 &
game result-clear 1500 --start-scene=result-clear &
wait
game result-fail 1500 --start-scene=result-fail &
game retry 1200 --start-scene=retry &
game gameover 1200 --start-scene=gameover &
WADDAMBURO_WAIWAI_RESULT=30,70,10 game waiwai-result 1500 --start-scene=waiwai-result &
wait
game boot 1500 --start-scene=boot &
game attract-coin 400 --start-scene=attract --press=F2@200 &
game entry 300 --start-scene=entry &
game song-select 400 --start-scene=song-select --press=K@200,K@230 &
wait

pattern="Showing|Queued rainbow|Rainbow|Loaded covered|Gameplay ended|Activated|Active scene|Coin credited|Smoke|Play results|Demo play|Practice from|xception"
for log in "$out"/*.log; do
    echo "== $(basename "$log" .log)"
    lines=$(grep -E "$pattern" "$log" | grep -v -e Warning -e 'Audio underrun')
    case $(basename "$log") in
        mode-review.log|mode-training.log) sed -E 's/(at )?tick [0-9]+//g' <<< "$lines" ;;
        *) echo "$lines" ;;
    esac
done > "$out/transitions.txt"
grep -l "Smoke failed\|Unhandled exception" "$out"/*.log 2>/dev/null | sed 's/^/FAILED: /'
echo "transitions: $out/transitions.txt"
