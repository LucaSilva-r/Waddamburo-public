// The hit target and the lane's tints. Go-Go, the route, strokes and judgements are read from the provider.
var self = this;

Wd.on("play.goGo", function(on) {
    self.gogo._visible = on == true;
});
// No value on a chart without routes: the plain lane.
Wd.on("play.branch", function(route) {
    self.expert._visible = route == "expert";
    self.master._visible = route == "master";
});

// The target flashes the part struck; a stroke that hit a note rings (its judgement) and lights up brighter.
Wd.on("drum.hit", function(side, part) {
    self.effect.gotoAndPlay(part == "don" ? "don_s" : "katsu_s");
});
Wd.on("note.judged", function(result, big, kind, hit) {
    if (hit == false)
        return;
    var ring = result == "good" ? "ryo" : result == "ok" ? "ka" : "huka";
    self.hit_effect.gotoAndPlay((big ? "hit_dai_" : "hit_") + ring);
    if (result != "bad")
        self.effect.gotoAndPlay(kind == "ka" ? "katsu_hit" : "don_hit");
});

stop();
