// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ChangeNextSection", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetProgress", this, function(prg) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetSectionType", this, function(type) {
});

stop();
