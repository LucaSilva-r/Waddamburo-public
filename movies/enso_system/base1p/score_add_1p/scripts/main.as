// The points just scored, rising and gone, heard from the provider.
var self = this;
var TOP = 195; // where the popup starts: just above the board's score
var left = 0;  // frames the popup still shows

Wd.on("score.added", function(points) {
    self.word.text = "+" + points;
    self.word._y = TOP;
    left = 40;
});
this.onEnterFrame = function() {
    if (left > 0) {
        self.word._y -= 0.5;
        if (--left == 0) self.word.text = "";
    }
};

stop();
