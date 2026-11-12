// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("AIEnsoWinIconInitialize", this, function(songSectionNum) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("AIEnsoWinIconMove", this, function(isSectionWin) {
});

stop();
