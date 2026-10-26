# Content filtering for the embedded player

Embedded playback hosts are popunder- and advertisement-heavy. This document
describes the filtering engine that protects that surface: what it enforces,
where it enforces it, what it deliberately refuses to do, and what it costs on a
device that has no room to spare.

## The invariant

**Filtering may cost an advertisement. It may never cost the video.**

Two allowances are checked before any rule, and no filter list can overturn
either:

- Requests to the page's own site or a subdomain of it.
- Anything that looks like media: a manifest, a segment, a key, a subtitle
  track, or a media file extension. Checked against the path and query only, so
  an advertising host cannot rename itself into the allowance.

These are not just a safety net — during playback almost every request is a
segment, so they are also the fast path that keeps the rule index cold while a
video is streaming.

## Architecture

`src/UniversalMediaDistribution.ContentFiltering` holds the whole engine and has
no dependency on Avalonia or on any platform.

| Piece | Responsibility |
| --- | --- |
| `Parsing/FilterListParser` | Adblock Plus / uBlock Origin syntax to typed rules |
| `Compilation/RulesetCompiler` | Typed rules to one contiguous binary ruleset |
| `CompiledRuleset` | Reads that blob back as spans |
| `ContentFilterEngine` | Matching, cosmetic resolution, hot ruleset swap |
| `Injection/InjectionPayloadBuilder` | The script a page receives |
| `Subscriptions/FilterListSubscriptionService` | Download, cache, recompile |

### The compiled ruleset is a blob, not an object graph

A hundred thousand rules as managed objects would be sixty to a hundred and
twenty megabytes and a permanent burden on the garbage collector. Instead the
compiler emits one contiguous buffer: a UTF-8 string pool, fixed sixteen-byte
rule records, and a sorted token index with a bitset prefilter. `CompiledRuleset`
reads it through `ReadOnlySpan`, so the same ruleset is a handful of large
allocations that the collector never walks.

Matching probes only the buckets a URL's own tokens name. The correctness
property that makes this safe is that a rule may only be indexed under a
*boundary-aligned* token — one whose preceding pattern character is a non-token
character or an anchor — while a URL emits every prefix of every token run. A
rule that could match is therefore always reachable through its bucket. That
property has its own test (`TokenIndexTests`), because a rule filed under a
token no URL emits is a rule that silently never fires.

### The match path allocates nothing

`ContentFilterEngine.Match` performs no allocation at all, and
`MatchPathAllocationTests` asserts it. This is not a micro-optimisation: the
method runs on a web view's network thread for every subresource of every page,
and on a low-memory television box the garbage it would otherwise produce is the
difference between smooth playback and a stutter.

The one exception is a URL whose host carries a trailing dot — a legal but
non-canonical spelling that must not become a way around a rule. That case
rewrites the URL, which needs a string, and it never occurs on the path a page's
own requests take.

## Supported syntax

**Network rules.** `||host^`, `|scheme://`, `^`, `*`, `/regular expressions/`,
and plain substrings. Options: `$third-party` / `$1p` / `$3p`,
`$domain=` / `$from=`, `$to=`, `$denyallow=`, `$method=`, `$important`,
`$badfilter`, `$match-case`, `$redirect=` / `$redirect-rule=`, `$all`, and the
resource types (`script`, `image`, `stylesheet`, `xmlhttprequest`,
`subdocument`, `media`, `font`, `websocket`, `ping`, `other`, `document`), each
of which may be negated. `@@` marks an exception.

Precedence, strongest first: an important exception, an important block, an
ordinary exception, an ordinary block.

**Cosmetic rules.** `##selector`, `#@#selector`, domain-scoped
`a.com,~b.a.com##selector`, and procedural selectors (`:has-text`,
`:matches-css`, `:upward`, `:xpath`, `:remove`, `:min-text-length`).

**Scriptlets.** `##+js(name, arg, ...)`, for the names listed in
`InjectionPayloadBuilder.SupportedScriptlets`. The implementations are our own,
written against the documented behaviour of those names; no uBlock Origin code
is used. A rule naming a scriptlet this build does not implement is dropped at
build time rather than shipped and ignored in the page.

**Not enforced.** `$csp=`, `$removeparam=`, `$header=`, `$popup`, `$elemhide`
and its relatives, and AdGuard-only cosmetic forms. Rules using them are parsed,
counted, and **dropped**. A half-applied rule breaks pages in ways nobody can
diagnose, and a page that breaks here is a video that does not play.

Regular expressions compile with `RegexOptions.NonBacktracking`, never
`RegexOptions.Compiled`: the latter needs runtime code generation that trimmed
Android builds do not have, and linear-time matching is what stops a hostile
pattern from stalling the network thread.

## Where rules are enforced

| Host | Blocking | Payload injection |
| --- | --- | --- |
| Windows (WebView2) | `WebResourceRequested` answers with a 403 | `AddScriptToExecuteOnDocumentCreated` |
| Android (API 26+) | A delegating `WebViewClient` overriding `shouldInterceptRequest` | `WebViewCompat.AddDocumentStartJavaScript`, else `onPageStarted` |
| Everything else | The injected policy wraps the page's own request APIs | After navigation completes |

Cosmetic payloads are per host, so both native hooks re-register theirs on
every navigation rather than keeping the host they attached with — and they
remove the previous registration first, or a viewer who tries three servers in
a row would accumulate three payloads. On Windows that needs the identifier
returned through the registration's completion handler; on Android it is the
`ScriptHandler` the call returns.

The in-page path remains the fallback and is what runs when no platform hook
attaches. It is weaker by nature — a page that captures `fetch` before the
script runs can evade it — which is exactly why the native hooks exist.

**Only the frame the hook is attached to is filtered.** A cross-origin iframe
runs in its own renderer, and WebView2 raises `WebResourceRequested` for the
frame the filter is attached to and no other — no `ICoreWebView2Frame`
interface exposes the event, so there is nothing to attach to a subframe. This
is a platform limitation, not a gap in the engine.

It matters because several hosts hand out a wrapper page whose only content is
an iframe of the real player, of the form
`host/iframe/?url=https%3A%2F%2Freal.example%2Fe%2Fid`. Measured on one, the
filter was offered seven requests while the page made fifty-one: everything
inside the iframe, including every advertisement, was invisible to it.

`EmbeddedWebTarget` therefore resolves such a wrapper to the address it wraps.
Only a short list of parameter names that mean "this is the page" is followed,
and only to a different host that passes the player's own safety check — a
parameter that merely happens to contain a URL is usually a tracker, and
following it would take the viewer somewhere they never asked to go.

Unwrapping happens in **two** places, and the earlier one matters more:

- `VideoHosterResolver` unwraps before matching an extractor. No extractor
  matches `host/iframe/?url=...`, so a title whose real hoster is one of the
  supported ones — VOE, for instance, which the map already knows by every one
  of its rotating domains — fell back to the embedded browser rather than
  playing natively. Unwrapping first lets the extractor recognize it, pull out
  the HLS address, and hand it to the native player. That skips the hoster's
  page altogether, which is a better outcome than filtering it.
- `WebViewPlatformMediaPlayer` unwraps whatever still reaches the embedded
  browser, so the player is at least the top-level document and filterable.

A page that iframes its own advertisements remains partly out of reach.

**Nothing may throw across a platform boundary.** The request callbacks are
invoked from COM on Windows and from Java on Android, and the payload
registration runs from a dispatcher callback. There is no managed frame above
any of them, so an exception that escapes does not fail a request — it ends the
process. All three catch everything and treat the failure as "not filtered",
because an unfiltered request is a far smaller problem than a dead application.
Switching servers before the previous one finished loading is the case that
found this: it tears a platform object down while a callback is still using it,
and it fails in more ways than the obvious exception type.

For the same reason the hook is attached *before* the session is told a new
page is opening. Announcing first meant a hook still bound to the previous
platform object answered the announcement by calling into one that was being
disposed.

**A hook is never latched on.** Avalonia destroys and recreates the platform
adapter when the control is reparented, which happens every time the viewer
leaves the player and opens a server again — the `ICoreWebView2` pointer
genuinely changes. A hook that attached once and assumed it stayed attached
filtered the first video of a session and silently nothing afterwards, which
looks exactly like filtering that does not work at all. Both hooks therefore
compare the platform pointer on every navigation and re-attach when it differs.

**Cached subresources are not offered to the filter.** `WebResourceRequested`
and `shouldInterceptRequest` are raised for requests that reach the network
stack; a resource already in the web view's cache is served without either.
Measured on a real page, that was the difference between fifty-one requests the
page made and seven the filter was asked about — the rest came from a profile
that had accumulated them while filtering was not attached, and no amount of
correct filtering reaches those.

The embedded player therefore runs on a **private WebView2 profile**
(`IsInPrivateModeEnabled`), so nothing survives the session to be served behind
the filter's back. It costs a re-fetch of the player's own scripts each session
and loses consent cookies, which is a fair price on third-party pages from
hosts whose business is tracking — and it means the request counts in the
settings panel describe what the page actually did.

Both native hooks reach the platform object through
`NativeWebView.TryGetPlatformHandle()`. The Windows COM interface layouts in
`src/UniversalMediaDistribution.Desktop/Web/WebView2Interop.cs` are transcribed
from `WebView2.idl` as shipped in the Microsoft.Web.WebView2 SDK, including
methods the application never calls: **method order is the ABI**, and a
misplaced declaration does not fail to compile, it calls the wrong function.

Popup suppression, cross-site click interception, and full-viewport click-trap
removal are built in and always run, at every device tier. No filter list
describes them, and they are what these player hosts actually do.

So is one more: **the frame around a refused picture is removed with it.** These
hosts redirect to a throwaway domain and inject their advertisement cards from
a script on that same origin, which puts the markup out of every rule's reach —
only the pictures and trackers it pulls in are blockable, and blocking those
leaves an empty white box on screen. A failed image is therefore treated as the
signal: the positioned or clickable element that framed it goes too, unless it
holds the player. Unlike a selector, that does not stop working when the domain
rotates tomorrow.

## Filter lists

No list data ships in the binary. Only a small seed — the curated hosts and
paths that were previously hard-coded — is compiled in, which is what protects a
first run and an offline install. Everything else is fetched at runtime into
application storage.

Default subscriptions, mirroring uBlock Origin Lite's set: uBlock Origin
filters, badware, privacy, quick fixes and unbreak; EasyList; EasyPrivacy;
Peter Lowe's list. EasyList annoyances is added by the `Complete` mode.

Those lists are published under their own terms — GPL and CC BY-SA, depending on
the list. Fetching them at runtime rather than redistributing them keeps that a
matter between the viewer and the publishers.

**Privacy.** A refresh issues one conditional HTTP GET per subscribed list to
the addresses above and sends nothing but the standard conditional headers. No
browsing activity, no visited hosts, and no identifiers leave the device. Raw
lists are cached gzip-compressed so a ruleset format change costs a recompile
rather than a re-download; the compiled ruleset and a small manifest of ETags
sit alongside them.

**On disk.** The compiled ruleset is written as `ruleset-<n>.bin`, a new
generation each time, and the newest one that loads wins. It is not replaced in
place, because it cannot be: Windows refuses to rename over a file that has an
open memory mapping, whatever share mode it was opened with, so a filtering
engine that mapped its ruleset could otherwise never be updated again in that
session. Superseded generations are deleted once nothing holds them, which on
Windows means the next run — the worst case is one stale file per session.

## Settings

**Settings → Advertisement and tracker filtering** offers four levels: `Off`,
built-in rules only, `Standard`, and `Complete`. The choice is stored in the
application settings table and applied immediately; a change goes to the
network at once, because that is the one moment the viewer is waiting for an
answer. An **Update lists now** button forces a refresh, and the panel reports
how many rules are loaded and when the lists were last checked.

The device tier caps the choice rather than overriding it silently: asking for
`Complete` on a low-memory device runs `Standard`, and the panel says so.

After an embedded page has been open, the panel also reports what filtering
actually did to it: whether requests were refused at the network layer or only
from inside the page, how many were refused, and how many were allowed by each
of the two unconditional allowances. That breakdown is the diagnostic — "I
still see advertisements" has several causes that look identical from the
outside, and a large same-site count says the host is serving its own
advertising, which no ruleset can reach.

It also names the hosts that loaded simply because no rule mentions them. The
subscribed lists are large but not complete, and a small or regional
advertisement network is often in none of them — naming the host is what turns
a surviving advertisement into a rule someone can write. Setting
`UMD_EMBEDDED_WEB_DEVTOOLS=1` additionally opens the web engine's own developer
tools on the embedded player, for the cases where the markup matters and the
host does not:

```powershell
$env:UMD_EMBEDDED_WEB_DEVTOOLS = "1"
dotnet run --project src\UniversalMediaDistribution.Desktop
```

The window opens by itself as soon as an embedded server page is shown. It is
opened programmatically rather than bound to a key: F12 belongs to the host
application's own inspector, and a keystroke delivered to a hosted page is not
reliably ours to claim. These are the web engine's developer tools — the
network log and element inspector for the page inside the player — and are
unrelated to, and independent of, Avalonia's own diagnostics.

**Scheduling is otherwise the host's decision.** The service never picks its own
moment.
Downloading fifteen megabytes of list text over a television's wireless
connection while a torrent stream saturates it is a real regression, and so is
compiling a hundred thousand rules while a video decodes.

`ContentFilterCoordinator` therefore requires three conditions before any list
is fetched: the host's own signal — on Android, network availability, whether
the connection is metered, and whether the device is saving power — that no
embedded player page is open, and that the list is actually due. Startup waits
45 seconds before its first attempt so a download does not compete with the
first catalog load.

Every failure keeps the previous ruleset. A refresh never leaves the
application unprotected.

## Device tiers and cost

`DeviceTier` is decided from the hardware, not from a preference. A Chromecast
with Google TV is a television with about two gigabytes of memory and four small
cores, and this application is already decoding video and verifying torrent
pieces while filtering runs.

| | Constrained | Standard |
| --- | --- | --- |
| EasyPrivacy, annoyance lists | not subscribed | subscribed |
| Generic element hiding | off | on |
| Procedural selectors | off | on, throttled |
| Selector ceiling | 500 | 50,000 |
| Always-evaluated rules | 512 | 2048 |
| Regular-expression rules | 128 | 1024 |
| Procedural budget per pass | 6 ms | 12 ms |
| Elements examined per pass | 500 | 2000 |

The subscription row is the one that matters for memory. The compiled ruleset is
dominated by network rules, and dropping cosmetics alone only took the
constrained blob from 9.9 MB to 8.2 MB — still twice the budget. EasyPrivacy is
about fifty-five thousand rules on its own, which is why a low-memory device
gives it up first: the advertisement and popunder blocking that actually
protects playback lives in the other lists.

The tier is a ceiling. A viewer may ask for less filtering than the device could
carry, never for more: `Complete` on a constrained device resolves to
`Standard`.

**Generic hiding goes into the top document only.** A document-start script
runs in *every* frame, and the generic set is around fourteen thousand
selectors — a quarter of a megabyte. Parsing that once per frame on a page
built out of frames is enough work to stall the renderer, and it buys nothing:
an advertisement frame is the advertiser's own document, where hiding elements
is pointless because the frame should not have loaded at all. Rules written for
the host stay in every frame its page runs in, and the built-in defences run
everywhere. The payload is also built once per host and reused, rather than
rebuilt on each navigation and re-registration.

**The selector ceiling is a sanity limit, not a throttle.** Neither embedded
host has any host-specific cosmetic rules in the subscribed lists, so generic
hiding — about fourteen thousand selectors — is their entire cosmetic defence.
A ceiling below that takes an arbitrary prefix of it, and the advertisement
boxes whose selectors fell off the end simply stay on screen. The whole set
costs about a quarter of a megabyte. A device that cannot afford it declines
generic hiding altogether rather than taking a fraction of it.

The stylesheet is injected as soon as the payload runs, not at
`DOMContentLoaded`: waiting means the box is laid out and painted first, so
hiding it afterwards is a flash of exactly the content that was meant to be
removed. Only the procedural pass, which needs elements to walk, waits for the
DOM.

**Why generic element hiding is the first thing to go.** EasyList's generic
cosmetic section is tens of thousands of selectors, and every one of them is
matched against every element on every style recalculation — continuously, while
a video plays. Host-specific selectors for a given embed host number in the
dozens and cost nothing. The trade is deliberate: on a constrained device some
advertisement containers remain as empty boxes. Blocking the *request* is what
stops the popunder; hiding the leftover box is cosmetic in both senses.

The in-page runtime is budgeted for the same reason. Procedural passes are
coalesced into an animation frame, capped by both time and element count, and
the mutation observer disconnects once the DOM has been still for a few seconds.
An embed page settles quickly, and watching it for the rest of a two-hour film
buys nothing.

### Measured

Against the live upstream lists (about 221,000 source lines across nine lists),
compiled and matched on a desktop:

| | Constrained | Standard |
| --- | --- | --- |
| Network rules | 66,891 | 125,718 |
| Cosmetic rules | 14,803 | 74,445 |
| Compiled ruleset | 4.14 MB | 9.90 MB |
| Compile time | 168 ms | 200 ms |
| Rules evaluated for every request | 188 | 258 |
| Regular-expression rules | 128 | 194 |
| Match, warm | 2.5 µs | 9.4 µs |
| Allocation per match | 0 bytes | 0 bytes |

Both blobs are inside the budgets above. The match figures are desktop numbers;
a Cortex-A55 is several times slower, which is the whole argument for the
constrained tier — its 2.5 µs has room to be five times worse and still sit
well under the 200 µs the network thread can afford, where the standard tier's
9.4 µs would not.

Startup does not pay any of the compile cost: it maps the blob that was
compiled last time and checks a header.

The mapping is why those megabytes are file-backed rather than anonymous — on a
two-gigabyte device that is the difference between memory the low-memory killer
counts against the application and pages the kernel can evict and re-read.
Nothing in a ruleset is ever written, so eviction costs a read, not a swap.

**The mapping is never unmapped, on purpose.** A match runs against spans
pointing straight into that memory while the engine may already have swapped in
a newer ruleset, so releasing an old view on a finalizer or a reference count
would eventually free memory a request is still reading — not an exception, a
corrupted read. Holding the view open for the life of the process costs a few
megabytes of evictable file-backed address space per retired ruleset, and
refreshes happen a couple of times a session at most.

Skip rates per list run from 0% to 12.5%, which is the parsed-but-not-enforced
syntax listed above rather than lines the parser failed on.

## Not yet done

- Linux, macOS, and iOS native hooks. Both WebKit families consume the same
  content-blocker JSON, so one emitter over `CompiledRuleset` would serve all
  three.
- The Public Suffix List is fetched rather than bundled; until one arrives, a
  built-in fallback covers the multi-label suffixes that occur in practice.
  A wrong answer only mis-classifies first versus third party for rules that
  constrain on it. Nothing fetches it yet.
- On-device verification of both native hooks. The subscription, caching, and
  memory-mapping paths have been exercised end to end on Windows against the
  live lists; the request interception and document-start injection themselves
  cannot be run from a build machine.
