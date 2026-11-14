// A dan course's card between songs: "Song N" and the song's title, for three seconds as each song starts.
var self = this;
var left = 0; // frames the card still shows
self.card.text = "";

function show() {
    var song = Wd.get("dan.song");
    var title = Wd.get("song.title");
    self.card.text = song == undefined ? "" : "Song " + song + (title == undefined ? "" : ":  " + title);
}
Wd.on("dan.song", function(song) {
    if (song == undefined)
        return;
    left = 180;
    show();
});
// The title can come after the song's number: the card follows it while it shows.
Wd.on("song.title", function(title) {
    if (left > 0)
        show();
});
this.onEnterFrame = function() {
    if (left > 0 && --left == 0)
        self.card.text = "";
};

stop();
