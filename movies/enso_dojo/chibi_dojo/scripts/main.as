// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("Create", this, function() {
});

// Called as: () -> undefined
ExternalInterface.addCallback("CreateTamashii", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("setInstanceLimit", this, function(num) {
});

stop();
