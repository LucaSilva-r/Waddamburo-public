// The link to the score server (network.state): green online, yellow while checking or when the server refuses this
// machine, red when it cannot be reached; nothing when no server is set.

var WORDS = { online: "ONLINE", checking: "CONNECTING", noToken: "NOT REGISTERED", unauthorized: "NOT AUTHORISED",
    noInternet: "NO INTERNET", unreachable: "OFFLINE", timeout: "OFFLINE", serverError: "SERVER ERROR" };

var self = this;

Wd.on("network.state", function(state) {
    var known = state != undefined && state != "none";
    self.ok._visible = state == "online";
    self.warn._visible = state == "checking" || state == "noToken" || state == "unauthorized";
    self.off._visible = known && !self.ok._visible && !self.warn._visible;
    self.label.text = known ? WORDS[state] : "";
});

stop();
