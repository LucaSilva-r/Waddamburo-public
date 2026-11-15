// A name board: its own drum's player (the engine tells each board its drum's player.name and player.joined; in a
// song the lane does). Hidden while the drum has no player.

var self = this;
var name;
var joined;

function refresh() {
    var shown = name != undefined && joined != false;
    self.board._visible = shown;
    self.label.text = shown ? name : "";
}

Wd.on("player.name", function(value) {
    name = value;
    refresh();
});
Wd.on("player.joined", function(value) {
    joined = value;
    refresh();
});

refresh();
stop();
