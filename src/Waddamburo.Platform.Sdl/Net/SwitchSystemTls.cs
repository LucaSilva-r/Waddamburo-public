using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl.Net;

/// <summary>
/// The Nintendo Switch's own TLS (platforms/switch/shim/tls.c: the console's trusted CAs, host name check) around a
/// connected socket, as a stream: .NET's TLS needs OpenSSL, which the Switch build lacks. Waddamburo.Services sends
/// https as plain HTTP over it (its SwitchTls). Only the Switch build links these functions.
/// </summary>
public static partial class SwitchSystemTls
{
    /// <summary>The connected socket inside the console's TLS; <paramref name="insecure"/>: no certificate checks.</summary>
    public static Stream Wrap(Socket socket, string host, bool insecure)
    {
        var connection = nxtls_open((int)socket.Handle, host, insecure ? 0 : 1, out var error);
        if (connection == 0)
        {
            socket.Dispose();
            throw new IOException($"Secure connection to {host} failed (0x{error:X}).");
        }
        return new TlsStream(connection, socket);
    }

    private sealed class TlsStream(nint connection, Socket socket) : Stream
    {
        private int _closed;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override unsafe int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            fixed (byte* pointer = buffer)
            {
                var read = nxtls_read(connection, pointer, buffer.Length);
                return read >= 0 ? read : throw new IOException("The secure connection failed or was closed.");
            }
        }

        // The service blocks (in short sleeps): off the caller's thread.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => Read(buffer.Span), cancellationToken));

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override unsafe void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                return;
            fixed (byte* pointer = buffer)
                if (nxtls_write(connection, pointer, buffer.Length) != 0)
                    throw new IOException("The secure connection failed or was closed.");
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Write(buffer, offset, count), cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => Write(buffer.Span), cancellationToken));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                nxtls_close(connection);
                socket.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [LibraryImport("waddamburo_switch", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint nxtls_open(int socket, string host, int verify, out uint error);

    [LibraryImport("waddamburo_switch")]
    private static unsafe partial int nxtls_read(nint connection, byte* buffer, int size);

    [LibraryImport("waddamburo_switch")]
    private static unsafe partial int nxtls_write(nint connection, byte* buffer, int size);

    [LibraryImport("waddamburo_switch")]
    private static partial void nxtls_close(nint connection);
}
