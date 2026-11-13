// The dan course's panel: its name, the notes left, and a row per requirement. A row is made when the list grows
// and removed when it shrinks, and written again whenever one of its values changes.
var self = this;
var WORDS = {soul_gauge: "Soul gauge", goods: "GOOD", oks: "OK", bads: "BAD", combo: "Combo", hits: "Hits", score: "Score", rolls: "Drumroll"};
var rows = 0;

var write = function(index) {
    var row = self["row" + index];
    if (row == undefined) return;
    var type = Wd.get("dan.exam.type", index);
    var target = Wd.get("dan.exam.target", index);
    var value = Wd.get("dan.exam.value", index);
    if (value == undefined) value = 0;
    // OK and BAD must stay under their target: their bar is what is left. The others fill towards it.
    var under = type == "oks" || type == "bads";
    var part = target > 0 ? (under ? target - value : value) / target : 0;
    row.fill._xscale = Math.max(0, Math.min(1, part)) * 100;
    // The number beside an "under" bar is what is left too, as on the game's panel.
    row.value.text = (under ? Math.max(0, target - value) : value) + (type == "soul_gauge" ? "%" : "");
    row.label.text = (WORDS[type] == undefined ? type : WORDS[type]) + (under ? " under " : " ") + target
        + (type == "soul_gauge" ? "%" : "") + (under ? "" : " or more")
        + (Wd.get("dan.exam.possible", index) == false ? "  FAILED" : "");
};

Wd.on("dan.exam.count", function(count) {
    if (count == undefined) count = 0;
    for (; rows < count; rows++) {
        var row = self.attachMovie("row", "row" + rows, rows);
        row._x = 305;
        row._y = 378 + 98 * rows;
        write(rows);
    }
    for (; rows > count; rows--) self["row" + (rows - 1)].removeMovieClip();
});
Wd.on("dan.exam.type", write);
Wd.on("dan.exam.target", write);
Wd.on("dan.exam.value", write);
Wd.on("dan.exam.possible", write);
Wd.on("dan.name", function(name) {
    self.name.text = name == undefined ? "" : name;
});
Wd.on("dan.notesLeft", function(left) {
    self.caption.text = left == undefined ? "" : "Notes left";
    self.left.text = left == undefined ? "" : "" + left;
});
stop();
