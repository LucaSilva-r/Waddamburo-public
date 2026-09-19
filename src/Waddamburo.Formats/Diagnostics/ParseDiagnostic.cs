using System.Collections.Immutable;

namespace Waddamburo.Formats.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum EvidenceStatus
{
    Confirmed,
    CorpusValidatedInference,
    Candidate,
    Unknown,
}

public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    long Offset,
    int? RecordIndex,
    EvidenceStatus Evidence,
    string Message);

public sealed record ParseResult<T>(T Value, ImmutableArray<ParseDiagnostic> Diagnostics);
