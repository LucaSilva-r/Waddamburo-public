// The points just scored, rising and gone. Worked out from the score the provider gives; Create and SetCount are
// what the game's own popup is driven by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;
var TOP = 195; // where the popup starts: just above the board's score
var last;     // the score before this change
var left = 0; // frames the popup still shows

Wd.on("play.score", function(score) {
    if (score != undefined && last != undefined && score > last) {
        self.word.text = "+" + (score - last);
        self.word._y = TOP;
        left = 40;
    }
    last = score;
});
this.onEnterFrame = function() {
    if (left > 0) {
        self.word._y -= 0.5;
        if (--left == 0) self.word.text = "";
    }
};

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Create", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCount", this, function() {
});

stop();
