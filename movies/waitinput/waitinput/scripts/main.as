// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("IsEnd", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Reset", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Start", this, function(player_left, player_right, scene_id) {
});

stop();
