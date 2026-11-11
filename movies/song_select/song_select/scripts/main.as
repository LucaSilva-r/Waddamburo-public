// Song select, bare minimum: a list of folders, a folder's songs, a song's courses. Left and right rim move, DON
// decides. What it lists it asks the game for, a line at a time (Lumen.Wd…: not in the game's own song select);
// what it tells the game are the game's own calls, in the game's order.
import flash.external.ExternalInterface;

var ROWS = 9;
var CENTRE = 4;
var SECONDS = 100;
var KEYS = [[65, 83, 90], [68, 70, 67]];   // per drum: left rim, right rim, DON (A S Z, D F C)
var COURSES = ["EASY", "NORMAL", "HARD", "ONI", "URA"];
var SLOTS = [0, 1, 2, 3, 7];               // the game's numbers for those courses
var FOLDERS = 0;
var SONGS = 1;
var COURSE = 2;
var REPEAT_AFTER = 20;                     // frames a rim is held before it repeats
var REPEAT_EVERY = 4;

var self = this;
var joined = [false, false];
var ready = false;         // the game has set the list up
var asked = false;
var done = false;
var mode = FOLDERS;
var folder = 0;            // cursor among the folders
var song = -1;             // cursor in the open folder: -1 is the line that closes it
var course = 0;            // cursor among the offered courses: -1 is the line that leaves
var offered = [];          // the song's courses, as indices into COURSES
var held = [0, 0, 0];      // frames each control has been down (either drum)
var blocked = true;        // a DON still down from the screen before does not count

function line(row, text, note, lit) {
    row._visible = text != undefined;
    if (text == undefined) {
        return;
    }
    row.bar._visible = lit;
    row.dim.text = lit ? "" : text;
    row.lit.text = lit ? text : "";
    row.noteDim.text = lit ? "" : note;
    row.noteLit.text = lit ? note : "";
}

function refresh() {
    var count = mode == FOLDERS ? Lumen.WdFolderCount() : Lumen.WdFolderSongs(folder);
    var first = mode == FOLDERS ? 0 : -1;
    var cursor = mode == FOLDERS ? folder : song;
    self.heading.text = mode == FOLDERS ? "SONG SELECT" : Lumen.WdFolderName(folder);
    for (var i = 0; i < ROWS; i++) {
        var at = cursor + i - CENTRE;
        var row = self["row" + i];
        if (at < first || at >= count) {
            line(row, undefined, "", false);
        } else if (mode == FOLDERS) {
            var songs = Lumen.WdFolderSongs(at);
            line(row, Lumen.WdFolderName(at), songs < 0 ? "" : "" + songs, i == CENTRE);
        } else if (at < 0) {
            line(row, "< BACK", "", i == CENTRE);
        } else {
            line(row, Lumen.WdSongTitle(folder, at), "", i == CENTRE);
        }
    }
    self.courses._visible = mode == COURSE;
    if (mode == COURSE) {
        self.courses.title.text = Lumen.WdSongTitle(folder, song);
        for (var c = 0; c < 6; c++) {
            var entry = self.courses["course" + c];
            var text = c == 0 ? "< BACK" : c - 1 < offered.length
                ? COURSES[offered[c - 1]] + "  " + Lumen.WdSongStars(folder, song, offered[c - 1]) : "";
            entry.dim.text = c - 1 == course ? "" : text;
            entry.lit.text = c - 1 == course ? text : "";
        }
    }
    self.hint.text = "RIM: MOVE   DON: DECIDE";
}

function move(by) {
    if (mode == COURSE) {
        course = Math.max(-1, Math.min(offered.length - 1, course + by));
    } else if (mode == FOLDERS) {
        var folders = Lumen.WdFolderCount();
        var to = (folder + by + folders) % folders;
        if (to != folder) {
            folder = to;
            Lumen.NotifyGenreFolder(folder, -1, false);
        }
    } else {
        var to = Math.max(-1, Math.min(Lumen.WdFolderSongs(folder) - 1, song + by));
        if (to != song) {
            song = to;
            Lumen.NotifyStopBGM(folder, song);   // the game starts the song's preview on this (none on the closing line)
        }
    }
    refresh();
}

function decide() {
    if (mode == FOLDERS) {
        if (Lumen.WdFolderSongs(folder) < 0) {
            done = true;                          // a folder that is a choice by itself: the game takes over
            Lumen.SetSelectedMusic(folder, -1);
            return;
        }
        mode = SONGS;
        song = Lumen.WdFolderSongs(folder) > 0 ? 0 : -1;
        Lumen.NotifyOpenFolder(folder, song);
    } else if (mode == SONGS && song < 0) {
        mode = FOLDERS;
        Lumen.NotifyCloseFolder(folder);
        Lumen.NotifyGenreFolder(folder, -1, false);
    } else if (mode == SONGS) {
        offered = [];
        for (var c = 0; c < COURSES.length; c++) {
            if (Lumen.WdSongStars(folder, song, c) > 0) {
                offered.push(c);
            }
        }
        if (offered.length == 0) {
            return;
        }
        mode = COURSE;
        course = 0;
        Lumen.NotifyBeginCourseSelect(folder, song);
    } else if (course < 0) {
        mode = SONGS;
        Lumen.WdLeaveCourseSelect();
    } else {
        done = true;
        // ponytail: both drums play the one course picked; a cursor each when two-player play matters here.
        var slot = SLOTS[offered[course]];
        Lumen.NotifyEndCourseSelect(folder, song, joined[0] ? slot : NaN, joined[1] ? slot : NaN);
        Lumen.StopTimer();
        Lumen.SetSelectedMusic(folder, song);
        Lumen.SetSelectedCourse(0, slot, 1, slot);
        return;
    }
    refresh();
}

// Our own: the game's way to say the list is ready and where the cursor starts (a song of -1: on the folder).
ExternalInterface.addCallback("WdInit", this, function(folder_, song_) {
    folder = folder_;
    if (song_ >= 0) {
        mode = SONGS;
        song = song_;
    }
});
ExternalInterface.addCallback("SetPlayer", this, function(playerID, isJoin_, isData_, jukuLevel_) {
    joined[playerID] = isJoin_;
});

this.onEnterFrame = function() {
    if (done || Lumen == undefined) {   // no game behind it (the editor, a test): only the picture
        return;
    }
    if (!asked) {
        asked = true;
        Lumen.GetMusicData();
        Lumen.GetPlayerData();
    }
    if (!ready) {
        if (!Lumen.IsInitWait() || !Lumen.IsStart()) {
            return;
        }
        ready = true;
        Lumen.StartTimer(SECONDS);
        if (mode == SONGS) {
            Lumen.NotifyOpenFolder(folder, song);
        }
        refresh();
    }
    var pressed = [false, false, false];
    for (var k = 0; k < 3; k++) {
        var down = false;
        for (var p = 0; p < 2; p++) {
            down = down || (joined[p] && Key.isDown(KEYS[p][k]));
        }
        held[k] = down ? held[k] + 1 : 0;
        pressed[k] = held[k] == 1 || (k < 2 && held[k] > REPEAT_AFTER && (held[k] - REPEAT_AFTER) % REPEAT_EVERY == 0);
    }
    if (held[2] == 0) {
        blocked = false;
    }
    if (pressed[0]) {
        move(-1);
    } else if (pressed[1]) {
        move(1);
    } else if ((pressed[2] && !blocked) || Lumen.IsTimeup()) {
        // Out of time: one step further each frame, until a song and its first course are picked.
        if (mode == SONGS && song < 0) {
            song = 0;
        }
        if (mode == COURSE && course < 0) {
            course = 0;
        }
        decide();
    }
};

this.courses._visible = false;
for (var i = 0; i < ROWS; i++) {
    line(this["row" + i], i == CENTRE ? "SONG SELECT" : undefined, "", i == CENTRE);
}
stop();
