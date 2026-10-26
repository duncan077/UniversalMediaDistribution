Run the JavaScript behavior regressions with .NET and Node.js installed:

```powershell
dotnet run --project tests/UniversalMediaDistribution.ContentFiltering.JavaScriptTests
```

The runner compiles the production payload generators, then executes their output in isolated JavaScript contexts. It checks document-start initialization, deferred styles, idempotence, request wrappers, same-site ads, and media URL bypasses. It does not replace testing the native WebView adapters or real playback.
