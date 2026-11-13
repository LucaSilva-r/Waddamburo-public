// The board. Score and combo are read from the provider.
var self = this;

Wd.on("play.score", function(score) {
    self.score.text = score == undefined ? "" : "" + score;
});
// The drum lights up where it was struck: side is left or right, part is don (the face) or ka (the rim).
Wd.on("drum.hit", function(side, part) {
    // Frames count from 1 in a script: 2 is the first lit frame (1 is the rest, which stops).
    self[part + "_" + side].gotoAndPlay(2);
});
Wd.on("play.combo", function(combo) {
    // (undefined >= 10 is true in ActionScript 2: asked for outright.)
    self.combo.text = combo != undefined && combo >= 10 ? "" + combo : "";
});

stop();
