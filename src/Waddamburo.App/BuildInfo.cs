using System.Reflection;

namespace Waddamburo.App;

/// <summary>What build this is.</summary>
internal static class BuildInfo
{
    /// <summary>An untagged development build (version 0.0.0-dev+commit): its developer options show.</summary>
    public static bool Dev { get; } = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Contains("-dev", StringComparison.Ordinal) == true;
}
