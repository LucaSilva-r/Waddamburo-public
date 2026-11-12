// The song's title, top right. The game draws its own title into a picture; this one reads the text.
var self = this;

Wd.on("song.title", function(title) {
    self.title.text = title == undefined ? "" : title;
});
Wd.on("song.subtitle", function(subtitle) {
    self.subtitle.text = subtitle == undefined ? "" : subtitle;
});
stop();
