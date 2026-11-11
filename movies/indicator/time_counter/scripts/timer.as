// The timer's behaviour. The host sets it up and starts it; it counts whole seconds down, one every 60 frames.
import flash.external.ExternalInterface;

var STATE_INIT = 0;       // set up or stopped: not counting
var STATE_COUNTDOWN = 1;
var STATE_PAUSE = 2;
var STATE_TIMEUP = 3;     // reached zero; stays until Start or Stop

var self = this;
var state = STATE_INIT;
var sec = 0;
var frames = 0;           // frames counted of the current second
var effect = true;        // the last ten seconds in red, with a tick on each
var shadow = false;       // dimmed, until the countdown starts
var musicNum = 1;         // the song tab: 1 to 4, anything else hides it
var musicSet = false;
var finalStage;           // undefined until the host says
var tick = 0;             // frames left of the tick animation
var fade = 0;             // +1 fading in, -1 fading out

function whole(value) {
    return value | 0;
}

function refresh() {
    var urgent = effect && sec <= 9;
    var face = self.face;
    face.white._visible = !urgent;
    face.red._visible = urgent;
    face.shade._visible = shadow;
    face.digits.dark.text = urgent ? "" : "" + sec;
    face.digits.light.text = urgent ? "" + sec : "";
    var small = sec >= 100 ? 72 : 100;
    face.digits._xscale = small;
    face.digits._yscale = small;

    var shown = musicNum >= 1 && musicNum <= 4;
    // Red for the last song of the credit; also when the host set a song number without saying which is last.
    var last = finalStage == true || (musicSet && finalStage != false);
    self.song._visible = shown;
    self.song.normal.text = shown && !last ? musicNum + "曲目" : "";
    self.song.final.text = shown && last ? musicNum + "曲目" : "";
}

function setSec(value) {
    sec = whole(value);
    if (effect && sec <= 9) {
        tick = 12;
    }
    refresh();
}

ExternalInterface.addCallback("SetMusicNum", this, function(musicNum_) {
    musicNum = whole(musicNum_);
    musicSet = true;
    refresh();
});
ExternalInterface.addCallback("SetFinalStage", this, function(final) {
    finalStage = final;
    refresh();
});
ExternalInterface.addCallback("Tween_FadeIn", this, function() {
    fade = 1;
});
ExternalInterface.addCallback("Tween_FadeOut", this, function() {
    fade = -1;
});
ExternalInterface.addCallback("SetVisible", this, function(flag) {
    self._visible = flag;
});
ExternalInterface.addCallback("SetShadow", this, function(flag) {
    shadow = flag == true;
    refresh();
});
ExternalInterface.addCallback("AddSec", this, function(time) {
    setSec(sec + whole(time));
});
ExternalInterface.addCallback("SetTime", this, function(time) {
    setSec(time);
});
ExternalInterface.addCallback("Start", this, function(time, isEffect_) {
    effect = isEffect_ == true;
    shadow = false;
    state = STATE_COUNTDOWN;
    frames = 0;
    setSec(time);
});
ExternalInterface.addCallback("Pause", this, function() {
    if (state == STATE_COUNTDOWN) {
        state = STATE_PAUSE;
    }
});
ExternalInterface.addCallback("Resume", this, function() {
    if (state == STATE_PAUSE) {
        state = STATE_COUNTDOWN;
    }
});
ExternalInterface.addCallback("IsStarted", this, function() {
    return state != STATE_INIT;
});
ExternalInterface.addCallback("IsPaused", this, function() {
    return state == STATE_PAUSE;
});
ExternalInterface.addCallback("IsTimeup", this, function() {
    return state == STATE_TIMEUP;
});
ExternalInterface.addCallback("GetSec", this, function() {
    return sec;
});
ExternalInterface.addCallback("Stop", this, function() {
    state = STATE_INIT;
});

this.onEnterFrame = function() {
    if (state == STATE_COUNTDOWN) {
        frames++;
        if (frames >= 60) {
            frames = 0;
            if (sec > 0) {
                setSec(sec - 1);
            }
            if (sec <= 0) {
                state = STATE_TIMEUP;
            }
        }
    }
    // The tick: the face jumps a little larger and settles.
    var size = 100 + (tick > 0 ? tick * 1.5 : 0);
    self.face._xscale = size;
    self.face._yscale = size;
    if (tick > 0) {
        tick--;
    }
    if (fade != 0) {
        var alpha = self._alpha + fade * 10;
        self._alpha = alpha > 100 ? 100 : alpha < 0 ? 0 : alpha;
        if (alpha >= 100 || alpha <= 0) {
            fade = 0;
        }
    }
};

refresh();
stop();
