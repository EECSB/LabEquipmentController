using System.Linq;
using System.Reflection;

namespace LabEquipmentController;

/// <summary>
/// What an About box says about the project itself, read off the assembly rather than typed
/// into each one that shows it.
///
/// The repository and the licence are already declared once, in Core's project file, because
/// the NuGet listing needs them there. The SDK stamps the repository into the assembly as
/// metadata on its own — that is what PublishRepositoryUrl does — and the licence is stamped
/// beside it by an AssemblyMetadata item. Reading them back here is what stops the desktop's
/// About box, the web build's and the package listing from disagreeing: a repository that
/// moves is one line to change rather than a hunt for every place it was written out.
///
/// Empty rather than a guess when the metadata is not there. Both About boxes are held to the
/// rule that nothing on them may quietly go stale, and a fallback address hard-coded here
/// would be exactly the stale figure the rule exists to prevent.
/// </summary>
public static class AppInfo
{
    /// <summary>Where the source lives, or empty if the assembly carries no repository.</summary>
    public static string Repository { get; } = Metadata("RepositoryUrl");

    /// <summary>The licence, as the SPDX expression the package declares — "MIT".</summary>
    public static string License { get; } = Metadata("PackageLicenseExpression");

    private static string Metadata(string key)
        => typeof(AppInfo).Assembly
                          .GetCustomAttributes<AssemblyMetadataAttribute>()
                          .FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
