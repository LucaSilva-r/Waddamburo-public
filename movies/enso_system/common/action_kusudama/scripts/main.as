// The kusudama, the ball every drum bursts together. Read from the provider.
var self = this;

// No value while no kusudama is under way.
Wd.on("play.kusudama", function(left) {
    self.ball._visible = left != undefined;
    self.ball.count.text = left != undefined ? "" + left : "";
});

stop();
