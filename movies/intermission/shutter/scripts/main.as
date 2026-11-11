// The shutter's two calls. Close's argument is whose colour the doors take (0 left drum, 1 right, 2 both).
// ponytail: one colour for all; a door per colour when the look is drawn.
import flash.external.ExternalInterface;

var self = this;
ExternalInterface.addCallback("Close", this, function(c) {
    self.gotoAndPlay("close");
});
ExternalInterface.addCallback("Open", this, function() {
    self.gotoAndPlay("open");
});
stop();
