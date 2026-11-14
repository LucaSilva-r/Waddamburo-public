// The face follows this drum's combo, on the game's tiers (past 50, 150 and 300); each face loops by itself.
// Frame 1 runs again each time level01 loops round: the listener is added once.
if (this.tier == undefined) {
    var self = this;
    self.tier = "level01";
    Wd.on("play.combo", function(combo) {
        var next = combo > 300 ? "level04" : combo > 150 ? "level03" : combo > 50 ? "level02" : "level01";
        if (next != self.tier) {
            self.tier = next;
            self.gotoAndPlay(next);
        }
    });
}
