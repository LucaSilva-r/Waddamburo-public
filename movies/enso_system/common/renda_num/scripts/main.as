// The drumroll's count. Read from the provider.
var self = this;

// No value while no roll is under way.
Wd.on("play.roll", function(hits) {
    self.count.text = hits > 0 ? "" + hits : "";
});

stop();
