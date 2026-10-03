using Waddamburo.App.Presentation;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Game;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Tools;

/// <summary>A local browser for user-owned movies and audio; no game-data layout required.</summary>
internal sealed class FilePreview : IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nub", ".wav", ".ogg", ".opus", ".mp3", ".flac", ".aac", ".m4a",
        ".at3", ".at9", ".vag", ".pam", ".pamf", ".bnsf", ".idsp", ".adx", ".hca", ".wem"
    };
    private readonly SdlApplication _application;
    private readonly RenderTextureId _glyphs, _white;
    private readonly List<RenderTextureId> _movieTextures = [];
    private PreviewTree? _tree;
    private List<PreviewTree.Row> _rows = [];
    private int _selected, _scroll, _cue;
    private string _cueInput = "";
    private string? _archivePath;
    private string _location = "", _status = "Select a file to preview", _title = "FILE PREVIEW";
    private DdpArchive? _archive;
    private LumenPlayer? _movie;
    private PreviewTree.Node? _movieNode;
    // M: run movies without the Lumen game object, i.e. their developer branch, which loads its
    // indicator parts itself through MovieClipLoader.
    private bool _developerMode;
    // Terminal lines ("SetTime 30") call the playing movie's callbacks.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();
    private SdlAudioDevice? _device;
    private StreamingMusicPlayer? _audio;
    private string? _audioPath;
    private bool _paused;
    private SdlKeyboardSnapshot _previous = SdlKeyboardSnapshot.Empty;
    private const int VisibleRows = 19;
    private enum DragTarget { None, Scrollbar, Timeline }
    private DragTarget _drag;
    private float _thumbGrabOffset, _wheelRemainder;
    private float _pointerX = .15f;
    private int? _pendingSeekFrame;

    private int maximumScroll => Math.Max(0, _rows.Count - VisibleRows);
    private int thumbHeight => Math.Max(18, VisibleRows * 26 * VisibleRows / Math.Max(VisibleRows, _rows.Count));
    private float thumbTop => 100 + (VisibleRows * 26 - thumbHeight) * _scroll / (float)Math.Max(1, maximumScroll);

    private FilePreview(int width, int height)
    {
        new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
                _commands.Enqueue(line);
        }) { IsBackground = true, Name = "Preview callback console" }.Start();
        _application = new SdlApplication("Waddamburo - File Preview", width, height, resizable: true, showCursor: true);
        _glyphs = _application.UploadRgba8(96, 32, BitmapFont.CreateAtlas());
        _white = _application.UploadRgba8(1, 1, new byte[] { 255, 255, 255, 255 });
    }

    public static void Run(string path, int width, int height, int? frames = null, int? ticks = null,
        string? screenshot = null, SdlKeyboardTimeline? inputTimeline = null)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("The preview file or directory does not exist.", fullPath);
        VgmstreamCli.GameFolder = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        using var preview = new FilePreview(width, height);
        if (Directory.Exists(fullPath))
            preview.browse(fullPath);
        else
        {
            preview.browse(Path.GetDirectoryName(fullPath)!);
            var node = preview._tree!.FindFile(fullPath);
            if (node is null)
            {
                node = new PreviewTree.Node(Path.GetFileName(fullPath), fullPath);
                preview._tree.Root.Add(node);
            }
            PreviewTree.Reveal(node);
            preview.refreshRows(node);
            preview.activate();
        }
        var simulationTick = 0;
        preview._application.Run(preview.render,
            keyboard => preview.tick(inputTimeline?.Apply(++simulationTick, keyboard) ?? keyboard), frames, ticks,
            screenshot is null ? null : capture => ScreenshotWriter.Write(screenshot, capture),
            pointerClicked: preview.click,
            pointerMoved: preview.pointerMoved,
            pointerReleased: preview.releasePointer,
            pointerScrolled: preview.scrollWheel);
    }

    private void stopPlayback()
    {
        _audio?.Dispose();
        _audio = null;
        _device?.Pause();
        _device?.Clear();
        _audioPath = null;
        _movie = null;
        _movieNode = null;
        foreach (var texture in _movieTextures)
            _application.ReleaseTexture(texture);
        _movieTextures.Clear();
        _paused = false;
        _cueInput = "";
        _pendingSeekFrame = null;
    }

    private void browse(string directory)
    {
        // Enumerate before replacing the current browser, so an unreadable directory remains recoverable.
        var files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Where(path => Path.GetExtension(path).Equals(".ddp", StringComparison.OrdinalIgnoreCase)
                || AudioExtensions.Contains(Path.GetExtension(path))).ToArray();
        var tree = new PreviewTree(directory, files);
        stopPlayback();
        _archive = null;
        _archivePath = null;
        _tree = tree;
        _location = directory;
        _selected = _scroll = 0;
        refreshRows(tree.Root);
        _title = "ASSET BROWSER";
        _status = files.Length == 0 ? "No supported files in this folder" : "Expand a folder to browse movies and audio";
    }

    private PreviewTree.Node? selectedNode => _rows.Count == 0 ? null : _rows[_selected].Node;

    private void refreshRows(PreviewTree.Node? selection)
    {
        _rows = _tree?.Visible() ?? [];
        var index = selection is null ? -1 : _rows.FindIndex(row => ReferenceEquals(row.Node, selection));
        _selected = index >= 0 ? index : Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        ensureSelectionVisible();
    }

    private void loadArchive(string path)
    {
        if (_archivePath == path && _archive is not null) return;
        if (new FileInfo(path).Length > ParserLimits.Default.MaxFileBytes)
            throw new InvalidDataException("Archive exceeds the configured parser size limit.");
        var archive = DdpArchive.Open(File.ReadAllBytes(path));
        _archive = archive;
        _archivePath = path;
    }

    private void expand(PreviewTree.Node node)
    {
        if (node.IsArchive && !node.MoviesLoaded)
        {
            loadArchive(node.Path);
            PreviewTree.SetMovies(node, _archive!.Index.Movies.Select(movie => movie.Name));
        }
        else node.Expanded = true;
        refreshRows(node);
    }

    private void attempt(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or ArgumentException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            _status = "Error: " + exception.Message;
            Console.Error.WriteLine(exception.Message);
        }
    }

    private void activate() => attempt(() =>
    {
        if (selectedNode is not { } node) return;
        if (node.CanExpand)
        {
            if (node.Expanded) { node.Expanded = false; refreshRows(node); }
            else expand(node);
        }
        else if (node.Movie is not null) playMovie(node);
        else
        {
            stopPlayback();
            _cue = 0;
            playAudio(node.Path, _cue);
        }
    });

    private void playMovie(PreviewTree.Node node)
    {
        stopPlayback();
        loadArchive(node.Path);
        var content = LumenMovieContent.Load(_archive!.OpenMovie(node.Movie!));
        if (content.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("Movie has semantic errors and cannot be presented safely.");
        foreach (var texture in content.Textures)
            _movieTextures.Add(_application.UploadRgba8((uint)texture.Width, (uint)texture.Height,
                texture.Rgba8.AsSpan()));
        _movie = content.CreatePlayer(hostBinding: _developerMode ? null : ViewerHostBinding.Instance,
            movieLoader: url => loadClip(node.Path, url));
        _movieNode = node;
        Console.WriteLine($"{node.Movie} callbacks (type 'Name arg ...' here): {string.Join(", ", _movie.CallbackNames)}");
        _status = node.Movie + (_developerMode ? " - playing (developer mode)" : " - playing");
    }

    private static void invoke(LumenPlayer movie, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;
        try
        {
            var invocation = Cli.CallbackInvocation.ParseExpression(string.Join('|', parts));
            Console.WriteLine(movie.TryInvokeCallback(invocation.Name, invocation.Arguments)
                ? $"Invoked {invocation.Name}" : $"Callback '{invocation.Name}' is not registered.");
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"Rejected: {exception.Message}");
        }
    }

    /// <summary>
    /// MovieClipLoader.loadClip for the developer branch: "../indicator/time_counter.lm" is resolved
    /// beside the requesting archive's folder (packed/indicator/packeddata.ddp, movie time_counter.lm).
    /// </summary>
    private LumenLoadedMovie? loadClip(string archivePath, string url)
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivePath)!, Path.GetDirectoryName(url) ?? ""));
        var file = Path.Combine(folder, "packeddata.ddp");
        if (!File.Exists(file))
            return null;
        var archive = DdpArchive.Open(File.ReadAllBytes(file));
        var entry = archive.Index.Movies.FirstOrDefault(movie =>
            Path.GetFileName(movie.Name).Equals(Path.GetFileName(url), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        var content = LumenMovieContent.Load(archive.OpenMovie(entry));
        var offset = (uint)_movieTextures.Count;
        foreach (var texture in content.Textures)
            _movieTextures.Add(_application.UploadRgba8((uint)texture.Width, (uint)texture.Height, texture.Rgba8.AsSpan()));
        var player = content.CreatePlayer(movieLoader: nested => loadClip(file, nested));
        Console.WriteLine($"loadClip {url} -> {Path.GetRelativePath(Path.GetDirectoryName(folder)!, file)}:{entry.Name}");
        return new LumenLoadedMovie(player, offset);
    }

    private void playAudio(string path, int cue)
    {
        _audio?.Dispose();
        _audio = null;
        _device ??= new SdlAudioDevice();
        _device.Pause();
        _device.Clear();
        _paused = false;
        var bank = Path.GetExtension(path).Equals(".nub", StringComparison.OrdinalIgnoreCase);
        _audioPath = path;
        _audio = new StreamingMusicPlayer(_device, path, bank ? checked((uint)cue + 1) : 0);
        _cue = cue;
        _audio.Play();
        _status = $"{Path.GetFileName(path)} - {(_audio.Info.TotalFrames is { } total ? $"{(double)total / _audio.Info.SampleRate:0.0}s" : "audio")}";
        Console.WriteLine($"Preview: {path}{(bank ? $", cue {cue} (decoder stream {cue + 1})" : "")}");
    }

    private void ensureSelectionVisible()
    {
        if (_selected < _scroll) _scroll = _selected;
        else if (_selected >= _scroll + VisibleRows) _scroll = _selected - VisibleRows + 1;
        _scroll = Math.Clamp(_scroll, 0, maximumScroll);
    }

    private void select(int index, bool autoplay)
    {
        if (_rows.Count == 0) return;
        var next = Math.Clamp(index, 0, _rows.Count - 1);
        if (next == _selected) { ensureSelectionVisible(); return; }
        _selected = next;
        _cueInput = "";
        ensureSelectionVisible();
        if (autoplay && selectedNode is { CanExpand: false }) activate();
    }

    private void move(int offset) => select(_selected + offset, autoplay: true);

    private void pointerMoved(float x, float y)
    {
        _pointerX = x;
        if (_drag == DragTarget.Timeline) queueTimelineSeek(x);
        else if (_drag == DragTarget.Scrollbar)
        {
            var travel = VisibleRows * 26 - thumbHeight;
            _scroll = (int)Math.Clamp(Math.Round((y * 720 - 100 - _thumbGrabOffset)
                / Math.Max(1, travel) * maximumScroll), 0, maximumScroll);
        }
    }

    private void releasePointer() => _drag = DragTarget.None;

    private void queueTimelineSeek(float x)
    {
        if (_movie is not { } movie) return;
        _paused = true;
        _pendingSeekFrame = (int)Math.Round(Math.Clamp((x * 1280 - 420) / 820, 0, 1)
            * (movie.FrameCount - 1));
    }

    private void scrollWheel(float delta)
    {
        _wheelRemainder -= delta;
        var notches = (int)_wheelRemainder;
        _wheelRemainder -= notches;
        if (notches == 0) return;
        if (_pointerX < .3f) _scroll = Math.Clamp(_scroll + notches * 3, 0, maximumScroll);
        else if (_movie is { } movie) seek(movie.CurrentFrame + notches);
        else changeCue(notches);
    }

    private void back()
    {
        if (selectedNode is not { } node) return;
        if (node.CanExpand && node.Expanded)
        {
            node.Expanded = false;
            refreshRows(node);
        }
        else if (node.Parent is { } parent)
        {
            parent.Expanded = false;
            refreshRows(parent);
        }
    }

    private void navigateRight() => attempt(() =>
    {
        if (selectedNode is not { } node || !node.CanExpand) return;
        if (!node.Expanded) expand(node);
        else if (node.Children.Count > 0) move(1);
    });

    private void replay()
    {
        if (_audioPath is { } path) attempt(() => playAudio(path, _cue));
        else if (_movie is not null) { seek(0); _paused = false; }
        else activate();
    }

    private void pause()
    {
        if (_audio is null && _movie is null) return;
        _paused = !_paused;
        if (_audio is { } audio)
        {
            if (_paused) audio.Pause(); else audio.Play();
        }
    }

    private void seek(int frame) => attempt(() =>
    {
        if (_movie is not { } movie) return;
        movie.Seek(Math.Clamp(frame, 0, movie.FrameCount - 1));
        _paused = true;
    });

    private void changeCue(int offset)
    {
        if (_movie is { } movie) { seek(movie.CurrentFrame + offset); return; }
        _cueInput = "";
        if (_audioPath is not { } path || !Path.GetExtension(path).Equals(".nub", StringComparison.OrdinalIgnoreCase))
            return;
        attempt(() => playAudio(path, Math.Clamp(_cue + offset, 0, int.MaxValue - 1)));
    }

    private void tick(SdlKeyboardSnapshot keyboard)
    {
        bool pressed(SdlKeyboardKey key) => keyboard.Presses.Any(press => press.Key == key)
            || keyboard.IsDown(key) && !_previous.IsDown(key);
        if (pressed(SdlKeyboardKey.Escape)) _application.RequestQuit();
        else if (pressed(SdlKeyboardKey.Backspace)) back();
        else if (pressed(SdlKeyboardKey.Up)) move(-1);
        else if (pressed(SdlKeyboardKey.Down)) move(1);
        else if (pressed(SdlKeyboardKey.PageUp)) move(-VisibleRows);
        else if (pressed(SdlKeyboardKey.PageDown)) move(VisibleRows);
        else if (pressed(SdlKeyboardKey.Enter))
        {
            if (_cueInput.Length > 0 && _audioPath is { } path)
            {
                var cue = int.Parse(_cueInput, System.Globalization.CultureInfo.InvariantCulture);
                _cueInput = "";
                attempt(() => playAudio(path, cue));
            }
            else activate();
        }
        else if (pressed(SdlKeyboardKey.Space)) pause();
        else if (pressed(SdlKeyboardKey.R)) replay();
        else if (pressed(SdlKeyboardKey.Left)) back();
        else if (pressed(SdlKeyboardKey.Right)) navigateRight();
        else if (pressed(SdlKeyboardKey.D) && _movie is null || pressed(SdlKeyboardKey.Q)) changeCue(-1);
        else if (pressed(SdlKeyboardKey.K) && _movie is null || pressed(SdlKeyboardKey.E)) changeCue(1);
        else if (pressed(SdlKeyboardKey.M) && _movieNode is { } node)
        {
            _developerMode = !_developerMode;
            attempt(() => playMovie(node));
        }
        if (_audioPath is { } bank && Path.GetExtension(bank).Equals(".nub", StringComparison.OrdinalIgnoreCase))
            for (var digit = 0; digit <= 9; digit++)
                if (pressed((SdlKeyboardKey)((int)SdlKeyboardKey.Digit0 + digit)))
                {
                    var input = _cueInput + digit;
                    if (input.Length <= 10 && int.TryParse(input, out var requestedCue) && requestedCue < int.MaxValue)
                        _cueInput = input;
                }
        if (_pendingSeekFrame is { } requestedFrame)
        {
            _pendingSeekFrame = null;
            if (_movie?.CurrentFrame != requestedFrame) seek(requestedFrame);
        }
        if (_movie is { } movie && !_paused)
        {
            // Drum keys reach the movie as the game maps them (D/K ka, F/J don -> authored A/S/Z).
            while (_commands.TryDequeue(out var command))
                invoke(movie, command);
            movie.Advance(LumenInputAdapter.CreateSnapshot(keyboard));
        }
        if (_audio?.Failure is { } failure) _status = "Error: " + failure.Message;
        _previous = keyboard;
    }

    private void click(float x, float y)
    {
        _pointerX = x;
        _drag = DragTarget.None;
        if (_movie is { } movie && x >= 420f / 1280 && x <= 1240f / 1280
            && y >= 577f / 720 && y <= 610f / 720)
        {
            _drag = DragTarget.Timeline;
            queueTimelineSeek(x);
            return;
        }
        // Pointer coordinates and layout share a fixed logical 1280x720 canvas.
        if (x < .3f && y >= 100f / 720 && y < (100 + VisibleRows * 26f) / 720)
        {
            if (x >= 364f / 1280 && _rows.Count > VisibleRows)
            {
                _drag = DragTarget.Scrollbar;
                var pixelY = y * 720;
                _thumbGrabOffset = pixelY >= thumbTop && pixelY <= thumbTop + thumbHeight
                    ? pixelY - thumbTop : thumbHeight / 2f;
                pointerMoved(x, y);
                return;
            }
            var row = _scroll + (int)((y * 720 - 100) / 26);
            if (row < _rows.Count)
            {
                select(row, autoplay: false);
                activate();
            }
        }
        else if (y >= 620f / 720 && y < 657f / 720)
        {
            if (x >= 420f / 1280 && x < 565f / 1280) pause();
            else if (x >= 580f / 1280 && x < 725f / 1280) replay();
            else if (x >= 740f / 1280 && x < 940f / 1280) changeCue(-1);
            else if (x >= 955f / 1280) changeCue(1);
        }
    }

    private RenderFrame render(double interpolation)
    {
        _scroll = Math.Clamp(_scroll, 0, maximumScroll);
        var quads = new List<RenderQuad>();
        var labels = new List<RenderQuad>();
        // The movie goes underneath, then the background covers whatever it draws outside its box.
        var movieQuads = new List<RenderQuad>();
        rect(quads, 0, 0, 384, 720, new RenderColor(.07f, .09f, .13f, 1));
        text(labels, _title, 20, 24, 3, 68);
        text(labels, Path.GetFileName(_location.TrimEnd(Path.DirectorySeparatorChar)), 20, 66, 2, 29);
        for (var i = _scroll; i < Math.Min(_rows.Count, _scroll + VisibleRows); i++)
        {
            var y = 100 + (i - _scroll) * 26;
            if (i == _selected) rect(quads, 12, y, 360, 25, new RenderColor(.16f, .33f, .45f, 1));
            var row = _rows[i];
            var indent = Math.Min(row.Depth, 12) * 12;
            var prefix = row.Node.CanExpand ? row.Node.Expanded ? "- " : "> " : "  ";
            var label = prefix + row.Node.Label;
            text(labels, label, 20 + indent, y + 5, 2, (344 - indent) / 12);
        }
        if (_rows.Count > VisibleRows)
        {
            rect(quads, 364, 100, 14, VisibleRows * 26, new RenderColor(.15f, .19f, .25f, 1));
            rect(quads, 364, (int)thumbTop, 14, thumbHeight, new RenderColor(.35f, .6f, .7f, 1));
        }
        text(labels, $"{_selected + 1} / {_rows.Count} VISIBLE", 20, 608, 2, 29);
        text(labels, "ARROWS SELECT/AUTOPLAY  ENTER EXPAND", 20, 640, 1, 58);
        text(labels, "WHEEL SCROLL  DRAG SCROLLBAR/TIMELINE", 20, 662, 1, 58);
        text(labels, "BACKSPACE COLLAPSE  D/K CUE  Q/E FRAME", 20, 684, 1, 58);
        if (selectedNode is { } selected)
        {
            text(labels, selected.Label, 420, 25, 2, 69);
            var path = selected.Movie ?? Path.GetRelativePath(_location, selected.Path);
            text(labels, path, 420, 58, 1, 137);
        }
        if (_movie is { } movie)
        {
            var frame = LumenRenderFrameAdapter.Compose(movie.CreateRenderSnapshot(_paused ? 1 : (float)interpolation),
                RenderColor.Black, index => _movieTextures[checked((int)index)]);
            // Aspect-fit the movie inside the preview panel, in actual drawable pixels.
            var surface = _application.GetPixelSize();
            var aspect = frame.ContentAspectRatio ?? 16d / 9;
            float w = 850f / 1280, h = 450f / 720;
            var panelAspect = w * surface.Width / (h * surface.Height);
            if (panelAspect > aspect) w *= (float)(aspect / panelAspect);
            else h *= (float)(panelAspect / aspect);
            float left = 410f / 1280 + (850f / 1280 - w) / 2, top = 105f / 720 + (450f / 720 - h) / 2;
            RenderVertex fit(RenderVertex vertex) => vertex with { X = left + vertex.X * w, Y = top + vertex.Y * h };
            var background = new RenderColor(.025f, .03f, .05f, 1);
            int x0 = (int)(left * 1280), y0 = (int)(top * 720), x1 = (int)MathF.Ceiling((left + w) * 1280), y1 = (int)MathF.Ceiling((top + h) * 720);
            rect(movieQuads, 0, 0, 1280, y0, background);
            rect(movieQuads, 0, y1, 1280, 720 - y1, background);
            rect(movieQuads, 0, y0, x0, y1 - y0, background);
            rect(movieQuads, x1, y0, 1280 - x1, y1 - y0, background);
            movieQuads.InsertRange(0, frame.Quads.Select(quad => quad with
            {
                TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight),
                BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight)
            }));
            rect(quads, 420, 585, 820, 8, new RenderColor(.2f, .25f, .32f, 1));
            var progress = movie.FrameCount <= 1 ? 0 : movie.CurrentFrame / (float)(movie.FrameCount - 1);
            rect(quads, 420, 585, (int)(820 * progress), 8, new RenderColor(.25f, .7f, .85f, 1));
            rect(quads, 420 + (int)(820 * progress) - 3, 580, 6, 18, RenderColor.White);
            text(labels, $"FRAME {movie.CurrentFrame} / {movie.FrameCount - 1} - DRAG TIMELINE TO SEEK", 420, 559, 1, 137);
        }
        else
        {
            text(labels, _audio is null ? "SELECT A MOVIE OR AUDIO FILE" : "AUDIO PREVIEW", 430, 260, 3, 43);
            if (_audioPath is { } path)
            {
                text(labels, Path.GetFileName(path), 430, 310, 2, 67);
                text(labels, $"{_audio?.Info.SampleRate ?? 0} HZ  {_audio?.Info.Channels ?? 0} CHANNELS", 430, 345, 2, 67);
                if (Path.GetExtension(path).Equals(".nub", StringComparison.OrdinalIgnoreCase))
                {
                    text(labels, $"CUE {_cue} / DECODER STREAM {_cue + 1}", 430, 380, 2, 67);
                    text(labels, "TYPE CUE NUMBER THEN ENTER: " + _cueInput, 430, 415, 2, 67);
                }
            }
        }
        button(quads, labels, _paused ? "RESUME" : "PAUSE", 420, 145);
        button(quads, labels, "REPLAY", 580, 145);
        button(quads, labels, _movie is null ? "PREVIOUS CUE" : "PREVIOUS FRAME", 740, 200);
        button(quads, labels, _movie is null ? "NEXT CUE" : "NEXT FRAME", 955, 200);
        text(labels, _movie is null ? "SPACE PAUSE  R REPLAY  D/K CUE" : "SPACE PAUSE  R START  Q/E FRAME  M DEV  DFJK DRUM", 420, 665, 2, 69);
        var state = _paused ? "PAUSED - " : _audio?.Completed == true ? "FINISHED - " : "";
        text(labels, state + _status, 420, 695, 1, 137);
        quads.AddRange(labels);
        movieQuads.AddRange(quads);
        return new RenderFrame(new RenderColor(.025f, .03f, .05f, 1), movieQuads);
    }

    private void button(List<RenderQuad> quads, List<RenderQuad> labels, string label, int x, int width)
    {
        rect(quads, x, 620, width, 37, new RenderColor(.16f, .22f, .29f, 1));
        text(labels, label, x + 10, 631, 2, width / 12 - 1);
    }

    private void rect(List<RenderQuad> quads, int x, int y, int width, int height, RenderColor color) =>
        quads.Add(RenderQuad.FromRectangles(_white, new RenderRectangle(x / 1280f, y / 720f,
            width / 1280f, height / 720f), RenderRectangle.Full, color, RenderColor.Transparent));

    private void text(List<RenderQuad> quads, string value, int x, int y, int scale, int limit)
    {
        if (value.Length > limit) value = value[..Math.Max(0, limit - 3)] + "...";
        foreach (var character in value.ToUpperInvariant())
        {
            var index = (character is >= ' ' and <= '_' ? character : '?') - ' ';
            quads.Add(RenderQuad.FromRectangles(_glyphs,
                new RenderRectangle(x / 1280f, y / 720f, 5f * scale / 1280, 7f * scale / 720),
                new RenderRectangle(index % 16 * 6f / 96, index / 16 * 8f / 32, 5f / 96, 7f / 32),
                RenderColor.White, RenderColor.Transparent, RenderSampling.Nearest));
            x += 6 * scale;
        }
    }

    public void Dispose()
    {
        stopPlayback();
        _device?.Dispose();
        _application.ReleaseTexture(_glyphs);
        _application.ReleaseTexture(_white);
        _application.Dispose();
    }
}
