// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number, bool, bool) -> undefined
ExternalInterface.addCallback("SetPlayerBasicData", this, function(playerID, isJoin, isData) {
});

// Bare minimum: shown a moment, then the game is told it is over (its own call for it).
var frames = 0;
this.onEnterFrame = function() {
    frames++;
    if (frames == 180 && Lumen != undefined) {
        Lumen.LumenTerminate_Gameover();
    }
};
stop();
