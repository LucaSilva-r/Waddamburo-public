// The end of the credit: thanks the players by name (player.name per drum), then asks to go back to the
// attract loop (gameover.done).

var FRAMES = 5 * 60;

var self = this;
var frames = 0;

function refresh() {
    var names = [];
    for (var drum = 1; drum <= 2; drum++) {
        if (Wd.get("player.name", drum) != undefined) {
            names.push(Wd.get("player.name", drum));
        }
    }
    self.heading.text = "THANK YOU FOR PLAYING";
    self.names.text = names.join("   ");
}

Wd.on("player.name", refresh, 1);
Wd.on("player.name", refresh, 2);

this.onEnterFrame = function() {
    if (++frames == FRAMES) {
        Wd.request("gameover.done");
    }
};

refresh();
stop();
