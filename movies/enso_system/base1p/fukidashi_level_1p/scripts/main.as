// Says which way the route went at a branch. Read from the provider; the game jumps its own bubble to level_up and
// level_down, labels this movie keeps and does nothing at.
var self = this;
var rank = {normal: 0, expert: 1, master: 2};
var last;     // the route before this change
var left = 0; // frames the words still show

Wd.on("play.branch", function(route) {
    if (route != undefined && last != undefined && route != last) {
        self.word.text = rank[route] > rank[last] ? "LEVEL UP!" : "LEVEL DOWN";
        left = 90;
    }
    last = route;
});
this.onEnterFrame = function() {
    if (left > 0 && --left == 0) self.word.text = "";
};
