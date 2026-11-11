# Movie package sample

`bounce/` is a movie package: a movie of our own that the engine plays like one of the game's
(`docs/development/free-movies-plan.md`). `movie.json` describes it (textures, shapes, sprites as
layers with keyframes, labels); `textures/ball.png` was drawn by a script for this sample.

```sh
dotnet run --project src/Waddamburo.App -- --package=samples/movie-package/bounce
```

In the game, a package at `USRDIR/waddamburo/movies/<archive folder>/<movie>/` replaces that movie
of the game data (e.g. `movies/indicator/time_counter/`).
