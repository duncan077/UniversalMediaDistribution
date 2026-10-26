# Challenged artwork integration check

Windows-only, opt-in live check. Exercises the production HTTP challenge gate,
WebView2 solver and Avalonia bitmap decoder for a public HTTPS cover URL. It does
not open the app catalog or modify its settings. Its off-screen browser uses an
isolated profile beside the build output; a challenge requiring manual input can
still time out.

```powershell
dotnet build eng/ChallengeArtworkProbe/ChallengeArtworkProbe.csproj -c Release -p:UsedAvaloniaProducts=
& ./eng/ChallengeArtworkProbe/bin/Release/net11.0/ChallengeArtworkProbe.exe 'https://www.leercapitulo.co/covers/d9/58eed26da8108d5100084f82ab692b.jpg?v1762641074242'
```

A successful check prints the recovered MIME type and decoded bitmap dimensions.
On 2026-09-19 this cover returned an HTTP 403 Cloudflare challenge to direct HTTP;
the production browser fallback returned `image/png`, decoded at `300, 450`.
