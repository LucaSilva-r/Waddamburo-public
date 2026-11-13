// The sign at every hundredth combo. Read from the provider; SetComboBonus is what the game's own sign is driven
// by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;
var left = 0; // frames the sign still shows

Wd.on("combo.milestone", function(combo) {
    self.word.text = combo + " COMBO!";
    left = 90;
});
this.onEnterFrame = function() {
    if (left > 0 && --left == 0) self.word.text = "";
};

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetComboBonus", this, function() {
});

stop();
