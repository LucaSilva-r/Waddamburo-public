// The notice between the title and the logo in the attract loop.
// The game moves on when told so: Command("End"), as the game's own movie does.
import flash.external.ExternalInterface;

var FRAMES = 240;
var frames = 0;
var told = false;
this.line0.text = "This is a placeholder skin.";
this.line1.text = "Songs come from your own libraries.";
this.onEnterFrame = function() {
    frames++;
    if (frames >= FRAMES && !told) {
        told = true;
        ExternalInterface.call("Command", "End");
    }
};
stop();
