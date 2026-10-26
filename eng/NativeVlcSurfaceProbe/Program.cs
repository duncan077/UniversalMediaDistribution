using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using UniversalMediaDistribution.Playback.LibVLC;
using UniversalMediaDistribution.UI.Localization;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe requires Windows child windows and libVLC.");
            return 2;
        }

        AppBuilder.Configure<Avalonia.Application>().UsePlatformDetect().SetupWithoutStarting();
        Core.Initialize();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                using var vlc = new LibVLC("--no-audio", "--no-video-title-show");
                if (args.Contains("--casting")) await CheckCastingDiscoveryAsync(vlc);
                using var stockPlayer = new MediaPlayer(vlc);
                using var fixedPlayer = new MediaPlayer(vlc);
                var stockReattached = await CheckAsync(new VideoView { MediaPlayer = stockPlayer }, stockPlayer);
                Console.WriteLine($"Stock VideoView rebound after detach: {stockReattached}");
                var fixedReattached = await CheckAsync(new LibVlcVideoView { MediaPlayer = fixedPlayer }, fixedPlayer);
                if (!fixedReattached)
                    throw new InvalidOperationException("The corrected video view did not rebind its child HWND.");
                Console.WriteLine("PASS: corrected view binds a real child HWND after repeated detach/reattach.");
                await CheckPlayerViewAsync(vlc, args.Contains("--casting"));
                result = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return result;
    }

    private static async Task<bool> CheckAsync(VideoView view, MediaPlayer player)
    {
        // Off-screen and not in the taskbar; no user windows or application
        // catalog state are touched. This is a real Win32 host, not headless.
        var window = new Window
        {
            Width = 320,
            Height = 180,
            Position = new PixelPoint(-32000, -32000),
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = view
        };
        try
        {
            window.Show();
            await WaitForLayoutAsync();
            Console.WriteLine($"{view.GetType().Name} initial handle: {player.Hwnd != IntPtr.Zero}");
            for (var cycle = 0; cycle < 3; cycle++)
            {
                window.Content = null;
                await WaitForLayoutAsync();
                if (player.Hwnd != IntPtr.Zero)
                    throw new InvalidOperationException("VLC retained a destroyed child HWND.");
                window.Content = view;
                await WaitForLayoutAsync();
                if (player.Hwnd == IntPtr.Zero)
                    return false;
                var owner = window.TryGetPlatformHandle()!.Handle;
                if (!IsWindow(player.Hwnd) || !IsChild(owner, player.Hwnd))
                    throw new InvalidOperationException("VLC is not bound to a live child of the application window.");
            }
            return true;
        }
        finally
        {
            window.Close();
            view.MediaPlayer = null;
        }
    }

    private static async Task WaitForLayoutAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        await Task.Delay(100);
    }

    private static async Task CheckCastingDiscoveryAsync(LibVLC vlc)
    {
        if (!vlc.RendererList.Any(renderer => renderer.Name == "microdns_renderer"))
            throw new InvalidOperationException("The bundled VLC has no Chromecast discoverer.");
        using var discovery = new RendererDiscoverer(vlc);
        // Exercise registration and lifetime with the actual native library.
        EventHandler<RendererDiscovererItemAddedEventArgs> added = (_, args) => args.RendererItem.Dispose();
        EventHandler<RendererDiscovererItemDeletedEventArgs> deleted = (_, args) => args.RendererItem.Dispose();
        discovery.ItemAdded += added;
        discovery.ItemDeleted += deleted;
        try
        {
            if (!discovery.Start()) throw new InvalidOperationException("Chromecast discovery did not start.");
            await Task.Delay(250);
        }
        finally
        {
            discovery.Stop();
            discovery.ItemAdded -= added;
            discovery.ItemDeleted -= deleted;
        }
        using var player = new MediaPlayer(vlc);
        if (!player.SetRenderer(null)) throw new InvalidOperationException("Could not select local output.");
        Console.WriteLine("PASS: bundled Chromecast discovery starts/stops, events attach, and local output is selectable.");
    }

    private static async Task CheckPlayerViewAsync(LibVLC vlc, bool checkCasting)
    {
        using var player = new MediaPlayer(vlc);
        using var view = new LibVlcPlayerView(player);
        var window = new Window
        {
            Width = 640, Height = 360,
            Position = new PixelPoint(-32000, -32000),
            ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            for (var cycle = 0; cycle < 3; cycle++)
            {
                window.Content = view;
                view.SetMedia("Surface probe", false, ["VOE"], 0);
                if (!await view.WaitForVideoSurfaceAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
                    throw new InvalidOperationException("The full player surface did not become ready.");
                if (!IsChild(window.TryGetPlatformHandle()!.Handle, player.Hwnd))
                    throw new InvalidOperationException("The full player is not using its own child HWND.");
                if (checkCasting && cycle == 0)
                {
                    var popup = view.GetLogicalDescendants().OfType<Popup>().Single();
                    var controls = popup.Child ?? throw new InvalidOperationException("The player controls are missing.");
                    var buttons = controls.GetLogicalDescendants().OfType<Button>().ToArray();
                    var cast = buttons.Single(button => AutomationProperties.GetName(button) == UiTextCatalog.Get("CastAction"));
                    if (!cast.IsEffectivelyVisible) throw new InvalidOperationException("Cast button is hidden on Windows.");
                    if (cast.Content is not PathIcon { Data: not null })
                        throw new InvalidOperationException("Cast button has no vector cast icon.");
                    var fullscreen = buttons.Single(button => Equals(button.Content, "⛶"));
                    if (cast.Parent is not Grid || !ReferenceEquals(cast.Parent, fullscreen.Parent) ||
                        Grid.GetColumn(cast) + 1 != Grid.GetColumn(fullscreen))
                        throw new InvalidOperationException("Cast button is not beside fullscreen in the playback controls row.");
                    if (!Equals(ToolTip.GetTip(cast), UiTextCatalog.Get("CastAction")))
                        throw new InvalidOperationException("Cast button is missing its localized tooltip.");
                    var discoveryRequested = false;
                    view.CastDiscoveryRequested += (_, _) => discoveryRequested = true;
                    cast.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!discoveryRequested) throw new InvalidOperationException("Cast picker did not request discovery.");
                    view.SetCastDevices([new(null, "This PC"), new("probe", "Test receiver")], null);
                    string? selection = null;
                    view.CastSelectionRequested += (_, id) => selection = id;
                    var receiver = controls.GetLogicalDescendants().OfType<Button>()
                        .Single(button => Equals(button.Content, "Test receiver"));
                    receiver.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (selection != "probe") throw new InvalidOperationException("The picker did not select the receiver.");
                    view.SetCastBusy(true);
                    if (receiver.IsEffectivelyEnabled) throw new InvalidOperationException("The picker allows concurrent switches.");
                    view.SetCastBusy(false);
                    view.SetCastMessage("Cast error probe", error: true);
                    await WaitForLayoutAsync();
                    var error = controls.GetLogicalDescendants().OfType<TextBlock>()
                        .Single(text => text.Text == "Cast error probe");
                    if (!error.IsEffectivelyVisible) throw new InvalidOperationException("Cast errors are not visible in the player.");
                    view.ResetCasting();
                    Console.WriteLine("PASS: Windows cast picker opens, selects devices, blocks concurrent switches, and displays errors.");
                }
                view.SuspendPlayerAccess();
                view.Reset();
                window.Content = new Border();
                await WaitForLayoutAsync();
                if (player.Hwnd != IntPtr.Zero)
                    throw new InvalidOperationException("The full player retained a detached HWND.");
            }
            Console.WriteLine("PASS: full player readiness gate succeeds across three web-placeholder/native switches.");
        }
        finally
        {
            window.Close();
        }
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr handle);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(IntPtr parent, IntPtr child);
}
