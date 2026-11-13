// A note that was hit flies from the target to the gauge. The hits are heard from the provider; eight notes can be in
// the air at once (f0 to f7, taken in turn). The game's own flight movie is jumped to a label per hit instead.
var self = this;
var next = 0;
var fly = function(label) {
    self["f" + next].gotoAndPlay(label);
    next = (next + 1) % 8;
};

Wd.on("note.judged", function(result, big, kind) {
    if (result != "bad") fly(big ? kind + "_big" : kind);
});
Wd.on("roll.hit", function(part, big) {
    fly(big ? part + "_big" : part);
});
stop();
