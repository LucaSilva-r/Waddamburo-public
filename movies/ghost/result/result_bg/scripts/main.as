// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetWinTotalFlag", this, function(is_win_total) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Start", this, function() {
});

stop();
