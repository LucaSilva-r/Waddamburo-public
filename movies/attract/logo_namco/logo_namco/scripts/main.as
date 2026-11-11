// The logo screen of the attract loop.
// The game moves on when told so: Command("End"), as the game's own movie does.
import flash.external.ExternalInterface;

var FRAMES = 150;
var frames = 0;
var told = false;
this.line0.text = "Waddamburo";
this.onEnterFrame = function() {
    frames++;
    if (frames >= FRAMES && !told) {
        told = true;
        ExternalInterface.call("Command", "End");
    }
};
stop();
