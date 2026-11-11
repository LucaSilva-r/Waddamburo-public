// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetScene", this, function(sceneNum) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetType", this, function(playerID, bits) {
});

stop();
