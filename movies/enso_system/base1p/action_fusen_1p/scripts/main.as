// The balloon. Read from the provider.
var self = this;

// No value while no balloon is under way.
Wd.on("play.balloon", function(left) {
    self.ball._visible = left != undefined;
    self.ball.count.text = left != undefined ? "" + left : "";
});

stop();
