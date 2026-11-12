// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("ApplyForPlayerName", this, function() {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetAchieve", this, function(index, value) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetAchieveCount", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetAchieveSuccess", this, function(index, value) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetAchieveType", this, function(index, type) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetBeats", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCharForPlayerName", this, function(index, char, playerNumero) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetCombo", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetCourse", this, function(song_num, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetDaniCurrent", this, function(n) {
});

// Called as: (number, bool, number) -> undefined
ExternalInterface.addCallback("SetDaniForPlayerName", this, function(dani, is_gold_color, playerNumero) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetDaniLevel", this, function(n) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetDaniPlate", this, function(b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetFuka", this, function(song_num, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetGauge", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetGenre", this, function(song_num, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetKa", this, function(song_num, n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetKinotakeForPlayerName", this, function(kinotakeside_) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetLevelUp", this, function(b) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetMedleyCourse", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetNameSizeForPlayerName", this, function(max, playerNumero) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetPlayerEntry", this, function(join) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetPlayerForPlayerName", this, function(playerID_, playerNumero) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetPresentInfo", this, function(id, type) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetRegionMode", this, function(b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetRenda", this, function(song_num, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetResultLevel", this, function(n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetReverse", this, function(song_num, b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetRyo", this, function(song_num, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetScore", this, function(score) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetSongPlayCount", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetStar", this, function(song_num, n) {
});

// Called as: (text, number) -> undefined
ExternalInterface.addCallback("SetTitleForPlayerName", this, function(titileName_, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetTitlePanelForPlayerName", this, function(plate_id, playerNumero) {
});

stop();
