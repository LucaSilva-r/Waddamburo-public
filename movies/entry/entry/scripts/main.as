// The entry, bare minimum. A drum joins with its DON (player.join); a joined drum's DON opens the mode menu (rims move,
// DON decides: session.start). The engine says who is in (player.joined), the countdown (entry.timeLeft: at 0 the
// session starts as it is) and when the home player setup has the drums (entry.setup).
// ponytail: the drums are read as the game's keys (Key.isDown); a provider input event can replace that.

var DON = [90, 67];            // each drum's DON as the engine presses it (Z, C)
var KA = [[65, 83], [68, 70]]; // and its rims (A S, D F)
var MODES = ["NORMAL", "DAN DOJO"];
var MODE_NAMES = ["normal", "dan"]; // as session.start takes them

var self = this;
var joined = [false, false];
var held = [true, true];       // a DON still down from the screen before does not count
var rimHeld = [[true, true], [true, true]];
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

// The players are in: the mode menu (the DON or rim that got here must be let go first).
function chooseMode() {
    choosing = true;
    held = [true, true];
    rimHeld = [[true, true], [true, true]];
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

this.onEnterFrame = function() {
    if (done || setup) {
        return;
    }
    for (var d = 0; d < 2; d++) {
        var don = Key.isDown(DON[d]);
        var pressed = don && !held[d];
        held[d] = don;
        if (!choosing) {
            if (pressed && joined[d]) {
                chooseMode();
                return;
            }
            if (pressed) {
                Wd.request("player.join", d + 1);
            }
            continue;
        }
        if (!joined[d]) {
            continue;
        }
        for (var r = 0; r < 2; r++) {
            var rim = Key.isDown(KA[d][r]);
            if (rim && !rimHeld[d][r]) {
                mode = (mode + (r == 0 ? MODES.length - 1 : 1)) % MODES.length;
                refresh();
            }
            rimHeld[d][r] = rim;
        }
        if (pressed) {
            start(MODE_NAMES[mode]);
            return;
        }
    }
};

refresh();
stop();
