// The soul gauge, read from the provider.
var self = this;
var WIDTH = 908; // the dojo's gauge runs the length of the lane
var LEFT = 332;

Wd.on("play.gauge", function(gauge) {
    self.bar._xscale = gauge == undefined ? 0 : gauge * 100 * WIDTH / 776;
});
Wd.on("play.gaugeClearLine", function(line) {
    self.mark._visible = line != undefined;
    if (line != undefined) self.mark._x = LEFT + line * WIDTH;
});
Wd.on("play.gaugeCleared", function(cleared) {
    self.bar._alpha = cleared ? 100 : 60;
});

stop();
