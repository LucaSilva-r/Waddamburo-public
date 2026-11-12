// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetChallengeLevel", this, function(n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetConditionNum", this, function(n) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetConditionType", this, function(index, type, normanum) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetConditionValid", this, function(index, valid) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetConditionValue", this, function(index, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetLeftNotes", this, function(n) {
});

stop();
