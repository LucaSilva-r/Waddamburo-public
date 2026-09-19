namespace Waddamburo.Lumen.Runtime;

public enum LumenRuntimeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record LumenRuntimeDiagnostic(
    LumenRuntimeDiagnosticSeverity Severity,
    string Code,
    uint CharacterId,
    int Frame,
    string Message);
