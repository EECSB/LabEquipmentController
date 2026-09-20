using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// What one of WindowDialog's windows takes with it when Open in a tab moves it into a browser tab
/// of its own, and brings back when that tab is closed: the multi-instrument editor's script and
/// picks, the library's catalog and filter, the reference's language. Each window packs its own.
/// </summary>
/// <remarks>
/// Kept rather than sent, in localStorage under the address of the page the window moves to
/// (<c>lec.windows.carry</c>, in index.html). A tab that is being closed cannot send anything, so
/// the tab keeps what it holds current as it changes, and the window takes home whatever was kept
/// last. A browser that refuses storage moves the window without it.
/// </remarks>
public static class WindowCarry
{
    /// <summary>
    /// Whether this page is a window that moved into a tab, rather than a page opened any other
    /// way. Open in a tab says so in the address, where a Shift-click's separate window finds it
    /// too; opened any other way, a page is a new window.
    /// </summary>
    public static bool Moved(NavigationManager nav)
        => nav.ToBaseRelativePath(nav.Uri).Contains("moved=1", StringComparison.Ordinal);

    /// <summary>Leave what a window holds where its tab, or its window, will find it.</summary>
    public static ValueTask KeepAsync<T>(IJSRuntime js, string route, T what)
        => js.InvokeVoidAsync("lec.windows.carry", route, JsonSerializer.Serialize(what));

    /// <summary>
    /// What was kept, or null. Home from a tab that has closed, it is taken out of storage: what
    /// the tab held is the window's now. A tab leaves it there and keeps it current, which also
    /// means a reload of the tab keeps it.
    /// </summary>
    public static async Task<T?> TakeAsync<T>(IJSRuntime js, string route, bool home) where T : class
    {
        string? packed = await js.InvokeAsync<string?>("lec.windows.carried", route);
        if (home) await js.InvokeVoidAsync("lec.windows.drop", route);
        if (packed is null) return null;

        try { return JsonSerializer.Deserialize<T>(packed); }
        catch (JsonException) { return null; }   // not something this build wrote
    }
}
