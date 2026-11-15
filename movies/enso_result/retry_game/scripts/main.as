// The revival drum roll: the engine counts the player's hits against the norma and says how it ended
// (revival.result); this shows it, then asks to go on (revival.done) a moment after the outcome.

var OUTCOME_FRAMES = 180;

var self = this;
var shownFor = -1;

function refresh() {
    var result = Wd.get("revival.result");
    var left = Wd.get("revival.timeLeft");
    self.heading.text = result == "success" ? "REVIVED!" : result == "fail" ? "GAME OVER" : "REVIVAL CHALLENGE";
    self.count.text = Wd.get("revival.hits") + " / " + Wd.get("revival.norma");
    self.time.text = left == undefined ? "" : "" + Math.ceil(left);
    self.hint.text = result != undefined ? "" : left == undefined ? "GET READY" : "HIT THE DRUM!";
}

Wd.on("revival.hits", refresh);
Wd.on("revival.norma", refresh);
Wd.on("revival.timeLeft", refresh);
Wd.on("revival.result", function(result) {
    if (result != undefined && shownFor < 0) {
        shownFor = 0;
    }
    refresh();
});

this.onEnterFrame = function() {
    if (shownFor >= 0 && ++shownFor == OUTCOME_FRAMES) {
        Wd.request("revival.done");
    }
};

refresh();
stop();
