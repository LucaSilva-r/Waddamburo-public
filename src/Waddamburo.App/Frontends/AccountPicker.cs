using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Game.Scores;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Frontends;

/// <summary>
/// Waddamburo's "who's playing?": Guest and each stored TaikOnline account, with the account's avatar (downloaded once
/// into the avatars folder, as the home player setup keeps them) and name. Rims move, a centre hit picks; Escape plays as
/// a guest. ponytail: the left drum only; signing in a new account stays the home player setup's (Green's frontend).
/// </summary>
internal sealed class AccountPicker(SdlApplication application, string fontPath, AccountBook? book, string avatarDirectory,
    HttpClient? avatars) : IDisposable
{
    private const int Width = 560, RowHeight = 76, Top = 96, Margin = 16;
    private const int VisibleRows = 6;
    private static readonly (byte R, byte G, byte B) Panel = (24, 28, 44), Row = (44, 52, 80), Picked = (238, 92, 60);

    private readonly ConcurrentDictionary<string, RgbaTextSurface?> _avatars = new();
    private Action<ScoreProfile?>? _picked;
    private int _cursor;
    private string? _drawn;
    private uint _drawnScale;
    private RenderTextureId? _texture;

    public bool IsOpen => _picked is not null;

    private IReadOnlyList<ScoreAccount> accounts => book?.Accounts ?? [];

    /// <summary>Shows the list, the cursor on the default account (else Guest); <paramref name="picked"/> gets the choice (null: guest).</summary>
    public void Open(Action<ScoreProfile?> picked)
    {
        _picked = picked;
        _cursor = book?.DefaultBaid is { } baid && accounts.ToList().FindIndex(account => account.Baid == baid) is var at and >= 0 ? at + 1 : 0;
        foreach (var account in accounts)
            if (account.Avatar is { } url)
                load(url);
    }

    public void Close() => _picked = null;

    /// <summary>One tick: the left drum (D/K rims, F/J centres) and the arrows move and pick. True: it took the keys.</summary>
    public bool Tick(SdlKeyboardSnapshot keys)
    {
        if (_picked is not { } picked)
            return false;
        var count = accounts.Count + 1;
        if (keys.IsDown(SdlKeyboardKey.D) || keys.IsDown(SdlKeyboardKey.Left) || keys.IsDown(SdlKeyboardKey.Up))
            _cursor = (_cursor + count - 1) % count;
        if (keys.IsDown(SdlKeyboardKey.K) || keys.IsDown(SdlKeyboardKey.Right) || keys.IsDown(SdlKeyboardKey.Down))
            _cursor = (_cursor + 1) % count;
        if (keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J) || keys.IsDown(SdlKeyboardKey.Enter) || keys.IsDown(SdlKeyboardKey.Escape))
        {
            var profile = !keys.IsDown(SdlKeyboardKey.Escape) && _cursor > 0 ? accounts[_cursor - 1].Profile : null;
            Close();
            picked(profile);
        }
        return true;
    }

    public IEnumerable<RenderQuad> Quads()
    {
        if (!IsOpen)
            return [];
        var rows = Math.Min(accounts.Count + 1, VisibleRows);
        var height = Top - 40 + rows * RowHeight + Margin;
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 4);
        // Redrawn when the cursor, the accounts or an avatar change.
        var key = $"{_cursor}|{string.Join(',', accounts.Select(account => account.Baid))}|{_avatars.Count(pair => pair.Value is not null)}";
        if (key != _drawn || scale != _drawnScale)
        {
            if (_texture is { } old)
                application.ReleaseTexture(old);
            _texture = application.UploadRgba8((uint)(Width * scale), (uint)(height * scale), draw(rows, height, (int)scale));
            (_drawn, _drawnScale) = (key, scale);
        }
        return [RenderQuad.FromRectangles(_texture!.Value,
            new RenderRectangle((1280f - Width) / 2 / 1280f, (720f - height) / 2 / 720f, Width / 1280f, height / 720f),
            RenderRectangle.Full, RenderColor.White, RenderColor.Transparent)];
    }

    private byte[] draw(int rows, int height, int scale)
    {
        var canvas = new VectorCanvas(Width, height, scale, fontPath);
        canvas.RoundedRect(0, 0, Width, height, 18, Panel);
        canvas.Text(Strings.T("picker.title"), Width / 2f, 34, Width - 40, 34);
        // The window of rows around the cursor.
        var first = Math.Clamp(_cursor - VisibleRows / 2, 0, Math.Max(0, accounts.Count + 1 - VisibleRows));
        for (var row = 0; row < rows; row++)
        {
            var index = first + row;
            var y = Top - 40 + row * RowHeight + 8;
            canvas.RoundedRect(Margin, y, Width - 2 * Margin, RowHeight - 10, 12, index == _cursor ? Picked : Row);
            var name = index == 0 ? Strings.T("setup.guest") : accounts[index - 1].Profile.DisplayName ?? accounts[index - 1].Name;
            if (index > 0 && accounts[index - 1].Avatar is { } url && _avatars.TryGetValue(url, out var avatar) && avatar is not null)
                canvas.Image(avatar, null, Margin + 12 + 26, y + (RowHeight - 10) / 2f, 52, 52);
            canvas.Text(name, Margin + 84, y + (RowHeight - 10) / 2f, Width - 2 * Margin - 100, 30, anchorX: 0);
        }
        return canvas.Pixels;
    }

    // An avatar: from the avatars folder, else downloaded there first (the same file names as the home player setup).
    private void load(string url)
    {
        if (!_avatars.TryAdd(url, null))
            return;
        var file = Path.Combine(avatarDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".png");
        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(file))
                {
                    if (avatars is null)
                        return;
                    var bytes = await avatars.GetByteArrayAsync(url).ConfigureAwait(false);
                    Directory.CreateDirectory(avatarDirectory);
                    await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
                }
                _avatars[url] = SdlPng.Decode(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Warning ACCOUNT: avatar not loaded ({exception.Message}).");
            }
        });
    }

    public void Dispose()
    {
        if (_texture is { } texture)
            application.ReleaseTexture(texture);
    }
}
