using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Desktop.Web;

public sealed partial class DesktopWebChallengeSolver
{
    public async ValueTask<ApiContentVideoSource?> ResolveVideoAsync(ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;
        await SolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => OperatingSystem.IsLinux()
                ? ShowLinuxVideoChallengeAsync(source, cancellationToken)
                : ShowVideoChallengeAsync(source, cancellationToken));
        }
        finally { SolveGate.Release(); }
    }

    private static async Task<ApiContentVideoSource?> ShowVideoChallengeAsync(ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closing.CancelAfter(TimeSpan.FromMinutes(2));
        var token = closing.Token;
        var browser = new NativeWebView { Background = Brushes.Black };
        var text = new TextBlock { Text = VideoChallengeCapture.Instructions, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        var useNative = new Button { Content = "Use native player", IsEnabled = false, Margin = new Thickness(8) };
        var cancel = new Button { Content = "Keep web player", Margin = new Thickness(8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { useNative, cancel } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(text); layout.Children.Add(browser); layout.Children.Add(buttons);
        Grid.SetRow(browser, 1); Grid.SetRow(buttons, 2);
        var window = new Window
        {
            Title = $"Video check — {source.PlaybackUri.IdnHost}",
            Width = 1000,
            Height = 760,
            Content = layout,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var completion = new TaskCompletionSource<ApiContentVideoSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApiContentVideoSource? candidate = null;
        var probing = false;
        window.Closed += (_, _) => { completion.TrySetResult(null); closing.Cancel(); };
        cancel.Click += (_, _) => window.Close();
        browser.NewWindowRequested += (_, args) => args.Handled = true;
        browser.NavigationStarted += (_, args) =>
        {
            if (args.Request is { } target && target.AbsoluteUri != "about:blank" &&
                !VideoChallengeCapture.AllowsNavigation(source.PlaybackUri, target)) args.Cancel = true;
        };
        browser.WebMessageReceived += (_, args) =>
        {
            if (VideoChallengeCapture.Parse(args.Body, source) is not { } captured || probing) return;
            candidate = captured;
            useNative.IsEnabled = true;
        };
        useNative.Click += async (_, _) =>
        {
            if (candidate is not { } captured || probing) return;
            probing = true;
            useNative.IsEnabled = false;
            text.Text = "Checking whether this video can play outside the browser…";
            try
            {
                if (await VideoChallengeMediaProbe.VerifyAsync(captured, token)) completion.TrySetResult(captured);
                else text.Text = "This video still needs its browser session. Choose Keep web player to continue there.";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { text.Text = "The native check failed. You can keep the web player."; }
            finally { probing = false; }
        };
        ICoreWebView2? core = null;
        try
        {
            // Attach before accessing the native adapter, just like the existing solver.
            window.Show();
            var deadline = Environment.TickCount64 + 10000;
            while (core is null && Environment.TickCount64 < deadline)
            {
                await Task.Delay(250, token);
                core = TryResolveCore(browser);
            }
            if (core is null) return null;
            core.AddScriptToExecuteOnDocumentCreated(VideoChallengeCapture.Script, null);
            core.Navigate(source.PlaybackUri.AbsoluteUri);
            return await completion.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            try { core?.Navigate("about:blank"); } catch { }
            core = null;
            window.Close();
        }
    }
}
