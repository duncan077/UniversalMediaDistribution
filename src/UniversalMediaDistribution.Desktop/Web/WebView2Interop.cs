using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// The slice of the WebView2 COM surface this application needs.
///
/// Avalonia hands out a raw <c>ICoreWebView2</c> pointer and nothing more, so
/// the interfaces are declared here. Method order <em>is</em> the ABI: a
/// misplaced declaration does not fail to compile, it calls the wrong function.
/// Every layout below is transcribed from <c>WebView2.idl</c> as shipped in the
/// Microsoft.Web.WebView2 SDK, including the methods this application never
/// calls — they are present only to hold the slots that follow them in place.
///
/// Source generation is used rather than <c>ComImport</c> so the marshalling
/// stubs survive trimming and ahead-of-time compilation, which the
/// <c>win-x64-aot</c> publish profile needs.
/// </summary>
internal static class WebView2Interop
{
    public static StrategyBasedComWrappers Wrappers { get; } = new();
}

internal enum WebResourceContext
{
    All = 0,
    Document = 1,
    Stylesheet = 2,
    Image = 3,
    Media = 4,
    Font = 5,
    Script = 6,
    XmlHttpRequest = 7,
    Fetch = 8,
    TextTrack = 9,
    EventSource = 10,
    Websocket = 11,
    Manifest = 12,
    SignedExchange = 13,
    Ping = 14,
    CspViolationReport = 15,
    Other = 16
}

[GeneratedComInterface]
[Guid("76eceacb-0462-4d94-ac83-423a6793775e")]
internal partial interface ICoreWebView2
{
    [PreserveSig] int GetSettings(out nint settings);

    [PreserveSig] int GetSource(out nint uri);

    [PreserveSig] int Navigate([MarshalAs(UnmanagedType.LPWStr)] string uri);

    [PreserveSig]
    int NavigateToString(
        [MarshalAs(UnmanagedType.LPWStr)] string htmlContent);

    [PreserveSig] int AddNavigationStarting(nint handler, out long token);

    [PreserveSig] int RemoveNavigationStarting(long token);

    [PreserveSig] int AddContentLoading(nint handler, out long token);

    [PreserveSig] int RemoveContentLoading(long token);

    [PreserveSig] int AddSourceChanged(nint handler, out long token);

    [PreserveSig] int RemoveSourceChanged(long token);

    [PreserveSig] int AddHistoryChanged(nint handler, out long token);

    [PreserveSig] int RemoveHistoryChanged(long token);

    [PreserveSig] int AddNavigationCompleted(nint handler, out long token);

    [PreserveSig] int RemoveNavigationCompleted(long token);

    [PreserveSig] int AddFrameNavigationStarting(nint handler, out long token);

    [PreserveSig] int RemoveFrameNavigationStarting(long token);

    [PreserveSig] int AddFrameNavigationCompleted(nint handler, out long token);

    [PreserveSig] int RemoveFrameNavigationCompleted(long token);

    [PreserveSig] int AddScriptDialogOpening(nint handler, out long token);

    [PreserveSig] int RemoveScriptDialogOpening(long token);

    [PreserveSig] int AddPermissionRequested(nint handler, out long token);

    [PreserveSig] int RemovePermissionRequested(long token);

    [PreserveSig] int AddProcessFailed(nint handler, out long token);

    [PreserveSig] int RemoveProcessFailed(long token);

    [PreserveSig]
    int AddScriptToExecuteOnDocumentCreated(
        [MarshalAs(UnmanagedType.LPWStr)] string javaScript,
        ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler?
            handler);

    [PreserveSig]
    int RemoveScriptToExecuteOnDocumentCreated(
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [PreserveSig]
    int ExecuteScript(
        [MarshalAs(UnmanagedType.LPWStr)] string javaScript,
        nint handler);

    [PreserveSig]
    int CapturePreview(
        int imageFormat,
        nint imageStream,
        nint handler);

    [PreserveSig] int Reload();

    [PreserveSig]
    int PostWebMessageAsJson(
        [MarshalAs(UnmanagedType.LPWStr)] string webMessageAsJson);

    [PreserveSig]
    int PostWebMessageAsString(
        [MarshalAs(UnmanagedType.LPWStr)] string webMessageAsString);

    [PreserveSig] int AddWebMessageReceived(nint handler, out long token);

    [PreserveSig] int RemoveWebMessageReceived(long token);

    [PreserveSig]
    int CallDevToolsProtocolMethod(
        [MarshalAs(UnmanagedType.LPWStr)] string methodName,
        [MarshalAs(UnmanagedType.LPWStr)] string parametersAsJson,
        ICoreWebView2CallDevToolsProtocolMethodCompletedHandler handler);

    [PreserveSig] int GetBrowserProcessId(out uint value);

    [PreserveSig] int GetCanGoBack(out int canGoBack);

    [PreserveSig] int GetCanGoForward(out int canGoForward);

    [PreserveSig] int GoBack();

    [PreserveSig] int GoForward();

    [PreserveSig]
    int GetDevToolsProtocolEventReceiver(
        [MarshalAs(UnmanagedType.LPWStr)] string eventName,
        out nint receiver);

    [PreserveSig] int Stop();

    [PreserveSig] int AddNewWindowRequested(nint handler, out long token);

    [PreserveSig] int RemoveNewWindowRequested(long token);

    [PreserveSig] int AddDocumentTitleChanged(nint handler, out long token);

    [PreserveSig] int RemoveDocumentTitleChanged(long token);

    [PreserveSig] int GetDocumentTitle(out nint title);

    [PreserveSig]
    int AddHostObjectToScript(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        nint value);

    [PreserveSig]
    int RemoveHostObjectFromScript(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig] int OpenDevToolsWindow();

    [PreserveSig]
    int AddContainsFullScreenElementChanged(
        nint handler,
        out long token);

    [PreserveSig] int RemoveContainsFullScreenElementChanged(long token);

    [PreserveSig] int GetContainsFullScreenElement(out int value);

    [PreserveSig]
    int AddWebResourceRequested(
        ICoreWebView2WebResourceRequestedEventHandler handler,
        out long token);

    [PreserveSig] int RemoveWebResourceRequested(long token);

    [PreserveSig]
    int AddWebResourceRequestedFilter(
        [MarshalAs(UnmanagedType.LPWStr)] string uri,
        WebResourceContext resourceContext);

    [PreserveSig]
    int RemoveWebResourceRequestedFilter(
        [MarshalAs(UnmanagedType.LPWStr)] string uri,
        WebResourceContext resourceContext);

    [PreserveSig] int AddWindowCloseRequested(nint handler, out long token);

    [PreserveSig] int RemoveWindowCloseRequested(long token);
}

[GeneratedComInterface]
[Guid("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C")]
internal partial interface ICoreWebView2_2 : ICoreWebView2
{
    [PreserveSig]
    int AddWebResourceResponseReceived(
        ICoreWebView2WebResourceResponseReceivedEventHandler handler,
        out long token);

    [PreserveSig] int RemoveWebResourceResponseReceived(long token);

    [PreserveSig] int NavigateWithWebResourceRequest(nint request);

    [PreserveSig] int AddDomContentLoaded(nint handler, out long token);

    [PreserveSig] int RemoveDomContentLoaded(long token);

    [PreserveSig] int GetCookieManager(out nint cookieManager);

    [PreserveSig] int GetEnvironment(out ICoreWebView2Environment? environment);
}

[GeneratedComInterface]
[Guid("b96d755e-0319-4e92-a296-23436f46a1fc")]
internal partial interface ICoreWebView2Environment
{
    [PreserveSig] int CreateCoreWebView2Controller(nint parentWindow, nint handler);

    [PreserveSig]
    int CreateWebResourceResponse(
        nint content,
        int statusCode,
        [MarshalAs(UnmanagedType.LPWStr)] string reasonPhrase,
        [MarshalAs(UnmanagedType.LPWStr)] string headers,
        out ICoreWebView2WebResourceResponse? response);

    [PreserveSig] int GetBrowserVersionString(out nint versionInfo);

    [PreserveSig]
    int AddNewBrowserVersionAvailable(
        nint handler,
        out long token);

    [PreserveSig] int RemoveNewBrowserVersionAvailable(long token);
}

[GeneratedComInterface]
[Guid("aafcc94f-fa27-48fd-97df-830ef75aaec9")]
internal partial interface ICoreWebView2WebResourceResponse
{
    [PreserveSig] int GetContent(out nint content);

    [PreserveSig] int SetContent(nint content);

    [PreserveSig] int GetHeaders(out nint headers);

    [PreserveSig] int GetStatusCode(out int statusCode);

    [PreserveSig] int SetStatusCode(int statusCode);

    [PreserveSig] int GetReasonPhrase(out nint reasonPhrase);

    [PreserveSig]
    int SetReasonPhrase(
        [MarshalAs(UnmanagedType.LPWStr)] string reasonPhrase);
}

[GeneratedComInterface]
[Guid("97055cd4-512c-4264-8b5f-e3f446cea6a5")]
internal partial interface ICoreWebView2WebResourceRequest
{
    [PreserveSig] int GetUri(out nint uri);

    [PreserveSig] int SetUri([MarshalAs(UnmanagedType.LPWStr)] string uri);

    [PreserveSig] int GetMethod(out nint method);

    [PreserveSig] int SetMethod([MarshalAs(UnmanagedType.LPWStr)] string method);

    [PreserveSig] int GetContent(out nint content);

    [PreserveSig] int SetContent(nint content);

    [PreserveSig]
    int GetHeaders(out ICoreWebView2HttpRequestHeaders? headers);
}

[GeneratedComInterface]
[Guid("453e667f-12c7-49d4-be6d-ddbe7956f57a")]
internal partial interface ICoreWebView2WebResourceRequestedEventArgs
{
    [PreserveSig] int GetRequest(out ICoreWebView2WebResourceRequest? request);

    [PreserveSig]
    int GetResponse(
        out ICoreWebView2WebResourceResponse? response);

    [PreserveSig] int SetResponse(ICoreWebView2WebResourceResponse? response);

    [PreserveSig] int GetDeferral(out nint deferral);

    [PreserveSig] int GetResourceContext(out WebResourceContext context);
}

[GeneratedComInterface]
[Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17")]
internal partial interface
    ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    /// <summary>
    /// Delivers the identifier the registration was given. Without it a
    /// document-start script can be added but never removed, and a viewer who
    /// opens three servers in a row would accumulate three payloads.
    /// </summary>
    [PreserveSig]
    int Invoke(
        int errorCode,
        [MarshalAs(UnmanagedType.LPWStr)] string? id);
}

[GeneratedComInterface]
[Guid("ab00b74c-15f1-4646-80e8-e76341d25d71")]
internal partial interface ICoreWebView2WebResourceRequestedEventHandler
{
    [PreserveSig]
    int Invoke(
        ICoreWebView2? sender,
        ICoreWebView2WebResourceRequestedEventArgs? args);
}

[GeneratedComInterface]
[Guid("e86cac0e-5523-465c-b536-8fb9fc8c8c60")]
internal partial interface ICoreWebView2HttpRequestHeaders
{
    [PreserveSig]
    int GetHeader(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out nint value);

    [PreserveSig]
    int GetHeaders(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out nint iterator);

    [PreserveSig]
    int Contains(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out int value);

    [PreserveSig]
    int SetHeader(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    [PreserveSig]
    int RemoveHeader(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig] int GetIterator(out nint iterator);
}

[GeneratedComInterface]
[Guid("03c5ff5a-9b45-4a88-881c-89a9f328619c")]
internal partial interface ICoreWebView2HttpResponseHeaders
{
    [PreserveSig]
    int AppendHeader(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    [PreserveSig]
    int Contains(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out int value);

    [PreserveSig]
    int GetHeader(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out nint value);

    [PreserveSig]
    int GetHeaders(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out nint iterator);

    [PreserveSig] int GetIterator(out nint iterator);
}

[GeneratedComInterface]
[Guid("79701053-7759-4162-8f7d-f1b3f084928d")]
internal partial interface ICoreWebView2WebResourceResponseView
{
    [PreserveSig]
    int GetHeaders(out ICoreWebView2HttpResponseHeaders? headers);

    [PreserveSig] int GetStatusCode(out int statusCode);

    [PreserveSig] int GetReasonPhrase(out nint reasonPhrase);

    [PreserveSig] int GetContent(nint handler);
}

[GeneratedComInterface]
[Guid("d1db483d-6796-4b8b-80fc-13712bb716f4")]
internal partial interface ICoreWebView2WebResourceResponseReceivedEventArgs
{
    [PreserveSig]
    int GetRequest(out ICoreWebView2WebResourceRequest? request);

    [PreserveSig]
    int GetResponse(out ICoreWebView2WebResourceResponseView? response);
}

[GeneratedComInterface]
[Guid("7de9898a-24f5-40c3-a2de-d4f458e69828")]
internal partial interface ICoreWebView2WebResourceResponseReceivedEventHandler
{
    [PreserveSig]
    int Invoke(
        ICoreWebView2? sender,
        ICoreWebView2WebResourceResponseReceivedEventArgs? args);
}
