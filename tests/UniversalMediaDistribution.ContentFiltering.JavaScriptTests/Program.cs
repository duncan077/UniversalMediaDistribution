using System.Diagnostics;
using System.Text.Json;
using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Injection;
using UniversalMediaDistribution.UI.Playback;

// Execute the actual generated payloads rather than checking for substrings.
// Node is needed only for this standalone suite, not for the .NET test suites.
var start = new ProcessStartInfo("node")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true
};
start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runtime.cjs"));
using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Node.js.");
await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
{
    cosmetic = InjectionPayloadBuilder.Build(new CosmeticPayload([".advertisement"], [], [], [])),
    requests = EmbeddedWebAdBlocker.RequestBlockingScript
}));
process.StandardInput.Close();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await process.WaitForExitAsync(timeout.Token);
}
catch (OperationCanceledException)
{
    process.Kill(entireProcessTree: true);
    throw;
}
return process.ExitCode;
