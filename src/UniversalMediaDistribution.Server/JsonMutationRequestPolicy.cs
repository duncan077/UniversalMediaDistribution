using Microsoft.AspNetCore.Http.Features;

namespace UniversalMediaDistribution.Server;

internal static class JsonMutationRequestPolicy
{
    public static bool ApplyLimit(
        HttpContext context,
        long maximumJsonMutationBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsBoundedJsonMutation(context.Request))
        {
            return true;
        }

        var maximumRequestBytes = IsCrawlerAdminRequest(context.Request)
            ? Math.Min(maximumJsonMutationBytes, 4096)
            : IsAccountRequest(context.Request)
            ? Math.Min(maximumJsonMutationBytes, 32L * 1024)
            : maximumJsonMutationBytes;
        var feature =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            feature.MaxRequestBodySize = maximumRequestBytes;
        }

        if (context.Request.ContentLength > maximumRequestBytes)
        {
            context.Response.StatusCode =
                StatusCodes.Status413PayloadTooLarge;
            return false;
        }

        return true;
    }

    private static bool IsBoundedJsonMutation(HttpRequest request) =>
        (request.Method == HttpMethods.Post &&
         (request.Path.StartsWithSegments("/api/v1/accounts") ||
          request.Path.StartsWithSegments("/api/v1/admin/diagnostics/crawl") ||
          request.Path.StartsWithSegments("/api/v1/admin/accounts") ||
          request.Path.StartsWithSegments("/api/v1/shared-lists"))) ||
        (request.Method == HttpMethods.Put &&
         (request.Path.StartsWithSegments("/api/v1/admin/diagnostics/provider-interval") ||
          request.Path.StartsWithSegments("/api/v1/catalog") ||
          request.Path.StartsWithSegments("/api/v1/sync") ||
          (request.Path.StartsWithSegments("/api/v1/releases") &&
           !(request.Path.Value?.Contains(
               "/package/",
               StringComparison.OrdinalIgnoreCase) ?? false)))) ||
        ((request.Method == HttpMethods.Delete || request.Method == HttpMethods.Patch) &&
         request.Path.StartsWithSegments("/api/v1/sync"));

    private static bool IsAccountRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/v1/accounts") ||
        request.Path.StartsWithSegments("/api/v1/admin/accounts");

    private static bool IsCrawlerAdminRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/v1/admin/diagnostics/crawl") ||
        request.Path.StartsWithSegments("/api/v1/admin/diagnostics/provider-interval");
}
