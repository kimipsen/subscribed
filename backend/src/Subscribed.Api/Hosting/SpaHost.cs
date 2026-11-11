using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Subscribed.Api.Bff;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Hosting;

/// <summary>
/// Serves the built Angular SPA (frontend/dist/subscribed/browser, copied to wwwroot) from the
/// same origin. index.html is never served raw: <c>__CSP_NONCE__</c> is replaced per request and
/// the response is no-store. Hashed assets are immutable.
/// </summary>
public sealed class SpaHost
{
    public const string NonceToken = "__CSP_NONCE__";

    private readonly string _root;
    private readonly Lazy<string?> _indexTemplate;

    public SpaHost(string root)
    {
        _root = root;
        _indexTemplate = new Lazy<string?>(() =>
        {
            var path = Path.Combine(_root, "index.html");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        });
    }

    public bool HasRoot => Directory.Exists(_root);

    public void UseStaticAssets(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!HasRoot)
        {
            return;
        }

        var contentTypes = new FileExtensionContentTypeProvider();
        app.Use(async (context, next) =>
        {
            // index.html only through the nonce path below.
            if (context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                await ServeIndexAsync(context);
                return;
            }

            await next(context);
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_root),
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = false,
            OnPrepareResponse = ctx =>
            {
                var name = ctx.File.Name;
                ctx.Context.Response.Headers.CacheControl = IsHashed(name) ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
    }

    /// <summary>Fallback for non-API HTML navigations.</summary>
    public async Task FallbackAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        var reserved = RequestGuards.IsApiPath(path) || path.StartsWithSegments("/bff", StringComparison.Ordinal)
            || path.StartsWithSegments("/health", StringComparison.Ordinal) || path.StartsWithSegments(OidcSetup.CallbackPath, StringComparison.Ordinal);
        var lastSegment = path.Value?[(path.Value.LastIndexOf('/') + 1)..] ?? "";
        if (reserved || lastSegment.Contains('.', StringComparison.Ordinal)
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            await Problem.WriteAsync(context, 404, ProblemCodes.NotFound, "Not found.");
            return;
        }

        await ServeIndexAsync(context);
    }

    private async Task ServeIndexAsync(HttpContext context)
    {
        var template = _indexTemplate.Value;
        if (template is null)
        {
            await Problem.WriteAsync(context, 404, ProblemCodes.NotFound, "Not found.");
            return;
        }

        var nonce = context.Items[RequestGuards.CspNonceKey] as string ?? "";
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(template.Replace(NonceToken, nonce, StringComparison.Ordinal), context.RequestAborted);
        }
    }

    /// <summary>Angular output names carry a content hash: name-ABCDEFGH.ext.</summary>
    private static bool IsHashed(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        return dash > 0 && stem.Length - dash - 1 >= 8 && stem[(dash + 1)..].All(char.IsAsciiLetterOrDigit);
    }
}
