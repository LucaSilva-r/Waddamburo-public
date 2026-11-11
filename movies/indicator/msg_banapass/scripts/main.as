// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetBncoin", this, function(bncointype) {
});

// Called as: (bool, number) -> undefined
ExternalInterface.addCallback("SetBurst", this, function(isBurst, bncoincost) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCamera", this, function(isCameraOn) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetOnline", this, function(isOnline) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetScene", this, function(sceneNum) {
});

stop();
