// The kusudama, the ball every drum bursts together. Read from the provider; the callbacks below are what the game's
// own kusudama is driven by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;

// No value while no kusudama is under way.
Wd.on("play.kusudama", function(left) {
    self.ball._visible = left != undefined;
    self.ball.count.text = left != undefined ? "" + left : "";
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("AddNorma", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("EndResult", this, function(res) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Norma", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Reset", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCount", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetGogoTime", this, function(isgogo) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetPlayerNum", this, function(n) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetWaiWai", this, function(iswaiwai) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("StartNorma", this, function(n) {
});

stop();
