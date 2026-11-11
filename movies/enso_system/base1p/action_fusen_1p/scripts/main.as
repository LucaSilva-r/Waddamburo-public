// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

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
