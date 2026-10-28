namespace Waddamburo.App.Cli;

internal sealed class MissingGameDataException(string root, IReadOnlyList<string> missing)
    : IOException(Strings.T("startup.missing_data", root, string.Join(Environment.NewLine, missing)))
{
    public string Root { get; } = root;
    public IReadOnlyList<string> Missing { get; } = missing;
}
