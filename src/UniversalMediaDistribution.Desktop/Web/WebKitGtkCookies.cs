using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// Avalonia 12.0.1's GTK adapter has no public cookie manager. Use WebKitGTK
/// 4.1's URI-scoped asynchronous API, which includes HttpOnly cookies. Every
/// WebKit call runs on GLib's thread, and native references survive cancellation
/// until its completion callback has released the list and the operation.
/// </summary>
internal static unsafe partial class WebKitGtkCookies
{
    internal static Task<IReadOnlyList<Cookie>> ReadAsync(NativeWebDialog browser, Uri endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (browser.TryGetWebViewPlatformHandle() is not IGtkWebViewPlatformHandle { WebKitWebView: not 0 } handle)
            return Task.FromResult<IReadOnlyList<Cookie>>([]);
        var operation = new Operation(ObjectRef(handle.WebKitWebView), endpoint.AbsoluteUri, CancellableNew(), token);
        var state = GCHandle.Alloc(operation);
        operation.Registration = token.Register(() => CancellableCancel(operation.Cancellable));
        // Unlike g_main_context_invoke, an idle source never calls WebKit
        // inline on Avalonia's calling thread when the context is unowned.
        if (IdleAdd(0, &Start, GCHandle.ToIntPtr(state), 0) == 0)
        {
            Release(state, operation);
            throw new InvalidOperationException("Could not schedule WebKit cookie retrieval.");
        }
        return operation.Completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Start(nint data)
    {
        var state = GCHandle.FromIntPtr(data);
        var operation = (Operation)state.Target!;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var manager = GetCookieManager(GetWebsiteDataManager(operation.View));
            GetCookies(manager, operation.Uri, operation.Cancellable, &Finished, data);
            return 0;
        }
        catch (OperationCanceledException) { operation.Completion.TrySetCanceled(operation.Token); }
        catch (Exception exception) { operation.Completion.TrySetException(exception); }
        Release(state, operation);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Finished(nint manager, nint result, nint data)
    {
        var state = GCHandle.FromIntPtr(data);
        var operation = (Operation)state.Target!;
        nint list = 0, error = 0;
        try
        {
            list = GetCookiesFinish(manager, result, out error);
            operation.Token.ThrowIfCancellationRequested();
            if (error != 0) throw new InvalidOperationException("WebKit could not retrieve cookies for this website.");
            var cookies = new List<Cookie>();
            for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var cookie = Marshal.ReadIntPtr(node);
                if (cookie == 0) continue;
                try
                {
                    var expires = CookieExpires(cookie);
                    cookies.Add(new Cookie(Text(CookieName(cookie)), Text(CookieValue(cookie)),
                        Text(CookiePath(cookie)), Text(CookieDomain(cookie)))
                    {
                        HttpOnly = CookieHttpOnly(cookie) != 0,
                        Secure = CookieSecure(cookie) != 0,
                        Expires = expires == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(DateTimeToUnix(expires)).UtcDateTime
                    });
                }
                catch (Exception exception) when (exception is CookieException or ArgumentException)
                {
                    // A malformed individual cookie must not escape a native callback.
                }
            }
            operation.Completion.TrySetResult(cookies);
        }
        catch (OperationCanceledException) { operation.Completion.TrySetCanceled(operation.Token); }
        catch (Exception exception) { operation.Completion.TrySetException(exception); }
        finally
        {
            for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var cookie = Marshal.ReadIntPtr(node);
                if (cookie != 0) CookieFree(cookie);
            }
            if (list != 0) ListFree(list);
            if (error != 0) ErrorFree(error);
            Release(state, operation);
        }
    }

    private static string Text(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

    private static void Release(GCHandle state, Operation operation)
    {
        operation.Registration.Dispose();
        ObjectUnref(operation.Cancellable);
        ObjectUnref(operation.View);
        state.Free();
    }

    private sealed class Operation(nint view, string uri, nint cancellable, CancellationToken token)
    {
        internal nint View { get; } = view;
        internal string Uri { get; } = uri;
        internal nint Cancellable { get; } = cancellable;
        internal CancellationToken Token { get; } = token;
        internal CancellationTokenRegistration Registration { get; set; }
        internal TaskCompletionSource<IReadOnlyList<Cookie>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string Soup = "libsoup-3.0.so.0";
    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_website_data_manager")]
    private static partial nint GetWebsiteDataManager(nint view);
    [LibraryImport(WebKit, EntryPoint = "webkit_website_data_manager_get_cookie_manager")]
    private static partial nint GetCookieManager(nint manager);
    [LibraryImport(WebKit, EntryPoint = "webkit_cookie_manager_get_cookies", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void GetCookies(nint manager, string uri, nint cancellable, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint data);
    [LibraryImport(WebKit, EntryPoint = "webkit_cookie_manager_get_cookies_finish")]
    private static partial nint GetCookiesFinish(nint manager, nint result, out nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_idle_add_full")]
    private static partial uint IdleAdd(int priority, delegate* unmanaged[Cdecl]<nint, int> function, nint data, nint notify);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_list_free")]
    private static partial void ListFree(nint list);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")]
    private static partial void ErrorFree(nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_date_time_to_unix")]
    private static partial long DateTimeToUnix(nint dateTime);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")]
    private static partial nint ObjectRef(nint instance);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static partial void ObjectUnref(nint instance);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")]
    private static partial nint CancellableNew();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")]
    private static partial void CancellableCancel(nint instance);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_name")]
    private static partial nint CookieName(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_value")]
    private static partial nint CookieValue(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_domain")]
    private static partial nint CookieDomain(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_path")]
    private static partial nint CookiePath(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_expires")]
    private static partial nint CookieExpires(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_http_only")]
    private static partial int CookieHttpOnly(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_get_secure")]
    private static partial int CookieSecure(nint cookie);
    [LibraryImport(Soup, EntryPoint = "soup_cookie_free")]
    private static partial void CookieFree(nint cookie);
}
