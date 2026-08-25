using System;
using System.IO;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The web build serves the application's own icon, not a picture of one.
///
/// There is one piece of artwork — app.ico — and two extractions of it in the tree:
/// Core/icon.png, which is the 128×128 entry lifted out for the NuGet package, and the web
/// client's wwwroot/app-icon.png, which is that same file where Blazor's static-asset
/// pipeline can see it. It has to be a copy: static web assets are discovered by their
/// physical location under wwwroot, and a Content item linked in from elsewhere is silently
/// not served — tried, and it 404s.
///
/// So the copy is unavoidable and the drift is not. Before this, the web drew its own
/// approximation of the icon — a wave glyph on a dark square — and it looked nothing like
/// the real thing for months without anybody noticing, because nothing was comparing them.
/// Now something is.
/// </summary>
public class AppIconTests
{
    [Fact]
    public void The_web_build_serves_the_same_icon_the_desktop_app_uses()
    {
        string? root = FindRepositoryRoot();
        if (root == null) return;   // a published test drop has no source tree to compare against

        string source = Path.Combine(root, "Core", "icon.png");
        string web = Path.Combine(root, "Web", "LabEquipmentController.Web.Client", "wwwroot", "app-icon.png");

        Assert.True(File.Exists(source), source + " is missing — it is the icon everything else copies.");
        Assert.True(File.Exists(web), web + " is missing, so the web build has no icon to serve.");

        byte[] a = File.ReadAllBytes(source);
        byte[] b = File.ReadAllBytes(web);

        Assert.True(a.AsSpan().SequenceEqual(b),
            $"The web build's icon has drifted from the application's. Core/icon.png is {a.Length:N0} bytes "
          + $"and wwwroot/app-icon.png is {b.Length:N0}. Copy the first over the second; if the artwork itself "
          + "changed, re-extract the 128×128 entry from app.ico into Core/icon.png first.");
    }

    /// <summary>The tree this test is running out of, or null when there is not one.</summary>
    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "app.ico"))) return dir.FullName;
        return null;
    }
}
