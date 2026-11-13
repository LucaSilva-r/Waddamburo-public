// The full gauge's sign (the game's is a flame over the soul). Read from the provider. The game jumps this movie
// to "fever_start" (this frame) each time the gauge fills, so the listener is set once only.
if (!this.ready) {
    this.ready = true;
    var self = this;
    Wd.on("play.gaugeFull", function(full) {
        self.word.text = full ? "MAX" : "";
    });
}
