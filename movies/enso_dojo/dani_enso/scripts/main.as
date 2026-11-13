// The dan course's requirements. Each is a row made when the list grows and removed when it shrinks; a row is
// written again whenever any of its four values changes.
var self = this;
var WORDS = {soul_gauge: "Soul gauge", goods: "GOOD", oks: "OK", bads: "BAD", combo: "Combo", hits: "Hits", score: "Score", rolls: "Drumroll"};
var rows = 0;

var write = function(index) {
    var row = self["row" + index];
    if (row == undefined) return;
    var type = Wd.get("dan.exam.type", index);
    var value = Wd.get("dan.exam.value", index);
    row.line.text = (WORDS[type] == undefined ? type : WORDS[type]) + "  " + (value == undefined ? 0 : value)
        + " / " + Wd.get("dan.exam.target", index) + (Wd.get("dan.exam.possible", index) == false ? "  FAILED" : "");
};

Wd.on("dan.exam.count", function(count) {
    if (count == undefined) count = 0;
    for (; rows < count; rows++) {
        var row = self.attachMovie("row", "row" + rows, rows);
        row._x = 600;
        row._y = 510 + 34 * rows;
        write(rows);
    }
    for (; rows > count; rows--) self["row" + (rows - 1)].removeMovieClip();
});
Wd.on("dan.exam.type", write);
Wd.on("dan.exam.target", write);
Wd.on("dan.exam.value", write);
Wd.on("dan.exam.possible", write);
Wd.on("dan.notesLeft", function(left) {
    self.left.text = left == undefined ? "" : left + " notes left";
});
stop();
