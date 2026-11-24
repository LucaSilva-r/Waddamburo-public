using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl;

/// <summary>
/// Switch: a home-menu forwarder for this NRO, made on the console (platforms/switch/shim/forwarder.c) from the
/// console's own keys, which Lockpick_RCM (started from hekate) writes to /switch/prod.keys. The NSP is installed with
/// DBI, Tinfoil or Goldleaf and needs sigpatches, like every homebrew forwarder.
/// </summary>
public static partial class SwitchForwarder
{
    public const string KeysPath = "/switch/prod.keys";
    public const string NspPath = "/switch/taiko/Waddamburo.nsp";

    public enum Result { Made, NoKeys, KeysIncomplete, NroUnreadable, WriteFailed }

    /// <summary>The last attempt's result in this run, or null before one.</summary>
    public static Result? Last { get; private set; }

    /// <summary>Writes <see cref="NspPath"/>; the result also lands in <see cref="Last"/>.</summary>
    public static Result Make()
    {
        var result = !File.Exists(KeysPath) ? Result.NoKeys : nxfwd_make(KeysPath, NspPath) switch
        {
            0 => Result.Made,
            1 => Result.NoKeys,
            2 => Result.KeysIncomplete,
            3 => Result.NroUnreadable,
            _ => Result.WriteFailed,
        };
        Last = result;
        return result;
    }

    [LibraryImport("waddamburo_switch", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int nxfwd_make(string keysPath, string nspPath);
}
