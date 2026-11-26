// Song select as a carousel, after osu!lazer's: the row under the cursor sits at the list's centre and every other
// panel eases to its place on an arc (lazer's Carousel: panels damp towards their positions, x = the arc's offset); a
// row opened in place lets its nodes slide out of it, a closed one folds them back in. The rims move the cursor; DON
// opens or closes a folder or a song, and on a chart plays it (song.start). Either drum drives the one cursor.
//
// The engine gives rows by index only: the movie keeps each panel on its node across an open or a close by shifting
// the rows under the opened one by the change in rows.count.

var CENTER = 380;           // the cursor's row, on screen
var LIST_X = 600;           // where the panels start (before the arc and the indent)
var HALF = 360;             // the arc's half height (lazer: the carousel's visible half height)
var GAP = 8;
var EASE_Y = 0.80;          // what is left of the distance after one tick (lazer: a 50 ms half-life, here a bit softer)
var EASE_X = 0.82;
var EASE_FADE = 0.78;
var WINDOW = 24;            // rows asked for on each side of the cursor
var COURSES = {easy: "Easy", normal: "Normal", hard: "Hard", oni: "Oni", ura: "Ura Oni"};

var self = this;
var list = this.list;
var panels = [];            // every panel clip made, bound or not
var byRow = {};             // row -> panel
var made = 0;
var started = false;
var frame = 0;
var pending = undefined;    // an open or a close waiting for rows.count to change
var opened = undefined;     // {row, from, to, until}: nodes that slide out of their parent
var shownKey = "";
var windowFirst = -1;

function height(kind) {
    return kind == "folder" ? 72 : kind == "chart" ? 46 : 66;
}

function arc(y) {
    var dist = Math.abs(y - CENTER) / HALF;
    var d = 9 - dist * dist;
    return (3 - Math.sqrt(d > 0 ? d : 0)) * HALF;
}

function setText(field, text) {
    if (field.text != text) {
        field.text = text;
    }
}

function thousands(n) {
    var s = "" + Math.floor(n);
    var out = "";
    while (s.length > 3) {
        out = "," + s.substr(s.length - 3) + out;
        s = s.substr(0, s.length - 3);
    }
    return s + out;
}

// ---- panels -------------------------------------------------------------------------------------------------------

function take() {
    for (var i = 0; i < panels.length; i++) {
        if (panels[i].row == undefined && !panels[i].dying) {
            return panels[i];
        }
    }
    var clip = list.attachMovie("panel", "p" + made, made);
    made++;
    var p = {clip: clip, row: undefined, y: 0, x: 1400, alpha: 0, glow: 0, dying: false};
    panels.push(p);
    return p;
}

// Fills a panel with its row's look.
function dress(p) {
    var r = p.row;
    var kind = Wd.get("row.kind", r);
    var c = p.clip;
    p.kind = kind;
    p.depth = Wd.get("row.depth", r);
    p.playable = Wd.get("row.playable", r) != false;
    c.folder._visible = kind == "folder";
    c.song._visible = kind == "song" || kind == "course";
    c.chart._visible = kind == "chart";
    c.glow._yscale = height(kind) / 72 * 100;
    var title = Wd.get("row.title", r);
    var sub = Wd.get("row.subtitle", r);
    if (kind == "chart") {
        setText(c.title, "");
        setText(c.sub, "");
        setText(c.chart_title, title == undefined ? "" : title);
        var course = Wd.get("row.course", r);
        var stars = Wd.get("row.stars", r);
        c.chip._visible = true;
        c.chip.easy._visible = course == "easy";
        c.chip.normal._visible = course == "normal" || course == undefined;
        c.chip.hard._visible = course == "hard";
        c.chip.oni._visible = course == "oni";
        c.chip.ura._visible = course == "ura";
        setText(c.chip.label, stars == undefined ? "-" : "" + stars);
        setText(c.meta, "");
    } else {
        c.chip._visible = false;
        setText(c.chart_title, "");
        setText(c.title, title == undefined ? "" : title);
        setText(c.sub, sub == undefined ? "" : sub);
        c.title._y = sub == undefined ? -17 : -29;
        var count = Wd.get("row.count", r);
        setText(c.meta, count == undefined ? "" : "" + count);
    }
    c._visible = true;
}

function bind(r, y, alpha, x) {
    var p = take();
    p.row = r;
    p.y = y;
    p.alpha = alpha;
    p.glow = 0;
    if (x != undefined) {
        p.x = x;
    }
    byRow[r] = p;
    dress(p);
    return p;
}

function release(p) {
    if (p.row != undefined && byRow[p.row] == p) {
        delete byRow[p.row];
    }
    p.row = undefined;
    p.dying = false;
    p.clip._visible = false;
}

function rebuildIndex() {
    byRow = {};
    for (var i = 0; i < panels.length; i++) {
        if (panels[i].row != undefined) {
            byRow[panels[i].row] = panels[i];
        }
    }
}

// An open or a close took: the rows under it moved by the change in rows.count.
function shift(op, count) {
    var d = count - op.count;
    if (op.open) {
        for (var i = 0; i < panels.length; i++) {
            if (panels[i].row != undefined && panels[i].row > op.row) {
                panels[i].row += d;
            }
        }
        opened = {row: op.row, from: op.row + 1, to: op.row + d, until: frame + 40};
    } else {
        var n = -d;
        var parent = byRow[op.row];
        for (var j = 0; j < panels.length; j++) {
            var p = panels[j];
            if (p.row == undefined) {
                continue;
            }
            if (p.row > op.row && p.row <= op.row + n) {
                p.row = undefined;       // folds into its parent, fading
                p.dying = true;
                p.into = parent;
            } else if (p.row > op.row + n) {
                p.row -= n;
            }
        }
    }
    rebuildIndex();
}

// ---- layout ---------------------------------------------------------------------------------------------------------

// Each row's place on screen, outwards from the cursor's row at the centre.
function targets(cur, count) {
    var t = {};
    t[cur] = CENTER;
    var y = CENTER;
    var h = height(Wd.get("row.kind", cur));
    for (var r = cur + 1; r < count && y < 900; r++) {
        var hr = height(Wd.get("row.kind", r));
        y += h / 2 + GAP + hr / 2;
        t[r] = y;
        h = hr;
    }
    y = CENTER;
    h = height(Wd.get("row.kind", cur));
    for (var q = cur - 1; q >= 0 && y > -180; q--) {
        var hq = height(Wd.get("row.kind", q));
        y -= h / 2 + GAP + hq / 2;
        t[q] = y;
        h = hq;
    }
    return t;
}

function layout() {
    var count = Wd.get("rows.count");
    var cur = Wd.get("selected.row");
    if (count == undefined || cur == undefined || Wd.get("row.kind", cur) == undefined) {
        return;
    }
    var t = targets(cur, count);
    var first = frame == 1 || panels.length == 0;

    // How far behind their places the settled panels are: one that comes in joins them where they are.
    var lag = 0;
    var lagged = 0;
    for (var i = 0; i < panels.length; i++) {
        var p = panels[i];
        if (p.row != undefined && t[p.row] != undefined) {
            lag += p.y - t[p.row];
            lagged++;
        }
    }
    lag = lagged > 0 ? lag / lagged : 0;

    // Rows that should be on screen get a panel.
    for (var r = Math.max(0, cur - WINDOW); r < Math.min(count, cur + WINDOW); r++) {
        if (t[r] == undefined || t[r] < -120 || t[r] > 840 || byRow[r] != undefined || Wd.get("row.kind", r) == undefined) {
            continue;
        }
        if (opened != undefined && r >= opened.from && r <= opened.to && byRow[opened.row] != undefined) {
            bind(r, byRow[opened.row].y, 0, byRow[opened.row].x);
        } else if (panels.length == 0 || shownKey == "") {
            // The screen's first frame: the list sweeps in from the right, the far rows last.
            bind(r, t[r], 0, LIST_X + 300 + Math.abs(t[r] - CENTER) * 0.6);
        } else {
            bind(r, t[r] + lag, 1);
        }
    }
    if (opened != undefined && frame > opened.until) {
        opened = undefined;
    }

    // Every panel eases towards its place.
    for (var k = 0; k < panels.length; k++) {
        var q = panels[k];
        if (q.row == undefined && !q.dying) {
            continue;
        }
        var ty;
        var ta;
        var selected = q.row == cur;
        if (q.dying) {
            ty = q.into != undefined && q.into.row != undefined ? q.into.y : q.y;
            ta = 0;
        } else if (t[q.row] == undefined) {
            ty = q.row < cur ? -300 : 1100;  // off the list's ends: slides away
            ta = 1;
        } else {
            ty = t[q.row];
            ta = q.playable ? 1 : 0.45;
        }
        q.y = ty + (q.y - ty) * EASE_Y;
        var tx = LIST_X + arc(q.y) + q.depth * 26 + (selected ? 0 : q.kind == "chart" ? 36 : 60);
        q.x = tx + (q.x - tx) * EASE_X;
        q.alpha = ta + (q.alpha - ta) * EASE_FADE;
        var tg = selected ? 0.85 + 0.15 * Math.sin(frame / 18) : 0;
        q.glow = tg + (q.glow - tg) * (selected ? 0.7 : 0.85);
        var c = q.clip;
        c._x = q.x;
        c._y = q.y;
        c._alpha = q.alpha * 100;
        c.glow._alpha = q.glow * 100;
        if (q.dying && q.alpha < 0.02) {
            release(q);
        } else if (!q.dying && (q.y < -160 || q.y > 880) && (t[q.row] == undefined || t[q.row] < -160 || t[q.row] > 880)) {
            release(q);
        }
    }
}

// ---- the selection's details ---------------------------------------------------------------------------------------

var info = this.info;
var infoAlpha = 0;
var infoX = 40;

function details() {
    var cur = Wd.get("selected.row");
    var title = Wd.get("selected.title");
    var key = cur + "|" + title;
    if (key != shownKey && title != undefined) {
        shownKey = key;
        var kind = Wd.get("selected.kind");
        var sub = Wd.get("row.subtitle", cur);
        if (kind == "chart") {
            // A chart: its song's title and subtitle (the row it sits under).
            var depth = Wd.get("row.depth", cur);
            for (var up = cur - 1; up >= 0 && Wd.get("row.depth", up) != undefined; up--) {
                if (Wd.get("row.depth", up) < depth) {
                    title = Wd.get("row.title", up);
                    sub = Wd.get("row.subtitle", up);
                    break;
                }
            }
        }
        setText(info.title, title);
        setText(info.sub, sub == undefined ? "" : sub);
        var meta = "";
        if (kind == "chart") {
            var course = Wd.get("row.course", cur);
            var stars = Wd.get("row.stars", cur);
            meta = (course == undefined ? Wd.get("row.title", cur) : COURSES[course]) + (stars == undefined ? "" : "   Lv " + stars);
        } else if (Wd.get("row.count", cur) != undefined) {
            meta = Wd.get("row.count", cur) + (kind == "folder" ? " songs" : kind == "song" ? " charts" : " songs");
        }
        setText(info.meta, meta);
        infoAlpha = 0;
        infoX = 16;
    }
    var ranks = Wd.get("scores.count");
    setText(info.label, ranks == undefined || ranks == 0 ? "" : "TOP SCORES");
    for (var r = 0; r < 3; r++) {
        setText(info["rank" + r], ranks == undefined || r >= ranks ? ""
            : "#" + (r + 1) + "   " + Wd.get("scores.name", r) + "   " + thousands(Wd.get("scores.score", r)));
    }
    infoAlpha = 1 + (infoAlpha - 1) * 0.8;
    infoX = 40 + (infoX - 40) * 0.82;
    info._alpha = infoAlpha * 100;
    info._x = infoX;
}

// ---- input ----------------------------------------------------------------------------------------------------------

function show(row) {
    var count = Wd.get("rows.count");
    if (count == undefined || count == 0) {
        return;
    }
    Wd.request("select.row", Math.max(0, Math.min(count - 1, row)));
}

function decide() {
    if (pending != undefined) {
        return;
    }
    var row = Wd.get("selected.row");
    var kind = Wd.get("selected.kind");
    if (kind == "chart") {
        started = Wd.request("song.start");
        return;
    }
    var open = Wd.get("row.opened", row) != true;
    pending = {row: row, open: open, count: Wd.get("rows.count"), until: frame + 10};
    Wd.request(open ? "row.open" : "row.close", row);
}

// Out of time: one step a frame towards a chart (open what is closed, go down into it), then play it.
function pickForThePlayer() {
    var row = Wd.get("selected.row");
    if (Wd.get("selected.kind") == "chart") {
        decide();
    } else if (Wd.get("row.opened", row) == true) {
        show(row + 1);
    } else {
        decide();
    }
}

function hit(side, part) {
    if (started) {
        return;
    }
    if (part == "don") {
        decide();
    } else {
        show(Wd.get("selected.row") + (side == "left" ? -1 : 1));
    }
}

Wd.on("drum.hit", function(side, part) {
    hit(side, part);
}, 1);
Wd.on("drum.hit", function(side, part) {
    hit(side, part);
}, 2);

this.onEnterFrame = function() {
    frame++;
    var count = Wd.get("rows.count");
    if (pending != undefined && count != undefined && count != pending.count) {
        shift(pending, count);
        pending = undefined;
    } else if (pending != undefined && frame > pending.until) {
        pending = undefined;     // refused (a choice folder takes over, or nothing to open)
    }
    var cur = Wd.get("selected.row");
    if (cur != undefined) {
        var first = Math.max(0, cur - WINDOW);
        if (first != windowFirst || Wd.get("row.kind", cur) == undefined) {
            windowFirst = first;
            Wd.request("rows.window", first, WINDOW * 2);
        }
    }
    if (!started && Wd.get("select.timeLeft") == 0) {
        pickForThePlayer();
    }
    layout();
    details();
    var left = Wd.get("select.timeLeft");
    self.pill._visible = left != undefined;
    setText(self.clock, left == undefined ? "" : "" + left);
    self.clock._alpha = left != undefined && left <= 10 ? 70 + 30 * Math.sin(frame / 5) : 100;
};

setText(this.heading, "SONG SELECT");
setText(this.hint, "RIM  move        DON  open / play");
info._alpha = 0;
stop();
