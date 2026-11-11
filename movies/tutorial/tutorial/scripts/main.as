// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetPlayerStatus", this, function() {
});

// Bare minimum: the rules in four lines; a DON after a moment (or half a minute) ends it, as the game's tutorial
// ends: _global.isAllEnd.
var KEYS = [90, 67];
var frames = 0;
this.line0.text = "HOW TO PLAY";
this.line1.text = "RED NOTE: HIT THE DRUM'S FACE (DON)";
this.line2.text = "BLUE NOTE: HIT THE DRUM'S RIM (KA)";
this.line3.text = "HIT DON TO GO BACK";
this.onEnterFrame = function() {
    frames++;
    if ((frames > 60 && (Key.isDown(KEYS[0]) || Key.isDown(KEYS[1]))) || frames > 1800) {
        _global.isAllEnd = true;
    }
};
stop();
