using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.Stock;

namespace Waddamburo.App.Sync;

internal sealed class NijiiroSync(NijiiroKeys keys)
{
    public const string Kind = "nijiiro";
    public const string Part = "nijiiro";

    public NijiiroKeys Keys { get; } = keys;

    public static bool Includes(string path) => path.StartsWith(Part + "/datatable/", StringComparison.Ordinal)
        || path.StartsWith(Part + "/fumen/", StringComparison.Ordinal) || IsBank(path);

    public static bool IsBank(string path) => path.StartsWith(Part + "/sound/song_", StringComparison.Ordinal)
        && path.EndsWith(".nus3bank", StringComparison.OrdinalIgnoreCase);

    public static string ReceiverPath(string path) => IsBank(path) ? Path.ChangeExtension(path, ".ogg") : path;

    public static IEnumerable<(string Path, long Size, long Time, string Full)> PrepareAudio(
        (string Path, long Size, long Time, string Full) bank)
    {
        var opus = VgmstreamCli.TryPrepareOpus(bank.Full)
            ?? throw new IOException(Strings.T("sync.nijiiro.audio_failed", Path.GetFileName(bank.Path)));
        var receiverPath = ReceiverPath(bank.Path);
        yield return (receiverPath, new FileInfo(opus).Length, bank.Time, opus);
        if (NijiiroCatalogProvider.BankPreviewStart(bank.Full) is { } preview)
        {
            var cue = opus + ".preview";
            if (!File.Exists(cue))
            {
                var partial = cue + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    File.WriteAllText(partial, ((int)preview.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                    File.Move(partial, cue, overwrite: true);
                }
                finally { File.Delete(partial); }
            }
            yield return (receiverPath + ".preview", new FileInfo(cue).Length, bank.Time, cue);
        }
    }

    public static string WrapKeys(NijiiroKeys values, string publicKey)
    {
        using var rsa = RSA.Create();
        var encoded = Convert.FromBase64String(publicKey);
        rsa.ImportSubjectPublicKeyInfo(encoded, out var consumed);
        if (consumed != encoded.Length || rsa.KeySize != 2048)
            throw new CryptographicException("Invalid sync key.");
        var data = Encoding.ASCII.GetBytes(serialize(values));
        try { return Convert.ToBase64String(rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static string PublicKey(RSA rsa) => Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

    public static NijiiroKeys UnwrapKeys(RSA rsa, string payload)
    {
        var data = rsa.Decrypt(Convert.FromBase64String(payload), RSAEncryptionPadding.OaepSHA256);
        try { return parse(Encoding.ASCII.GetString(data)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static NijiiroKeys[] ReadConfig(string folder) => [.. configLines(folder).Select(line => parse(line.Replace('\t', '\n')))];

    private static string[] configLines(string folder)
    {
        var path = configPath(folder);
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    public static void WriteConfig(string folder, NijiiroKeys keys)
    {
        var path = configPath(folder);
        var line = serialize(keys).Replace('\n', '\t');
        var previous = configLines(folder);
        if (previous.FirstOrDefault() == line)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new StreamWriter(new FileStream(temporary, options)))
            {
                file.WriteLine(line);
                foreach (var old in previous.Where(old => old != line))
                    file.WriteLine(old);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string configPath(string folder) => Path.Combine(SyncWire.Folder, "nijiiro",
        Path.GetFileName(Path.GetDirectoryName(folder))!, Path.GetFileName(folder) + ".cfg");

    private static string serialize(NijiiroKeys keys) =>
        $"{(keys.Datatable is { } datatable ? Convert.ToHexString(datatable) : "")}\n"
        + (keys.Fumen is { } fumen ? Convert.ToHexString(fumen) : "");

    private static NijiiroKeys parse(string payload)
    {
        if (payload.Split('\n') is not [var tables, var charts]
            || (tables.Length > 0 && (tables.Length != 64 || !tables.All(char.IsAsciiHexDigit)))
            || (charts.Length > 0 && (charts.Length != 64 || !charts.All(char.IsAsciiHexDigit))))
            throw new InvalidDataException("Invalid Nijiiro key configuration.");
        return NijiiroKeys.Parse(tables, charts);
    }
}
