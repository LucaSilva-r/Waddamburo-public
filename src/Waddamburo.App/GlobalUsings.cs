// The app's own namespaces, one per folder: every file sees them all (the flows, overlays and hosts call one another
// throughout the app, so per-file usings would only list most of them everywhere).
global using Waddamburo.App.Audio;
global using Waddamburo.App.Flow;
global using Waddamburo.App.Flow.Cabinet;
global using Waddamburo.App.Flow.Credit;
global using Waddamburo.App.Flow.Diagnostics;
global using Waddamburo.App.Flow.Gameplay;
global using Waddamburo.App.Flow.SongSelect;
global using Waddamburo.App.Gameplay;
global using Waddamburo.App.Online;
global using Waddamburo.App.Sync;
global using Waddamburo.Services.Online;
global using Waddamburo.Services.Scores;
