// Covers the screen, then uncovers the next one, as the engine says (transition.phase); with transition.title the
// song's title shows on it (the rainbow before a song). Frame 1 runs again on each showing: the listener is added once.
if (this.wired == undefined) {
    var self = this;
    self.wired = true;
    Wd.on("transition.phase", function(phase) {
        if (phase == undefined)
            return;
        var title = Wd.get("transition.title") == true;
        self.gotoAndPlay(phase == "cover" ? (title ? "in_extra" : "in") : (title ? "out_extra" : "out"));
    });
}
stop();
