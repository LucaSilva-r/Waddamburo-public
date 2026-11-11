// The boot notice: shown a moment, then the game goes on to the logo.
// The game moves on when told so: _global.is_end, as the game's own movie does.
import flash.external.ExternalInterface;

var FRAMES = 150;
var frames = 0;
var told = false;
this.line0.text = "Waddamburo";
this.line1.text = "An independent engine. It ships no game data.";
this.onEnterFrame = function() {
    frames++;
    if (frames >= FRAMES && !told) {
        told = true;
        _global.is_end = true;
    }
};
stop();
