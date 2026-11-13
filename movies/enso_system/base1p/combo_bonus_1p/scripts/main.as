// The sign at every hundredth combo, heard from the provider.
var self = this;
var left = 0; // frames the sign still shows

Wd.on("combo.milestone", function(combo) {
    self.word.text = combo + " COMBO!";
    left = 90;
});
this.onEnterFrame = function() {
    if (left > 0 && --left == 0) self.word.text = "";
};

stop();
