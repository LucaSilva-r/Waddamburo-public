// The Go-Go Time sign: shown for as long as Go-Go lasts. Read from the provider. The game jumps this movie to
// "splash" (this frame) at each start, so the listener is set once only.
if (!this.ready) {
    this.ready = true;
    var self = this;
    Wd.on("play.goGo", function(on) {
        self.word.text = on ? "GO-GO TIME!" : "";
    });
}
