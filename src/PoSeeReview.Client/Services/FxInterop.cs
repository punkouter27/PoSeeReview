using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace PoSeeReview.Client.Services;

/// <summary>
/// The hot or bulky half of the fx surface over <c>[JSImport]</c> instead of <c>IJSRuntime</c>:
/// arguments cross as plain numbers with no JSON, and <see cref="FillStats"/> has JS write the
/// frame-budget readout straight into a .NET <see cref="Span{T}"/>. Bound to the module instance
/// index.html already loaded (same URL), so nothing in fx.js initialises twice.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class FxInterop
{
    private const string Module = "fx";
    private static Task? _import;

    /// <summary>Order of the values <see cref="FillStats"/> writes; mirrors fx.js fillStats.</summary>
    public const int StatCount = 10;

    /// <summary>True once the module is bound; until then callers use the IJSRuntime path.</summary>
    public static bool Ready { get; private set; }

    /// <summary>Starts the bind once. Never throws: a failed bind leaves <see cref="Ready"/> false and may be retried.</summary>
    public static Task EnsureAsync() =>
        _import ??= ImportAsync();

    private static async Task ImportAsync()
    {
        try
        {
            await JSHost.ImportAsync(Module, "/js/fx.js");
            Ready = true;
        }
        catch (System.Runtime.InteropServices.JavaScript.JSException)
        {
            _import = null;
        }
    }

    [JSImport("tapAt", Module)]
    public static partial void TapAt(double clientX);

    [JSImport("tapCached", Module)]
    public static partial void TapCached(double clientX, bool cached);

    [JSImport("fillStats", Module)]
    public static partial int FillStats([JSMarshalAs<JSType.MemoryView>] Span<double> buffer);
}
