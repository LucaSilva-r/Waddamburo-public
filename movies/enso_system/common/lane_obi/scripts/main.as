// The board. Score and combo are read from the provider; the callbacks below are what the game's own board is
// driven by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

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

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetAbekobe", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetComboCount", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetComboCountDrawMin", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCount", this, function(n) {
});

// Called as: (text) -> undefined
ExternalInterface.addCallback("SetCourse", this, function(course_name) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetDani", this, function() {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetDoron", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetEntryType", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetPlaySide", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetRandomLevel", this, function(n) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetSeriousMode", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetSpeed", this, function(n) {
});

// Called as: (text) -> undefined
ExternalInterface.addCallback("SetTaikoHit", this, function(ins_name) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetWaiwai", this, function(is_waiwai) {
});

stop();
