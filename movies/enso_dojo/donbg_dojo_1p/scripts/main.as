// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetFever", this, function(fever) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetHit", this, function(hitstate) {
});

stop();
