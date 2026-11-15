// The results, bare minimum: a column per drum that played (result.*, read with the drum's number). A DON (after a
// moment) or fifteen seconds ask the engine to go on (result.continue). Any drum's hit counts (drum.hit, per drum).

var HOLD = 60;            // frames before a hit counts (a late hit of the song must not skip the results)
var LIMIT = 15 * 60;
var ROWS = ["result.course", "result.score", "result.best", "result.good", "result.ok", "result.bad", "result.maxCombo", "result.rolls"];
var NAMES = ["", "SCORE", "BEST", "GOOD", "OK", "BAD", "MAX COMBO", "DRUMROLL"];
var COURSES = { easy: "EASY", normal: "NORMAL", hard: "HARD", oni: "ONI", ura: "URA" };

var self = this;
var frames = 0;
var done = false;
var hitNow = false;

Wd.on("drum.hit", function() {
    hitNow = true;
}, 1);
Wd.on("drum.hit", function() {
    hitNow = true;
}, 2);

function outcome(drum) {
    return Wd.get("result.fullCombo", drum) == true ? "  FULL COMBO" : Wd.get("result.cleared", drum) == true ? "  CLEAR" : "";
}

function refresh() {
    self.heading.text = "RESULTS";
    for (var p = 0; p < 2; p++) {
        var column = p == 0 ? self.p1 : self.p2;
        var drum = p + 1;
        column._visible = Wd.has("result.score", drum);
        for (var r = 0; r < ROWS.length; r++) {
            var value = Wd.get(ROWS[r], drum);
            column["name" + r].text = r == 0 ? drum + "P" : NAMES[r];
            column["value" + r].text = value == undefined ? "" : r == 0 ? COURSES[value] + outcome(drum) : "" + value;
        }
    }
    self.hint.text = frames >= HOLD ? "HIT DON TO CONTINUE" : "";
}

this.onEnterFrame = function() {
    if (done) {
        return;
    }
    frames++;
    if (frames == HOLD) {
        refresh();
    }
    var don = hitNow;
    hitNow = false;
    // A refusal (printed for whoever debugs) keeps the results up.
    if (((don && frames >= HOLD) || frames >= LIMIT) && Wd.request("result.continue")) {
        done = true;
    }
};

refresh();
stop();
