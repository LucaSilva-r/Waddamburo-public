// The title screen of the attract loop.
// The game moves on when told so: Command("End"), as the game's own movie does.
import flash.external.ExternalInterface;

var FRAMES = 600;
var frames = 0;
var told = false;
this.line0.text = "Waddamburo";
this.line1.text = "HIT THE DRUM TO START";
this.onEnterFrame = function() {
    frames++;
    if (frames >= FRAMES && !told) {
        told = true;
        ExternalInterface.call("Command", "End");
    }
};
stop();
