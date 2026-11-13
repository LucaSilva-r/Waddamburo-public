// A note that was hit flies from the target to the gauge. The hits are heard from the provider; each one gets a
// flight of its own (the sprite exported as "flight"), which removes itself when it lands. The game's own flight
// movie is jumped to a label per hit instead.
var self = this;
var MOST = 128; // flights in the air at once: past that the oldest gives its place
var next = 0;
var fly = function(label) {
    self.attachMovie("flight", "f" + next, next).gotoAndPlay(label);
    next = (next + 1) % MOST;
};

Wd.on("note.judged", function(result, big, kind) {
    if (result != "bad") fly(big ? kind + "_big" : kind);
});
Wd.on("roll.hit", function(part, big) {
    fly(big ? part + "_big" : part);
});
stop();
