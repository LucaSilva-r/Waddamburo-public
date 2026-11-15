// A dan course's results: its outcome (dan.passed), the course's totals (result.* under the player's drum),
// each requirement where the course ended (dan.exam.*) and each song's row (dan.song*). A DON after a moment, or
// fifteen seconds, ask to go on (result.continue).

var HOLD = 60;
var LIMIT = 15 * 60;
var TOTALS = ["result.score", "result.good", "result.ok", "result.bad", "result.maxCombo", "result.rolls"];
var NAMES = ["SCORE", "GOOD", "OK", "BAD", "MAX COMBO", "DRUMROLL"];
var RULES = { soul_gauge: "Soul gauge", goods: "GOODs", oks: "OKs", bads: "BADs", combo: "Max combo", hits: "Hits",
    score: "Score", rolls: "Drumroll" };
var OUTCOMES = { gold: "GOLD PASS", red: "PASS", failed: "FAILED" };

var self = this;
var frames = 0;
var done = false;
var hitNow = false;

// The player's drum: the one whose totals were told.
function drum() {
    return Wd.has("result.score", 2) && !Wd.has("result.score", 1) ? 2 : 1;
}

function refresh() {
    var d = drum();
    self.heading.text = Wd.get("dan.name") == undefined ? "" : Wd.get("dan.name");
    self.outcome.text = OUTCOMES[Wd.get("dan.passed")] == undefined ? "" : OUTCOMES[Wd.get("dan.passed")];
    for (var i = 0; i < TOTALS.length; i++) {
        var value = Wd.get(TOTALS[i], d);
        self["tname" + i].text = NAMES[i];
        self["tvalue" + i].text = value == undefined ? "" : "" + value;
    }
    var count = Wd.get("dan.exam.count");
    for (var e = 0; e < 4; e++) {
        var type = Wd.get("dan.exam.type", e);
        self["exam" + e].text = count == undefined || e >= count || type == undefined ? ""
            : RULES[type] + "   " + Wd.get("dan.exam.value", e) + " / " + Wd.get("dan.exam.target", e);
    }
    for (var s = 0; s < 3; s++) {
        var title = Wd.get("dan.songTitle", s);
        var good = Wd.get("dan.songGood", s);
        self["song" + s].text = title == undefined ? "" : (s + 1) + ".  " + title
            + (good == undefined ? "   (not played)" : "   GOOD " + good + "  OK " + Wd.get("dan.songOk", s) + "  BAD " + Wd.get("dan.songBad", s));
    }
    self.hint.text = frames >= HOLD ? "HIT DON TO CONTINUE" : "";
}

Wd.on("drum.hit", function(side, part) {
    hitNow = hitNow || part == "don";
}, 1);
Wd.on("drum.hit", function(side, part) {
    hitNow = hitNow || part == "don";
}, 2);

this.onEnterFrame = function() {
    if (done) {
        return;
    }
    frames++;
    if (frames == 1 || frames == HOLD) {
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
