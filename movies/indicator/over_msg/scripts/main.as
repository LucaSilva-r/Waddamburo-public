// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("IsTween", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetBlack", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetType", this, function(index) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SkipTween", this, function() {
});

// Called as: (bool, number, number) -> undefined
ExternalInterface.addCallback("TweenVisible", this, function(flag, fadeTime_, delayTime_) {
});

stop();
