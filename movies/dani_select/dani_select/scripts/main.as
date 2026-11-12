// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignCondition", this, function(type, value) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("AssignDani", this, function(id) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("AssignMusic", this, function(id, genre, course, star) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("IsEnd", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetChallengeID", this, function(id) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetDailyMode", this, function(b) {
});

// Called as: (bool, number, number) -> undefined
ExternalInterface.addCallback("SetDaniStatus", this, function(valid, onpu, history) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetPlayer", this, function(playerID, selected, current) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("StopLumen", this, function() {
});

stop();
