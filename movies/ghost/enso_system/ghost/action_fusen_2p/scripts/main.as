// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("GekiRendaEnd", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Reset", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetGekiRendaCount", this, function(n) {
});

stop();
