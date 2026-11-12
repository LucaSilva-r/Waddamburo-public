// The drumroll's count. Read from the provider; the callbacks below are what the game's own counter is driven by,
// kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;

// No value while no roll is under way.
Wd.on("play.roll", function(hits) {
    self.count.text = hits > 0 ? "" + hits : "";
});

// Called as: () -> undefined
ExternalInterface.addCallback("RendaEnd", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetRendaCount", this, function(n) {
});

stop();
