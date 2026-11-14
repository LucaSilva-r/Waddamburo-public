// The face follows this drum's combo, on the game's tiers (past 50, 150 and 300), and moves with the song's beat:
// level02 opens its mouth once a beat, level03 and level04 twice; level01 holds still.
// Each tick's play.beat puts the movie on its frame, so it stays stopped (and comes back here on frame 1: set up once).
if (this.tier == undefined) {
    var self = this;
    self.tier = "level01";
    self.beat = 0;
    var start = { level01: 1, level02: 11, level03: 51, level04: 71 };
    var span = { level01: 1, level02: 40, level03: 20, level04: 20 };
    var show = function() {
        var n = span[self.tier];
        var frame = start[self.tier] + (Math.floor(self.beat * 40) % n + n) % n;
        if (frame != self._currentframe) self.gotoAndStop(frame);
    };
    Wd.on("play.combo", function(combo) {
        self.tier = combo > 300 ? "level04" : combo > 150 ? "level03" : combo > 50 ? "level02" : "level01";
        show();
    });
    Wd.on("play.beat", function(beat) {
        self.beat = beat == undefined ? 0 : beat;
        show();
    });
}
stop();
