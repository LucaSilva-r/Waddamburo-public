// A drumroll: the engine places the movie at the head, turned along the roll; note.length is how far the tail is.
var self = this;

Wd.on("note.length", function(length) {
    if (length == undefined)
        return;
    self.body._xscale = length; // the body is 100 pixels wide at 100%
    self.tail._x = length;
});
// Each hit swells the head, which settles back.
Wd.on("note.hit", function(part) {
    self.head._xscale = 115;
    self.head._yscale = 115;
});
this.onEnterFrame = function() {
    if (self.head._xscale > 100) {
        self.head._xscale = Math.max(100, self.head._xscale - 3);
        self.head._yscale = self.head._xscale;
    }
};

stop();
