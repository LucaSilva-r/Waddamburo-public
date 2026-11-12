// The banner at a song's end. The game moves this movie to a label (fullcombo, success, fail), which passes frame 0
// again: the listener is set the first time only.
var self = this;
if (!self.wired) {
    self.wired = true;
    Wd.on("song.ended", function(cleared, fullCombo) {
        self.banner.text = fullCombo ? "FULL COMBO!" : cleared ? "CLEAR!" : "FAILED";
    });
}
