// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("Apply", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ChangeKinotake", this, function(kinotakeside) {
});

// Called as: () -> undefined
ExternalInterface.addCallback("Fadein", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("Fadeout", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("IsVisible", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetChar", this, function(index, char) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetCover", this, function(flag) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetDani", this, function(dani, is_kin_color) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetKinotake", this, function(kinotakeside) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetNameSize", this, function(max) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetPlayer", this, function(playerID_) {
});

// Called as: (text) -> undefined
ExternalInterface.addCallback("SetTitleName", this, function(titlename) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetTitlePanelID", this, function(titlepanel_id) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetVisible", this, function(flag) {
});

stop();
