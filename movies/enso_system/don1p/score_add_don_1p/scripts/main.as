// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("Create", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCount", this, function(n) {
});

stop();
