// The hit target and the lane's tints. Go-Go and the route are read from the provider; the callbacks below are what
// the game's own lane is driven by, kept empty so this movie can stand where that one does.
import flash.external.ExternalInterface;

var self = this;

Wd.on("play.goGo", function(on) {
    self.gogo._visible = on == true;
});
// No value on a chart without routes: the plain lane.
Wd.on("play.branch", function(route) {
    self.expert._visible = route == "expert";
    self.master._visible = route == "master";
});

ExternalInterface.addCallback("SetBranch", this, function(branchtype) {
});
ExternalInterface.addCallback("SetBranchAvailable", this, function(available) {
});
ExternalInterface.addCallback("SetBranchImmediate", this, function(branchtype) {
});
ExternalInterface.addCallback("SetGogo", this, function(is_gogo) {
});
ExternalInterface.addCallback("SetGogoImmediate", this, function(is_gogo) {
});
stop();
