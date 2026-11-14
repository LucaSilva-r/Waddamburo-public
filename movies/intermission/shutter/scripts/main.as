// The shutter closes over the play as the engine says (transition.phase "cover"); the next screen replaces it.
// transition.side is whose colour the doors take (0 left drum, 1 right, 2 both).
// ponytail: one colour for all; a door per colour when the look is drawn.
var self = this;

Wd.on("transition.phase", function(phase) {
    if (phase == "cover")
        self.gotoAndPlay("close");
    else if (phase == "reveal")
        self.gotoAndPlay("open");
});

stop();
