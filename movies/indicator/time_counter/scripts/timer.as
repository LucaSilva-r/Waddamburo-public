// The timer: the screen's countdown (timer.left, whole seconds; none: no timer runs, and it hides) and, on song
// select, the song tab (song.stage 1 to 4, red for the credit's last song: song.finalStage). The engine counts; this
// draws. The last ten seconds turn red, with a small jump on each.

var self = this;
var sec;                  // undefined while no timer runs
var stage;
var finalStage = false;
var shown = true;
var tick = 0;             // frames left of the tick animation

function refresh() {
    var urgent = sec != undefined && sec <= 9;
    var face = self.face;
    face._visible = sec != undefined;
    face.white._visible = !urgent;
    face.red._visible = urgent;
    face.shade._visible = false;
    face.digits.dark.text = sec == undefined || urgent ? "" : "" + sec;
    face.digits.light.text = urgent ? "" + sec : "";
    var small = sec != undefined && sec >= 100 ? 72 : 100;
    face.digits._xscale = small;
    face.digits._yscale = small;

    var tab = stage != undefined && stage >= 1 && stage <= 4;
    self.song._visible = tab;
    self.song.normal.text = tab && !finalStage ? stage + "曲目" : "";
    self.song.final.text = tab && finalStage ? stage + "曲目" : "";
    self._visible = shown;
}

Wd.on("timer.left", function(left) {
    var before = sec;
    sec = left == undefined ? undefined : Math.ceil(left);
    if (sec != before && sec != undefined && sec <= 9) {
        tick = 12;
    }
    refresh();
});
Wd.on("timer.shown", function(flag) {
    shown = flag != false;
    refresh();
});
Wd.on("song.stage", function(number) {
    stage = number;
    refresh();
});
Wd.on("song.finalStage", function(flag) {
    finalStage = flag == true;
    refresh();
});

this.onEnterFrame = function() {
    // The tick: the face jumps a little larger and settles.
    var size = 100 + (tick > 0 ? tick * 1.5 : 0);
    self.face._xscale = size;
    self.face._yscale = size;
    if (tick > 0) {
        tick--;
    }
};

refresh();
stop();
