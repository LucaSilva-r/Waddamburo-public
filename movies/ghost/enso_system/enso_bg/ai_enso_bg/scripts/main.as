// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (number) -> undefined
ExternalInterface.addCallback("AIEnsoBGInitialize", this, function(songSectionNum) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ChangeAIEmotion", this, function(aiEmotion) {
});

// Called as: () -> undefined
ExternalInterface.addCallback("OnSectionClosing", this, function() {
});

// Called as: () -> undefined
ExternalInterface.addCallback("OnSectionMiddle", this, function() {
});

// Called as: () -> undefined
ExternalInterface.addCallback("StartReadyAction", this, function() {
});

// Called as: () -> undefined
ExternalInterface.addCallback("StartSectionLose", this, function() {
});

// Called as: () -> undefined
ExternalInterface.addCallback("StartSectionWin", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("UpdateTargetGaugeNum", this, function(targetGaugeNum_) {
});

stop();
