// The judgement word over the hit target, for each note hit (one that passed unhit shows none, as in the game).
// Frame 1 runs again each time a word ends (rest.as): the listener is added once.
if (this.ready == undefined) {
    var self = this;
    self.ready = true;
    Wd.on("note.judged", function(result, big, kind, hit) {
        if (hit == false)
            return;
        var word = result == "good" ? "hit_ryo" : result == "ok" ? "hit_ka" : "hit_huka";
        self.gotoAndPlay(word + (big && result != "bad" ? "_big" : ""));
    });
}
stop();
