// The script of the sample's first frame: ActionScript 2, compiled when the package loads.
// A callback is what the game (the host) calls on a movie. Try it in the viewer:
//   --package=samples/movie-package/bounce "--invoke=SetGlow|b:false@120"
import flash.external.ExternalInterface;

var self = this;
var turns = 0;

ExternalInterface.addCallback("SetGlow", this, function(shown: Boolean): Void {
    self.glow._visible = shown;
});
ExternalInterface.addCallback("GetTurns", this, function(): Number {
    return turns;
});

// Runs every frame, on top of the timeline's own motion.
this.onEnterFrame = function() {
    turns++;
    self.counter.text = "frame " + turns;
};
