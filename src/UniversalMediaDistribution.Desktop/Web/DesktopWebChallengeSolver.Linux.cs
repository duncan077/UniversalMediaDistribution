using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Desktop.Web;

public sealed partial class DesktopWebChallengeSolver
{
    private static async Task<WebChallengeClearance?> SolveLinuxAsync(Uri endpoint, CancellationToken cancellationToken) =>
        (await LoadLinuxAsync(endpoint, clearanceRequired: true, cancellationToken))?.Clearance;

    private static async Task<WebChallengeDocument?> FetchLinuxAsync(Uri endpoint, CancellationToken cancellationToken) =>
        (await LoadLinuxAsync(endpoint, clearanceRequired: false, cancellationToken))?.Document;

    private static async Task<(WebChallengeDocument Document, WebChallengeClearance? Clearance)?> LoadLinuxAsync(
        Uri endpoint, bool clearanceRequired, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(DocumentTimeout);
        using var browser = CreateLinuxDialog($"Website check — {endpoint.IdnHost}");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.AdapterCreated += (_, _) => ready.TrySetResult();
        browser.Closing += (_, _) => attempt.Cancel();
        browser.NavigationStarted += (_, args) =>
        {
            if (args.Request is { } target && target.AbsoluteUri != "about:blank" &&
                !LinuxWebChallengeResult.AllowsNavigation(endpoint, target)) args.Cancel = true;
        };
        try
        {
            browser.Show();
            await ready.Task.WaitAsync(attempt.Token);
            Mark("linux:adapter-ready");
            browser.Navigate(endpoint);
            while (true)
            {
                await Task.Delay(PollInterval, attempt.Token);
                LinuxWebChallengeResult? result;
                try
                {
                    var snapshot = await browser.InvokeScript(LinuxWebChallengeResult.SnapshotScript).WaitAsync(attempt.Token);
                    result = LinuxWebChallengeResult.Parse(snapshot, endpoint);
                    Mark($"linux:snapshot length={snapshot?.Length ?? 0}; parsed={result is not null}");
                }
                catch (InvalidOperationException) { continue; } // A redirect replaced the document.
                if (result is null) continue;
                WebChallengeClearance? clearance = null;
                if (clearanceRequired)
                {
                    var jar = browser.TryGetCookieManager() is { } cookies
                        ? await cookies.GetCookiesAsync().WaitAsync(attempt.Token)
                        : await WebKitGtkCookies.ReadAsync(browser, endpoint, attempt.Token).WaitAsync(attempt.Token);
                    Mark($"linux:cookies count={jar.Count}");
                    clearance = result.GetClearance(endpoint, jar, DateTime.UtcNow);
                }
                if (clearanceRequired ? clearance is not null : result.HasDocument)
                    return (result.Document, clearance);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            browser.Stop();
            browser.Close();
        }
    }

    private static NativeWebDialog CreateLinuxDialog(string title)
    {
        // 12.0.1's embedded view requires WPE on some distributions; its GTK
        // dialog works with the WebKitGTK packages available on Debian/Ubuntu.
        var adapter = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk);
        if (!adapter.IsInstalled)
            throw new NotSupportedException($"Website checks require WebKitGTK 4.1 (libwebkit2gtk-4.1-0). {adapter.UnavailableReason}");
        var browser = new NativeWebDialog { Title = title, CanUserResize = true };
        browser.Resize(1000, 760);
        browser.NewWindowRequested += (_, args) => args.Handled = true;
        return browser;
    }
}
