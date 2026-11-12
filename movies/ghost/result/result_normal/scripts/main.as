// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: () -> undefined
ExternalInterface.addCallback("ApplyForPlayerName", this, function() {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetBestScore", this, function(player, score) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCharForPlayerName", this, function(index, char, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetCombo", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetCourse", this, function(player, n) {
});

// Called as: (number, bool, number) -> undefined
ExternalInterface.addCallback("SetDaniForPlayerName", this, function(dani, is_gold_color, playerNumero) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetExitSignal", this, function() {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetFuka", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetFullCombo", this, function(player, n) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetFullComboType", this, function(n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetGauge", this, function(player, n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetGotTitle", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetKa", this, function(player, n) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetKinotakeForPlayerName", this, function(kinotakeside_, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetNameSizeForPlayerName", this, function(max, playerNumero) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionAbekobe", this, function(player, b) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionDoron", this, function(player, b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetOptionRandomLevel", this, function(player, n) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetOptionShinuchi", this, function(player, b) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetOptionSpeed", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetPlayerForPlayerName", this, function(playerID_, playerNumero) {
});

// Called as: (bool, bool) -> undefined
ExternalInterface.addCallback("SetPlayerStatus", this, function(left, right) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetRankIn", this, function(playerid, rank) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetRanking", this, function(playerid, rank, score, name) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetRenda", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetResultLevel", this, function(player, n, is_win) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetRyo", this, function(player, n) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetScore", this, function(player, score) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetSongPlayCount", this, function(n) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("SetSupportFlag", this, function(support_flag_) {
});

// Called as: (text, number) -> undefined
ExternalInterface.addCallback("SetTitleForPlayerName", this, function(titileName_, playerNumero) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetTitlePanelForPlayerName", this, function(plate_id, playerNumero) {
});

stop();
