// The card prompt (card.*): while a cabinet takes cards, the pairing code to enter on the website (with its seconds
// left), or the player whose card waits for the credit. Nothing without a cabinet link.

var self = this;

function refresh() {
    var player = Wd.get("card.player");
    var code = Wd.get("card.code");
    var text = "";
    if (player != undefined) {
        text = "CARD:  " + player;
    } else if (Wd.get("card.accepting") == true && code != undefined) {
        text = "PAIR A CARD:  " + code + "   " + Wd.get("card.codeSeconds");
    }
    self.plate._visible = text != "";
    self.line.text = text;
}

Wd.on("card.accepting", refresh);
Wd.on("card.code", refresh);
Wd.on("card.codeSeconds", refresh);
Wd.on("card.player", refresh);

refresh();
stop();
