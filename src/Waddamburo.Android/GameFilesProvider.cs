using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Webkit;

namespace Waddamburo.Android;

/// <summary>
/// The app's folder (Android/data/org.waddamburo.game/files: Green, Custom Songs, Waddamburo with config.cfg and the
/// logs, the other releases) as a "Waddamburo" location in the system's Files app and its picker, so any file manager
/// that goes through them can open and change it: since Android 11 nothing but the app itself (and a PC over USB)
/// reaches Android/data otherwise. Document ids are "files" (the folder) and "files/relative/path".
/// </summary>
[ContentProvider([Authority], Exported = true, GrantUriPermissions = true, Permission = "android.permission.MANAGE_DOCUMENTS")]
[IntentFilter(["android.content.action.DOCUMENTS_PROVIDER"])]
public sealed class GameFilesProvider : DocumentsProvider
{
    private const string Authority = "org.waddamburo.game.files";
    private const string RootId = "waddamburo", Top = "files";

    private static readonly string[] RootColumns =
    [
        DocumentsContract.Root.ColumnRootId, DocumentsContract.Root.ColumnFlags, DocumentsContract.Root.ColumnTitle,
        DocumentsContract.Root.ColumnDocumentId, DocumentsContract.Root.ColumnIcon, DocumentsContract.Root.ColumnAvailableBytes,
    ];

    private static readonly string[] DocumentColumns =
    [
        DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnFlags,
        DocumentsContract.Document.ColumnSize, DocumentsContract.Document.ColumnLastModified,
    ];

    private string _base = "";

    /// <summary>The "Waddamburo" location, for the system's file screen (ACTION_VIEW, type vnd.android.document/root).</summary>
    public static global::Android.Net.Uri RootUri => DocumentsContract.BuildRootUri(Authority, RootId)!;

    public override bool OnCreate()
    {
        _base = Path.GetFullPath(Context!.GetExternalFilesDir(null)!.AbsolutePath);
        return true;
    }

    public override ICursor? QueryRoots(string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? RootColumns);
        var row = cursor.NewRow()!;
        row.Add(DocumentsContract.Root.ColumnRootId, RootId);
        row.Add(DocumentsContract.Root.ColumnFlags, (int)(DocumentRootFlags.SupportsCreate | DocumentRootFlags.LocalOnly
            | DocumentRootFlags.SupportsIsChild));
        row.Add(DocumentsContract.Root.ColumnTitle, "Waddamburo");
        row.Add(DocumentsContract.Root.ColumnDocumentId, Top);
        row.Add(DocumentsContract.Root.ColumnIcon, Resource.Drawable.icon);
        row.Add(DocumentsContract.Root.ColumnAvailableBytes, new StatFs(_base).AvailableBytes);
        return cursor;
    }

    public override ICursor? QueryDocument(string? documentId, string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        add(cursor, documentId ?? Top);
        return cursor;
    }

    public override ICursor? QueryChildDocuments(string? parentDocumentId, string[]? projection, string? sortOrder)
    {
        var parent = parentDocumentId ?? Top;
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        foreach (var entry in new DirectoryInfo(pathOf(parent)).EnumerateFileSystemInfos())
            add(cursor, child(parent, entry.Name));
        cursor.SetNotificationUri(Context!.ContentResolver, DocumentsContract.BuildChildDocumentsUri(Authority, parent));
        return cursor;
    }

    public override ParcelFileDescriptor? OpenDocument(string? documentId, string? mode, CancellationSignal? signal) =>
        ParcelFileDescriptor.Open(new Java.IO.File(pathOf(documentId ?? Top)), ParcelFileDescriptor.ParseMode(mode ?? "r"));

    public override string? CreateDocument(string? parentDocumentId, string? mimeType, string? displayName)
    {
        var parent = parentDocumentId ?? Top;
        var folder = pathOf(parent);
        var name = Path.GetFileName(displayName ?? "") is { Length: > 0 } given ? given : "New";
        // A name already there gets " (2)", " (3)"... before its extension, as the Files app expects.
        var (stem, extension) = (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        var directory = mimeType == DocumentsContract.Document.MimeTypeDir;
        if (directory)
            (stem, extension) = (name, "");
        for (var copy = 2; File.Exists(Path.Combine(folder, name)) || Directory.Exists(Path.Combine(folder, name)); copy++)
            name = $"{stem} ({copy}){extension}";
        if (directory)
            Directory.CreateDirectory(Path.Combine(folder, name));
        else
            File.Create(Path.Combine(folder, name)).Dispose();
        changed(parent);
        return child(parent, name);
    }

    public override void DeleteDocument(string? documentId)
    {
        if (documentId is null or Top)
            throw new Java.Lang.UnsupportedOperationException("The app's folder itself stays.");
        var path = pathOf(documentId);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
        changed(parentOf(documentId));
    }

    public override string? RenameDocument(string? documentId, string? displayName)
    {
        if (documentId is null or Top || Path.GetFileName(displayName ?? "") is not { Length: > 0 } name)
            throw new Java.Lang.UnsupportedOperationException("Not renamed.");
        var path = pathOf(documentId);
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (Directory.Exists(path))
            Directory.Move(path, renamed);
        else
            File.Move(path, renamed);
        changed(parentOf(documentId));
        return child(parentOf(documentId), name);
    }

    public override bool IsChildDocument(string? parentDocumentId, string? documentId) =>
        parentDocumentId is not null && documentId is not null && documentId.StartsWith(parentDocumentId + "/", StringComparison.Ordinal);

    public override string? GetDocumentType(string? documentId) => mimeOf(pathOf(documentId ?? Top));

    private void add(MatrixCursor cursor, string documentId)
    {
        var path = pathOf(documentId);
        var folder = Directory.Exists(path);
        FileSystemInfo info = folder ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"No such document: {documentId}");
        var flags = folder ? DocumentContractFlags.DirSupportsCreate : DocumentContractFlags.SupportsWrite;
        if (documentId != Top)
            flags |= DocumentContractFlags.SupportsDelete | DocumentContractFlags.SupportsRename;
        var row = cursor.NewRow()!;
        row.Add(DocumentsContract.Document.ColumnDocumentId, documentId);
        row.Add(DocumentsContract.Document.ColumnDisplayName, documentId == Top ? "Waddamburo" : info.Name);
        row.Add(DocumentsContract.Document.ColumnMimeType, mimeOf(path));
        row.Add(DocumentsContract.Document.ColumnFlags, (int)flags);
        row.Add(DocumentsContract.Document.ColumnSize, info is FileInfo file ? file.Length : null);
        row.Add(DocumentsContract.Document.ColumnLastModified, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
    }

    // The file a document id names, never outside the app's folder (an id with ".." in it is refused).
    private string pathOf(string documentId)
    {
        if (documentId != Top && !documentId.StartsWith(Top + "/", StringComparison.Ordinal))
            throw new FileNotFoundException($"No such document: {documentId}");
        var path = Path.GetFullPath(Path.Combine(_base, documentId[Top.Length..].TrimStart('/')));
        if (path != _base && !path.StartsWith(_base + "/", StringComparison.Ordinal))
            throw new FileNotFoundException($"No such document: {documentId}");
        return path;
    }

    private static string child(string parent, string name) => parent + "/" + name;

    private static string parentOf(string documentId) => documentId[..documentId.LastIndexOf('/')];

    private static string mimeOf(string path) => Directory.Exists(path) ? DocumentsContract.Document.MimeTypeDir
        : MimeTypeMap.Singleton?.GetMimeTypeFromExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
            ?? "application/octet-stream";

    private void changed(string parent) =>
        Context!.ContentResolver!.NotifyChange(DocumentsContract.BuildChildDocumentsUri(Authority, parent)!, null);
}
