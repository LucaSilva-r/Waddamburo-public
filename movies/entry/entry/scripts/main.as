// The entry's behaviour, bare minimum: a drum joins with its DON, a joined drum's DON (or the timer) starts.
// The calls to the game and their order are the ones the game's own entry makes on that path.
import flash.external.ExternalInterface;

var SECONDS = 50;
var DON = [90, 67];       // Key codes the game presses for each drum's DON (Z, C)
var TRIGGER_NONE = 3;     // SetPrevious's trigger: 0 the left drum joins at once, 1 the right, 2 both

var self = this;
var joined = [false, false];
var held = [true, true];  // a DON still down from the screen before does not count
var trigger = TRIGGER_NONE;
var setup = false;        // the home player setup is open over the entry: it has the drums
var started = false;      // InitInfo sent
var triggered = false;
var done = false;

function refresh() {
    self.heading.text = "PLAYER ENTRY";
    for (var p = 0; p < 2; p++) {
        var panel = p == 0 ? self.p1 : self.p2;
        panel.side.text = (p + 1) + "P";
        panel.state.text = joined[p] ? "READY" : setup ? "" : "DON TO JOIN";
    }
    self.hint.text = setup ? "" : joined[0] || joined[1] ? "HIT DON TO START" : "";
}

function join(p) {
    if (joined[p] || !Lumen.EntryCoin(p, p)) {
        return;
    }
    var first = !joined[0] && !joined[1];
    joined[p] = true;
    if (first) {
        Lumen.StartTimer(SECONDS);
        Lumen.NotifyDataSelect(false);
    }
    Lumen.SetIndicator(p, 2);
    refresh();
}

function decide() {
    done = true;
    Lumen.NotifyDecide(0, 0);
    Lumen.NotifyModeSelectEnd();
    Lumen.SetNextScene(1, 0, 0);
    Lumen.Terminate();
}

ExternalInterface.addCallback("SetPrevious", this, function(scene_, trigger_) {
    trigger = trigger_;
});

// A drum that is already in (back from Song Select to let the other one join).
ExternalInterface.addCallback("SetPlayer", this, function(playerID, isCoin_, isData_, isFix_) {
    if (isCoin_ && (playerID == 0 || playerID == 1)) {
        joined[playerID] = true;
        refresh();
    }
});

// Our own (not in the game's entry; the host looks for them): the home player setup opens over the entry and
// takes the drums, both sides shown as joined; when it closes, the drums that play are told and the entry starts.
ExternalInterface.addCallback("WdSetupOpen", this, function() {
    setup = true;
    refresh();
});
ExternalInterface.addCallback("WdSetupDone", this, function(playing1p, playing2p) {
    setup = false;
    joined = [playing1p, playing2p];
    refresh();
    if (!done && (playing1p || playing2p)) {
        decide();
    }
});

this.onEnterFrame = function() {
    if (done || Lumen == undefined) {   // no game behind it (the editor, a test): only the picture
        return;
    }
    if (!started) {
        started = true;
        Lumen.InitInfo();
        refresh();
    }
    if (!Lumen.IsStartLumen() || !Lumen.IsReady()) {
        return;
    }
    if (!triggered) {
        triggered = true;
        if (trigger == 0 || trigger == 2) {
            join(0);
        }
        if (trigger == 1 || trigger == 2) {
            join(1);
        }
    }
    if (setup) {
        return;
    }
    var start = false;
    for (var p = 0; p < 2; p++) {
        var down = Key.isDown(DON[p]);
        if (down && !held[p]) {
            if (joined[p]) {
                start = true;
            } else {
                join(p);
            }
        }
        held[p] = down;
    }
    if ((joined[0] || joined[1]) && (start || (Lumen.IsTimeStarted() && Lumen.IsTimeup()))) {
        decide();
    }
};


// Called as: not called in the recorded sessions
ExternalInterface.addCallback("AddCampaign", this, function(id) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignCosBody", this, function(playerID, bodyID_) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignCosHead", this, function(playerID, headID_) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignCostume", this, function(playerID, cosID_) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("AssignRandomCostume", this, function(playerID, cosID) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("AssignRandomHeadBody", this, function(playerID, headID, bodyID) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("AssignRandomPaintAcce", this, function(playerID, paintID, acceID) {
});

// Called as: (number, number, number) -> undefined
ExternalInterface.addCallback("AssignSlotCostume", this, function(playerID, slotID, cosID) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("AssignSlotHeadBody", this, function(playerID, slotID, headID, bodyID) {
});

// Called as: (number, number, number, number) -> undefined
ExternalInterface.addCallback("AssignSlotPaintAcce", this, function(playerID, slotID, paintID, acceID) {
});

// Called as: (number, bool, bool, bool) -> undefined
ExternalInterface.addCallback("BnCoinInit", this, function(cost, isopen, isenable, isplay) {
});

// Called as: (bool) -> undefined
ExternalInterface.addCallback("BnCoinSetFree", this, function(isfree) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("BnCoinSetRemain", this, function(remain, is_connect) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("FinishCostumeLoad", this, function(playerID) {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("InitCostumeList", this, function(playerID) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("InputCardReader", this, function() {
});

// Called as: (number) -> undefined
ExternalInterface.addCallback("ReleaseCostume", this, function(playerID) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("ReplyServer", this, function(isSuccess_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("SetCardInfo", this, function(playerID, mediaType_, isNewEntry_, isNewCard_) {
});

// Called as: not called in the recorded sessions
ExternalInterface.addCallback("TweenCostumeLoad", this, function(playerID) {
});

// Called as: (number, number) -> undefined
ExternalInterface.addCallback("UpdateCoins", this, function(requireCoins0, requireCoins1) {
});
refresh();
stop();
