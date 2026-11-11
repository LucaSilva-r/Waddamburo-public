// Runs on every label: shows that label's word (five frames each) and stays on it.
var words = ["ドン", "ド", "コ", "カッ", "カ", "ドン(大)", "カッ(大)", "連打ーっ!!", "ーっ", "ゲキ連打ーっ!!", "ーっ", "イモ連打ーっ!!", "ドン", "カッ", "連打(大)ーっ!!", "ドン", "カッ"];
this.word.text = words[Math.floor((this._currentframe - 1) / 5)];
stop();
