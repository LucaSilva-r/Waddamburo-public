// The results, bare minimum: the numbers the game hands over, a drum's column each. A DON (after a moment) or
// fifteen seconds end them, the way the game's results end: _global.isAllEnd.
import flash.external.ExternalInterface;

var HOLD = 60;            // frames before a DON counts (the one that ended the song is still down)
var LIMIT = 15 * 60;
var KEYS = [90, 67, 65, 83, 68, 70];
var COURSES = ["EASY", "NORMAL", "HARD", "ONI", "URA"];
var ROWS = ["course", "score", "best", "ryo", "ka", "fuka", "combo", "renda"];
var NAMES = ["", "SCORE", "BEST", "GOOD", "OK", "BAD", "MAX COMBO", "DRUMROLL"];

var self = this;
var shown = [true, false];
var values = [{}, {}];
var frames = 0;

function refresh() {
    self.heading.text = "RESULTS";
    for (var p = 0; p < 2; p++) {
        var column = p == 0 ? self.p1 : self.p2;
        column._visible = shown[p];
        for (var r = 0; r < ROWS.length; r++) {
            var value = values[p][ROWS[r]];
            column["name" + r].text = r == 0 ? (p + 1) + "P" : NAMES[r];
            column["value" + r].text = value == undefined || (r == 2 && value < 0) ? "" : "" + value;
        }
    }
    self.hint.text = frames >= HOLD ? "HIT DON TO CONTINUE" : "";
}

function set(player, row, value) {
    if (player == 0 || player == 1) {
        values[player][row] = value;
        refresh();
    }
}

function finish() {
    _global.isAllEnd = true;
}

ExternalInterface.addCallback("SetPlayerStatus", this, function(left, right) {
    shown = [left, right];
    refresh();
});
ExternalInterface.addCallback("SetCourse", this, function(player, n) {
    set(player, "course", COURSES[n] == undefined ? "" : COURSES[n]);
});
ExternalInterface.addCallback("SetScore", this, function(player, score) { set(player, "score", score); });
ExternalInterface.addCallback("SetBestScore", this, function(player, score) { set(player, "best", score); });
ExternalInterface.addCallback("SetRyo", this, function(player, n) { set(player, "ryo", n); });
ExternalInterface.addCallback("SetKa", this, function(player, n) { set(player, "ka", n); });
ExternalInterface.addCallback("SetFuka", this, function(player, n) { set(player, "fuka", n); });
ExternalInterface.addCallback("SetCombo", this, function(player, n) { set(player, "combo", n); });
ExternalInterface.addCallback("SetRenda", this, function(player, n) { set(player, "renda", n); });
ExternalInterface.addCallback("SetFullCombo", this, function(player, n) {
});
// The game's way to end the results from outside.
ExternalInterface.addCallback("SetExitSignal", this, function() {
    finish();
});

this.onEnterFrame = function() {
    frames++;
    if (frames == HOLD) {
        refresh();
    }
    var don = false;
    for (var k = 0; k < KEYS.length; k++) {
        don = don || Key.isDown(KEYS[k]);
    }
    if ((don && frames >= HOLD) || frames >= LIMIT) {
        finish();
    }
};

// The rest of what the game sets: not shown yet.

// Called as: () -> undefined
ExternalInterface.addCallback("ApplyForPlayerName", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCharForPlayerName", this, function(index, char, playerNumero) {
});

// Called as: (number, bool, number) -> undefined
ExternalInterface.addCallback("SetDaniForPlayerName", this, function(dani, is_gold_color, playerNumero) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetEndMessage", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetGauge", this, function(player, n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetGotTitle", this, function(player, n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetKinotakeForPlayerName", this, function(kinotakeside_, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetNameSizeForPlayerName", this, function(max, playerNumero) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionAbekobe", this, function(player, b) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionDoron", this, function(player, b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetOptionRandomLevel", this, function(player, n) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionShinuchi", this, function(player, b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetOptionSpeed", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetPlayerForPlayerName", this, function(playerID_, playerNumero) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetRankIn", this, function(playerid, rank) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetRanking", this, function(playerid, rank, score, name) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetResultLevel", this, function(player, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetRunMode", this, function(mode) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetSongPlayCount", this, function(n) {
});

// Called as: (text, number) -> undefined
ExternalInterface.addCallback("SetTitleForPlayerName", this, function(titileName_, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetTitlePanelForPlayerName", this, function(plate_id, playerNumero) {
});
refresh();
stop();
