// The board. Score and combo are read from the provider; the callbacks below are what the game's own board is
// driven by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;

Wd.on("play.score", function(score) {
    self.score.text = score == undefined ? "" : "" + score;
});
Wd.on("play.combo", function(combo) {
    self.combo.text = combo >= 10 ? "" + combo : "";
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

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetScore", this, function(n) {
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
