// Waddamburo skins in VS Code: the engine's Wd names completed and explained (wd-names.json, written by
// `Waddamburo.App --dump-wd=...`), the package's labels and clip names from its movie.json, and the skin's problems
// on save (`Waddamburo.App --lint-skin=<skin> --json`). Plain JavaScript, no build step: see README.md.
const vscode = require('vscode');
const fs = require('fs');
const path = require('path');
const { execFile } = require('child_process');

const names = JSON.parse(fs.readFileSync(path.join(__dirname, 'wd-names.json'), 'utf8')).names;
const byName = new Map(names.map((name) => [name.name, name]));
// What each call takes: Wd.on hears states, lists and events; Wd.get reads states and lists; Wd.request asks.
const accepts = { on: ['state', 'list', 'event'], get: ['state', 'list'], request: ['request'] };

function describe(name) {
    const text = new vscode.MarkdownString();
    text.appendMarkdown(`**${name.name}** — ${name.kind} (${name.type || 'no arguments'})\n\n${name.description}\n\n`);
    text.appendMarkdown(`*Has a value during:* ${name.scope}` + (name.sample ? `  ·  *e.g.* \`${name.sample}\`` : ''));
    return text;
}

// The folder upwards from a file that holds `marker` (a package's movie.json, a skin's skin.json), or null.
function findUp(file, marker) {
    for (let dir = path.dirname(file); ; dir = path.dirname(dir)) {
        if (fs.existsSync(path.join(dir, marker))) return dir;
        if (path.dirname(dir) === dir) return null;
    }
}

// A package's movie.json, read as the engine does (whole-line // comments allowed).
function readMovie(file) {
    const dir = findUp(file, 'movie.json');
    if (!dir) return null;
    try {
        const text = fs.readFileSync(path.join(dir, 'movie.json'), 'utf8').replace(/^\s*\/\/.*$/gm, '');
        return JSON.parse(text);
    } catch {
        return null;
    }
}

function packageCompletions(document) {
    const movie = readMovie(document.fileName);
    const labels = new Set();
    const clips = new Set();
    for (const [sprite, definition] of Object.entries(movie?.sprites ?? {})) {
        for (const label of Object.keys(definition.labels ?? {})) labels.add(label);
        for (const layer of definition.layers ?? []) if (layer.name) clips.add(layer.name);
        if (definition.export) clips.add(definition.export);
        void sprite;
    }
    return { labels, clips };
}

function completions(document, position) {
    const before = document.lineAt(position.line).text.slice(0, position.character);
    const wd = /Wd\.(on|get|request)\(\s*["']([\w.]*)$/.exec(before);
    if (wd) {
        const wanted = accepts[wd[1]];
        return names.filter((name) => wanted.includes(name.kind)).map((name) => {
            const item = new vscode.CompletionItem(name.name, name.kind === 'event' ? vscode.CompletionItemKind.Event
                : name.kind === 'request' ? vscode.CompletionItemKind.Function : vscode.CompletionItemKind.Property);
            item.detail = `${name.kind}: ${name.type}`;
            item.documentation = describe(name);
            item.range = new vscode.Range(position.line, position.character - wd[2].length, position.line, position.character);
            return item;
        });
    }
    const { labels, clips } = packageCompletions(document);
    const label = /(gotoAndPlay|gotoAndStop)\(\s*["']([\w-]*)$/.exec(before);
    if (label) {
        return [...labels].map((name) => {
            const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.EnumMember);
            item.detail = 'label of this package';
            item.range = new vscode.Range(position.line, position.character - label[2].length, position.line, position.character);
            return item;
        });
    }
    if (/(\bthis|\b\w+)\.\w*$/.test(before) && !/Wd\.\w*$/.test(before)) {
        return [...clips].map((name) => {
            const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Field);
            item.detail = 'clip of this package (an instance name, or an export for attachMovie)';
            return item;
        });
    }
    if (/\bWd\.\w*$/.test(before)) {
        return ['on', 'get', 'request'].map((method) => {
            const item = new vscode.CompletionItem(method, vscode.CompletionItemKind.Method);
            item.detail = { on: 'hear a state or event', get: 'read a state now', request: 'ask the engine' }[method];
            return item;
        });
    }
    return [];
}

function hover(document, position) {
    const range = document.getWordRangeAtPosition(position, /[\w.]+/);
    if (!range) return null;
    const name = byName.get(document.getText(range));
    return name ? new vscode.Hover(describe(name), range) : null;
}

// Waddamburo.App: the setting, else a checkout's build found up from the skin (movies/ is a submodule of the engine).
function app(skin) {
    const configured = vscode.workspace.getConfiguration('waddamburo').get('app');
    if (configured) return configured;
    for (let dir = skin; path.dirname(dir) !== dir; dir = path.dirname(dir)) {
        for (const build of ['Debug', 'Release']) {
            const dll = path.join(dir, 'src', 'Waddamburo.App', 'bin', build, 'net10.0', 'Waddamburo.App.dll');
            if (fs.existsSync(dll)) return dll;
        }
    }
    return null;
}

function lint(diagnostics, output, file) {
    const skin = findUp(file, 'skin.json');
    if (!skin) return;
    const program = app(skin);
    if (!program) {
        output.appendLine('No Waddamburo.App found: set waddamburo.app to check the skin on save.');
        return;
    }
    const [command, args] = program.endsWith('.dll') ? ['dotnet', [program]] : [program, []];
    execFile(command, [...args, `--lint-skin=${skin}`, '--json'], { maxBuffer: 16 * 1024 * 1024 }, (error, stdout) => {
        let problems;
        try {
            problems = JSON.parse(stdout.split('\n').find((line) => line.startsWith('[')) ?? '[]');
        } catch {
            output.appendLine(`The skin check did not answer: ${error?.message ?? stdout}`);
            return;
        }
        diagnostics.clear();
        const byFile = new Map();
        for (const problem of problems) {
            const uri = vscode.Uri.file(path.join(skin, problem.file));
            const line = Math.max(0, problem.line - 1);
            const column = Math.max(0, problem.column - 1);
            const diagnostic = new vscode.Diagnostic(new vscode.Range(line, column, line, problem.line > 0 ? 1000 : 0), problem.text,
                problem.level === 'error' ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
            diagnostic.source = 'waddamburo';
            byFile.set(uri.toString(), [...(byFile.get(uri.toString()) ?? []), [uri, diagnostic]]);
        }
        for (const entries of byFile.values()) diagnostics.set(entries[0][0], entries.map((entry) => entry[1]));
    });
}

function activate(context) {
    const selector = { language: 'actionscript2' };
    const diagnostics = vscode.languages.createDiagnosticCollection('waddamburo');
    const output = vscode.window.createOutputChannel('Waddamburo');
    context.subscriptions.push(
        diagnostics, output,
        vscode.languages.registerCompletionItemProvider(selector, { provideCompletionItems: completions }, '"', "'", '.'),
        vscode.languages.registerHoverProvider(selector, { provideHover: hover }),
        vscode.workspace.onDidSaveTextDocument((document) => {
            if (/\.(as|json)$/.test(document.fileName)) lint(diagnostics, output, document.fileName);
        }),
    );
    const open = vscode.window.activeTextEditor?.document;
    if (open && /\.(as|json)$/.test(open.fileName)) lint(diagnostics, output, open.fileName);
}

module.exports = { activate, deactivate() {} };
