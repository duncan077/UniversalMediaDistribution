using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Desktop.Web;

public sealed partial class DesktopWebChallengeSolver
{
    private static async Task<ApiContentVideoSource?> ShowLinuxVideoChallengeAsync(
        ApiContentVideoSource source, CancellationToken cancellationToken)
    {
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closing.CancelAfter(TimeSpan.FromMinutes(2));
        using var browser = CreateLinuxDialog($"Video check — {source.PlaybackUri.IdnHost}");
        var instructions = new TextBlock
        {
            Text = VideoChallengeCapture.Instructions,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var useNative = new Button { Content = "Use native player", IsEnabled = false };
        var keepWeb = new Button { Content = "Keep web player" };
        // GTK owns the browser window. Keep the confirmation in trusted app UI,
        // rather than injecting a button a remote page could activate itself.
        var actions = new Window
        {
            Title = "Video playback",
            Width = 540,
            Height = 180,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    instructions,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { useNative, keepWeb } }
                }
            }
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ApiContentVideoSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApiContentVideoSource? candidate = null;
        var probing = false;
        browser.AdapterCreated += (_, _) => ready.TrySetResult();
        browser.Closing += (_, _) => closing.Cancel();
        actions.Closed += (_, _) => closing.Cancel();
        keepWeb.Click += (_, _) => closing.Cancel();
        browser.NavigationStarted += (_, args) =>
        {
            candidate = null;
            useNative.IsEnabled = false;
            if (args.Request is { } target && target.AbsoluteUri != "about:blank" &&
                !VideoChallengeCapture.AllowsNavigation(source.PlaybackUri, target)) args.Cancel = true;
            Mark($"linux:video navigation host={args.Request?.Host}; cancelled={args.Cancel}");
        };
        browser.NavigationCompleted += (_, args) => Mark($"linux:video navigation-completed success={args.IsSuccess}");
        useNative.Click += async (_, _) =>
        {
            if (candidate is not { } captured || probing) return;
            probing = true;
            useNative.IsEnabled = false;
            instructions.Text = "Checking whether this video can play outside the browser…";
            try
            {
                if (await VideoChallengeMediaProbe.VerifyAsync(captured, closing.Token)) completion.TrySetResult(captured);
                else instructions.Text = "This video still needs its browser session. Choose Keep web player to continue there.";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { instructions.Text = "The native check failed. You can keep the web player."; }
            finally { probing = false; }
        };
        try
        {
            actions.Show();
            browser.Show();
            await ready.Task.WaitAsync(closing.Token);
            browser.Navigate(source.PlaybackUri);
            while (!completion.Task.IsCompleted)
            {
                await Task.Delay(PollInterval, closing.Token);
                try
                {
                    // Reinstall after redirects. The observer also checks
                    // performance entries, recovering manifests fetched before
                    // this first script evaluation. Cross-origin frames remain
                    // browser-only when their media is not accessible here.
                    await browser.InvokeScript(VideoChallengeCapture.Script).WaitAsync(closing.Token);
                    var report = LinuxWebChallengeResult.DecodeString(await browser.InvokeScript(
                        "window.__umdVideoChallengeResult || null").WaitAsync(closing.Token));
                    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UMD_WEB_CHALLENGE_TRACE")))
                    {
                        var state = await browser.InvokeScript("JSON.stringify({host:location.hostname,title:document.title,videos:Array.from(document.querySelectorAll('video')).map(v=>({paused:v.paused,ready:v.readyState,time:v.currentTime,width:v.videoWidth,error:v.error?v.error.code:0}))})").WaitAsync(closing.Token);
                        Mark($"linux:video reportLength={report?.Length ?? 0}; state={state}");
                    }
                    if (!probing && VideoChallengeCapture.Parse(report, source) is { } captured &&
                        captured.Referrer is { } referrer && VideoChallengeCapture.AllowsNavigation(source.PlaybackUri, referrer))
                    {
                        candidate = captured;
                        useNative.IsEnabled = true;
                    }
                }
                catch (InvalidOperationException) { } // Navigation is replacing the page.
            }
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            closing.Cancel();
            browser.Stop();
            browser.Close();
            actions.Close();
        }
    }
}
