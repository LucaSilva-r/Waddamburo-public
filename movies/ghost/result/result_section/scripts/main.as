// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: (bool, bool) -> undefined
ExternalInterface.addCallback("SetBonusNumberFlag", this, function(is_first_play_song_, is_challenge_song_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetEventNextRewardNumber", this, function(event_next_reward_number_, is_event_reward_limit_flag_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetEventWinNumber", this, function(event_win_num_before_, event_win_num_after_) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetFukaNoteNumber", this, function(section_num_, fuka_num_before_best_, fuka_num_after_) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetKaNoteNumber", this, function(section_num_, ka_num_before_best_, ka_num_after_) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetNextRankNumber", this, function(next_rank_number_) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetNextRewardNumber", this, function(next_reward_number_, is_reward_limit_flag_) {
});

// Called as: (bool, bool) -> undefined
ExternalInterface.addCallback("SetPlayerJoin", this, function(isJoinLeft, isJoinRight) {
});

// Called as: (number, number, bool) -> undefined
ExternalInterface.addCallback("SetRankNumber", this, function(rank_before_, rank_after_, rank_limit_flag_) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetRendaNoteNumber", this, function(section_num_, renda_num_before_best_, renda_num_after_) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetRyoNoteNumber", this, function(section_num_, ryo_num_before_best_, ryo_num_after_) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetWinNumber", this, function(win_num_before_, win_get_num_) {
});

// Called as: (number, bool) -> undefined
ExternalInterface.addCallback("SetWinOrLoseSection", this, function(section_num_, is_win_section_) {
});

// Called as: (number, number, bool) -> undefined
ExternalInterface.addCallback("SetWinTokenNumber", this, function(win_token_before_, win_get_token_, is_fulfilled_song_) {
});

// Called as: () -> undefined
ExternalInterface.addCallback("Start", this, function() {
});

stop();
