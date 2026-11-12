// The callbacks the game calls on this movie. Skeleton: each one still has to be written.
import flash.external.ExternalInterface;

var self = this;

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("AddCourseBits", this, function(player, course, bits) {
});

// Called as: (text, number, number, number) -> undefined
ExternalInterface.addCallback("AssignMusic", this, function(key, musicNum, extendFlag, feature_id) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("AssignMusic_GenreID", this, function(genreid, musicNum, extendFlag, feature_id) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignSession", this, function(playerID, flagBits) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignTone", this, function(playerID, flagBits) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("AssignToneID", this, function(playerID, toneID) {
});

// Called as: (number) -> number
ExternalInterface.addCallback("GetSession", this, function(playerID) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("IsEnd", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("MyScore", this, function(player, course, score, shin_score) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("MyScoreHidden", this, function(player, course, score, shin_score) {
});

// Called as: (number, number, number, text) -> undefined
ExternalInterface.addCallback("RankingScore", this, function(course_, rank_, score_, name_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ReleaseMusic", this, function(key) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ResetTween", this, function() {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("RivalNameChar", this, function(player, rival, index, char) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("RivalNameSize", this, function(player, rival, size) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("RivalScore", this, function(player, rival, course, score) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetClearBits", this, function(player, silverBits, goldenBits) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCloseBoard", this, function(isValid) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCourse", this, function(playerID, selected_, isMania_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCourseBits", this, function(player, course, bits) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetCourseStarGoldNum", this, function(playerID, course, star, num) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetCourseStarHistory", this, function(playerid, course, star) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetCourseStarMusicNum", this, function(course, star, num) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("SetCourseStarRecommend", this, function(playerid, course, star) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetCourseStarSilverNum", this, function(playerID, course, star, num) {
});

// Called as: (number, number, bool) -> undefined
ExternalInterface.addCallback("SetDefaultSession", this, function(playerID, defBits, isShinuchi) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetDefaultTone", this, function(playerID, toneID) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("SetInvalidCourse", this, function(invalidBits) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetMusicData", this, function(rank, musicBits) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetPapamamaSeparateBits", this, function(papamamaBits, separateBits) {
});

// Called as: (number, bool, number, number) -> undefined
ExternalInterface.addCallback("SetPlayer", this, function(playerID, isJoin_, playSide_, jukuLevel_) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetPlayerBits", this, function(player, bits) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetScoreCourse", this, function(playerID, bestranking_course, localranking_course) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetScoreType", this, function(playerID, myScore, otherwiseScore, localrankingScore) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetSelectedCourseStar", this, function(boardid, courseid, coursestar) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("SetSelectedMusic", this, function(genre_, music_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetSelectedSession", this, function(playerID, itemID) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetStar", this, function(easy, normal, hard, mania) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetStarHidden", this, function(easy, normal, hard, mania) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("SetSupportLevelNum", this, function(easy_num_, norm_num_, hard_num_, mania_num_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetTimeSec", this, function(type, timeSec_) {
});

// Called as: (number, bool, bool) -> undefined
ExternalInterface.addCallback("SetValidOptionMenu", this, function(playerID, is_session, is_tone) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("StopLumen", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("UpdateMusicBoard", this, function(index) {
});

stop();
