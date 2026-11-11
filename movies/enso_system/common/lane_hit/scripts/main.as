// The callbacks the game calls on the hit target. Go-Go and the branch lanes are not drawn yet.
import flash.external.ExternalInterface;

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
