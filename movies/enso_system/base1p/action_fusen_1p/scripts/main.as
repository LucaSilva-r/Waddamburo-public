// The balloon. Read from the provider; the callbacks below are what the game's own balloon is driven by, kept
// empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;

// No value while no balloon is under way.
Wd.on("play.balloon", function(left) {
    self.ball._visible = left != undefined;
    self.ball.count.text = left != undefined ? "" + left : "";
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("GekiRendaEnd", this, function(n) {
});

// Called as: () -> undefined
ExternalInterface.addCallback("Reset", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetGekiRendaCount", this, function(n) {
});

stop();
