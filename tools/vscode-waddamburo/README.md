# Waddamburo Skins for VS Code

ActionScript 2 for a Waddamburo skin's scripts (`.as`):

- Highlighting, snippets (`won`, `wget`, `wreq`, `oef`).
- **Wd names**: inside `Wd.on("`, `Wd.get("` and `Wd.request("` every name the engine gives movies, filtered to what
  that call takes, with its description, when it has a value and a sample; the same on hover.
- **The package's own**: labels inside `gotoAndPlay("` / `gotoAndStop("`, clip names (instances, exports) after `.`,
  from the script's package's `movie.json`.
- **Problems on save**: the skin is checked as the engine reads it (`Waddamburo.App --lint-skin=<skin> --json`) and
  its errors and warnings show in the files they are in. Waddamburo.App: the `waddamburo.app` setting, or a checkout's
  build found up from the skin.

`wd-names.json` is written by the engine (`Waddamburo.App --dump-wd=tools/vscode-waddamburo/wd-names.json`); a test
fails when it falls behind `WdNames`.

## Install

No build step: `python3 tools/vscode-waddamburo/package-vsix.py` packs `waddamburo-<version>.vsix` (no npm or
vsce), then `code --install-extension tools/vscode-waddamburo/waddamburo-0.1.0.vsix` and reload the window. Pack and
install again after pulling a change (or after `--dump-wd` brought new names).
