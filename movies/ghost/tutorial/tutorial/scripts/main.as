// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetPlayerJoin", this, function() {
});

// Called as: (bool, bool) -> undefined
ExternalInterface.addCallback("SetPlayerStatus", this, function(left, right) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Start", this, function() {
});

stop();
