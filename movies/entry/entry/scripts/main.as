// The entry, bare minimum. A drum joins with its DON (player.join); a joined drum's DON opens the mode menu (rims move,
// DON decides: session.start). The engine says who is in (player.joined), the countdown (entry.timeLeft: at 0 the
// session starts as it is) and when the home player setup has the drums (entry.setup). Each drum's hits come as
// drum.hit, heard per drum.

var MODES = ["NORMAL", "DAN DOJO"];
var MODE_NAMES = ["normal", "dan"]; // as session.start takes them

var self = this;
var joined = [false, false];
var setup = false;
var choosing = false;          // the mode menu is up
var mode = 0;
var done = false;

function refresh() {
    self.heading.text = choosing ? "MODE" : "PLAYER ENTRY";
    for (var p = 0; p < 2; p++) {
        var panel = p == 0 ? self.p1 : self.p2;
        panel.side.text = (p + 1) + "P";
        panel.state.text = joined[p] ? "READY" : setup ? "" : "DON TO JOIN";
    }
    self.hint.text = choosing ? "<  " + MODES[mode] + "  >" : setup ? "" : joined[0] || joined[1] ? "HIT DON TO START" : "";
}

// The players are in: the mode menu.
function chooseMode() {
    choosing = true;
    refresh();
}

function start(name) {
    // A refusal (printed for whoever debugs) keeps the screen as it is.
    if (!done && Wd.request("session.start", name)) {
        done = true;
    }
}

Wd.on("player.joined", function(isIn) {
    joined[0] = isIn == true;
    refresh();
}, 1);
Wd.on("player.joined", function(isIn) {
    joined[1] = isIn == true;
    refresh();
}, 2);
// The home setup closes on who plays: straight to the mode menu.
Wd.on("entry.setup", function(open) {
    var closed = setup && open != true;
    setup = open == true;
    if (closed && (joined[0] || joined[1])) {
        chooseMode();
    }
    refresh();
});
Wd.on("entry.timeLeft", function(left) {
    if (left == 0 && (joined[0] || joined[1])) {
        start(choosing ? MODE_NAMES[mode] : "normal");
    }
});

// A hit of drum d (0 left, 1 right): DON joins, then opens the mode menu, then decides; a joined drum's rims move.
function hit(d, side, part) {
    if (done || setup) {
        return;
    }
    if (part == "don") {
        if (!choosing) {
            if (joined[d]) {
                chooseMode();
            } else {
                Wd.request("player.join", d + 1);
            }
        } else if (joined[d]) {
            start(MODE_NAMES[mode]);
        }
        return;
    }
    if (choosing && joined[d]) {
        mode = (mode + (side == "left" ? MODES.length - 1 : 1)) % MODES.length;
        refresh();
    }
}

Wd.on("drum.hit", function(side, part) {
    hit(0, side, part);
}, 1);
Wd.on("drum.hit", function(side, part) {
    hit(1, side, part);
}, 2);

refresh();
stop();
