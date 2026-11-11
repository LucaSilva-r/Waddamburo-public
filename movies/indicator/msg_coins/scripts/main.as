// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCoinNum", this, function(num) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCoinType", this, function(type) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetMsgNum", this, function(player, num) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetScene", this, function(scene_) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetVisibieMsg", this, function(flag) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetVisibleCoinType", this, function(flag) {
});

// Called as: (bool, bool) -> undefined
ExternalInterface.addCallback("SetVisibleSplitPanel", this, function(disp1p, disp2p) {
});

stop();
