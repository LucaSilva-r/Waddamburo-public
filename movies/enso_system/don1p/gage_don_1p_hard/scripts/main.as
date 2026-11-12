// The soul gauge. Read from the provider; SetCurrentGauge is what the game's own gauge is driven by, kept empty
// so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;
var WIDTH = 776;

Wd.on("play.gauge", function(gauge) {
    self.bar._xscale = gauge * 100;
});
Wd.on("play.gaugeClearLine", function(line) {
    self.mark._x = 252 + line * WIDTH;
});
Wd.on("play.gaugeCleared", function(cleared) {
    self.bar._alpha = cleared ? 100 : 60;
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCurrentGauge", this, function(current_gage) {
});

stop();
