// The dan dojo as library rows: the series, their courses opened in place, each course's songs (its charts) under it.
// The rims move the cursor; DON opens or closes a series or a course, and on an open course plays it (course.start).
// The selected course's rules are the dan.exam lists. Back is the engine's (Escape).
// ponytail: the drums are read as the game's keys (Key.isDown); a provider input event can replace that.

var VISIBLE = 12;
var RULES = 4;
var DON = [90, 67];
var KA = [65, 83, 68, 70];        // left rim, right rim of each drum: A S, D F
var RULE_NAMES = { soul_gauge: "Soul gauge", goods: "GOODs", oks: "OKs", bads: "BADs", combo: "Max combo",
    hits: "Hits", score: "Score", rolls: "Drumroll" };

var self = this;
var first = 0;
var held = [true, true];
var rimHeld = [true, true, true, true];

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
    if (kind == "chart" && Wd.get("row.stars", index) != undefined) {
        text += "   Lv " + Wd.get("row.stars", index);
    }
    if (Wd.get("row.playable", index) == false) {
        text += "   (missing)";
    }
    return (index == Wd.get("selected.row") ? "> " : "  ") + text;
}

function setText(field, text) {
    if (field.text != text) {
        field.text = text;
    }
}

function draw() {
    setText(self.heading, "DAN DOJO");
    for (var i = 0; i < VISIBLE; i++) {
        setText(self["row" + i], rowText(first + i));
    }
    var course = Wd.get("selected.kind") == "course";
    setText(self.course, course ? Wd.get("selected.title") : "");
    var rules = course ? Wd.get("dan.exam.count") : 0;
    for (var r = 0; r < RULES; r++) {
        var type = r < rules ? Wd.get("dan.exam.type", r) : undefined;
        var under = type == "oks" || type == "bads";
        setText(self["rule" + r], type == undefined ? ""
            : RULE_NAMES[type] + (under ? " under " : " ") + Wd.get("dan.exam.target", r));
    }
    setText(self.hint, "RIM: MOVE    DON: OPEN / PLAY");
}

// Keeps the cursor in the rows shown, asking for the window when it moves past an edge.
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
    if (kind == "course" && Wd.get("row.opened", row) == true) {
        Wd.request("course.start");
    } else if (Wd.get("row.opened", row) == true) {
        Wd.request("row.close", row);
    } else if (kind != "chart") {
        Wd.request("row.open", row);
    }
}

this.onEnterFrame = function() {
    if (Wd.get("rows.count") != undefined && Wd.get("row.kind", first) == undefined) {
        Wd.request("rows.window", first, VISIBLE);
    }
    for (var k = 0; k < KA.length; k++) {
        var rim = Key.isDown(KA[k]);
        if (rim && !rimHeld[k]) {
            show(Wd.get("selected.row") + (k % 2 == 0 ? -1 : 1));
        }
        rimHeld[k] = rim;
    }
    for (var d = 0; d < 2; d++) {
        var don = Key.isDown(DON[d]);
        if (don && !held[d]) {
            decide();
        }
        held[d] = don;
    }
    draw();
};

draw();
stop();
