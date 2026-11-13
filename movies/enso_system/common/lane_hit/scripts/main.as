// The hit target and the lane's tints. Go-Go and the route are read from the provider.
var self = this;

Wd.on("play.goGo", function(on) {
    self.gogo._visible = on == true;
});
// No value on a chart without routes: the plain lane.
Wd.on("play.branch", function(route) {
    self.expert._visible = route == "expert";
    self.master._visible = route == "master";
});

stop();
