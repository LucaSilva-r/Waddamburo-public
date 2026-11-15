// The credit line, and per drum its player's name (entry, song select) or a prompt to join (the title, the demo,
// the entry, song select). Everything comes from the provider; nothing is driven by name.

// The screens where a drum without a player is asked to join.
var PROMPT = { title: true, demo: true, entry: true, select: true };
var NAMES = { entry: true, select: true };

var self = this;

function refresh() {
    var screen = Wd.get("screen.name");
    var free = Wd.get("credits.freePlay") == true;
    var line = "";
    if (Wd.get("credits.shown") != false && screen != undefined) {
        line = free ? "FREE PLAY" : (Wd.get("credits.service") == true ? "SERVICE  " : "CREDIT  ") + Wd.get("credits.count");
    }
    self.credits.text = line;
    var canJoin = free || Wd.get("credits.toJoin") == 0;
    for (var drum = 1; drum <= 2; drum++) {
        var field = drum == 1 ? self.left : self.right;
        var text = "";
        if (Wd.get("player.joined", drum) == true) {
            if (NAMES[screen] == true && Wd.get("player.name", drum) != undefined) {
                text = drum + "P  " + Wd.get("player.name", drum);
            }
        } else if (PROMPT[screen] == true) {
            text = canJoin ? "HIT THE DRUM" : "INSERT COIN";
        }
        field.text = text;
    }
}

var watched = ["screen.name", "credits.freePlay", "credits.count", "credits.service", "credits.toJoin", "credits.shown"];
for (var i = 0; i < watched.length; i++) {
    Wd.on(watched[i], refresh);
}
for (var drum = 1; drum <= 2; drum++) {
    Wd.on("player.joined", refresh, drum);
    Wd.on("player.name", refresh, drum);
}

refresh();
stop();
