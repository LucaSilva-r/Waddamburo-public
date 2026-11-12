// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("Reset", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetPlayerSide", this, function(_plside) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("UpdateValue", this, function(_ryo, _ka, _fuka, _renda) {
});

stop();
