// Song select, bare minimum: the library as rows. Folders open in place to their songs, a song to its charts. The rims
// move the cursor (held: they repeat); DON opens or closes a folder or a song, and on a chart plays it (song.start).
// A folder that is a choice by itself (another library, the dojo, the tutorial) takes over when opened.
// ponytail: the drums are read as the game's keys (Key.isDown); a provider input event can replace that.

var VISIBLE = 12;
var DON = [90, 67];
var KA = [65, 83, 68, 70];        // left rim, right rim of each drum: A S, D F
var REPEAT_AFTER = 20;            // frames a rim is held before it repeats
var REPEAT_EVERY = 4;

var self = this;
var first = 0;
var held = [true, true];          // a DON still down from the screen before does not count
var rimHeld = [0, 0, 0, 0];       // frames each rim has been down
var started = false;

function rowText(index) {
    var kind = Wd.get("row.kind", index);
    if (kind == undefined) {
        return "";
    }
    var indent = "";
    for (var d = 0; d < Wd.get("row.depth", index); d++) {
        indent += "    ";
    }
    var mark = kind == "chart" ? "  " : Wd.get("row.opened", index) == true ? "- " : "+ ";
    var text = indent + mark + Wd.get("row.title", index);
    if (kind == "chart") {
        text += "   Lv " + Wd.get("row.stars", index);
    } else if (kind == "folder" && Wd.get("row.count", index) != undefined) {
        text += "   (" + Wd.get("row.count", index) + ")";
    }
    return (index == Wd.get("selected.row") ? "> " : "  ") + text;
}

function setText(field, text) {
    if (field.text != text) {
        field.text = text;
    }
}

function draw() {
    setText(self.heading, "SONG SELECT");
    for (var i = 0; i < VISIBLE; i++) {
        setText(self["row" + i], rowText(first + i));
    }
    var row = Wd.get("selected.row");
    var title = Wd.get("selected.title");
    setText(self.title, title == undefined ? "" : title);
    var subtitle = row == undefined ? undefined : Wd.get("row.subtitle", row);
    setText(self.detail, subtitle == undefined ? "" : subtitle);
    var left = Wd.get("select.timeLeft");
    setText(self.hint, "RIM: MOVE    DON: OPEN / PLAY" + (left == undefined ? "" : "    " + left));
}

// Moves the cursor, asking for the window when it passes an edge.
function show(row) {
    var count = Wd.get("rows.count");
    if (count == undefined || count == 0) {
        return;
    }
    row = Math.max(0, Math.min(count - 1, row));
    Wd.request("select.row", row);
    if (row < first) {
        first = row;
    } else if (row >= first + VISIBLE) {
        first = row - VISIBLE + 1;
    }
    Wd.request("rows.window", first, VISIBLE);
}

function decide() {
    var row = Wd.get("selected.row");
    var kind = Wd.get("selected.kind");
    if (kind == "chart") {
        started = Wd.request("song.start");
    } else if (Wd.get("row.opened", row) == true) {
        Wd.request("row.close", row);
    } else {
        Wd.request("row.open", row);
    }
}

// Out of time: one step a frame towards a chart (open what is closed, go down into it), then play it.
function pickForThePlayer() {
    var row = Wd.get("selected.row");
    var kind = Wd.get("selected.kind");
    if (kind == "chart") {
        decide();
    } else if (Wd.get("row.opened", row) == true) {
        show(row + 1);
    } else {
        Wd.request("row.open", row);
    }
}

this.onEnterFrame = function() {
    if (started) {
        return;
    }
    var row = Wd.get("selected.row");
    if (row != undefined && (row < first || row >= first + VISIBLE)) {
        first = Math.max(0, row - Math.floor(VISIBLE / 2)); // where the engine put the cursor (back from a song)
    }
    if (Wd.get("rows.count") != undefined && Wd.get("row.kind", first) == undefined) {
        Wd.request("rows.window", first, VISIBLE);
    }
    for (var k = 0; k < KA.length; k++) {
        rimHeld[k] = Key.isDown(KA[k]) ? rimHeld[k] + 1 : 0;
        var held_ = rimHeld[k];
        if (held_ == 1 || (held_ > REPEAT_AFTER && (held_ - REPEAT_AFTER) % REPEAT_EVERY == 0)) {
            show(Wd.get("selected.row") + (k % 2 == 0 ? -1 : 1));
        }
    }
    for (var d = 0; d < 2; d++) {
        var don = Key.isDown(DON[d]);
        if (don && !held[d]) {
            decide();
        }
        held[d] = don;
    }
    if (Wd.get("select.timeLeft") == 0) {
        pickForThePlayer();
    }
    draw();
};

draw();
stop();
