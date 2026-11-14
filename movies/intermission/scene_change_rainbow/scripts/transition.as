// Covers the screen, then uncovers the next one, as the engine says (transition.phase); with transition.title the
// song's title shows on it (the rainbow before a song). Frame 1 runs again on each showing: the listener is added once.
if (this.wired == undefined) {
    var self = this;
    self.wired = true;
    // The song's title on the card that crosses the screen (in_extra / out_extra). The clip keeps the game's name:
    // the engine stops the song's preview once it is on screen. Each crossing is a new clip: set every frame.
    self.onEnterFrame = function() {
        var title = Wd.get("song.title");
        var card = self.scene_change_song_name;
        if (card != undefined && card.text != title)
            card.text = title == undefined ? "" : title;
    };
    Wd.on("transition.phase", function(phase) {
        if (phase == undefined)
            return;
        var title = Wd.get("transition.title") == true;
        self.gotoAndPlay(phase == "cover" ? (title ? "in_extra" : "in") : (title ? "out_extra" : "out"));
    });
}
stop();
