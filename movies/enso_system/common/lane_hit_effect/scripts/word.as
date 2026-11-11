// Runs on each judgement's label: writes its word into the text field placed there.
var words = ["良", "可", "不可", "良", "可"];
var index = Math.floor((this._currentframe - 11) / 30);
this["word" + index].text = words[index];
