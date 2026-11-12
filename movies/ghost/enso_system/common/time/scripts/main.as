// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("ChangeNextSection", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetProgress", this, function(prg) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetSectionType", this, function(type) {
});

stop();
