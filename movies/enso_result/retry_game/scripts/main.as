// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetHandsColor", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetNorma", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetPlayerStatus", this, function() {
});

// Bare minimum: no revival challenge yet. It ends by itself (the game reads _global.isAllEnd), not succeeded.
var frames = 0;
this.onEnterFrame = function() {
    frames++;
    if (frames == 120) {
        _global.isAllEnd = true;
    }
};
stop();
