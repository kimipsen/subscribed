using Microsoft.AspNetCore.Http.Features;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Mcp;

/// <summary>Raised by the counting body stream once a request body passes the cap. It is a 413 <see cref="BadHttpRequestException"/>, so the error boundary maps it like Kestrel's own limit.</summary>
public sealed class RequestTooLargeException() : BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);

/// <summary>
/// The 64 KB body cap on <c>/mcp</c> (plan WP2 step 4, a deliberate addition to the approved requirements). A tool call has small arguments.
/// It is its own middleware, before the endpoint: setting the Kestrel feature alone is not enough, because it can be absent or read-only
/// (TestServer) and the SDK reads the body, not an endpoint helper. A declared length over the cap is rejected before any read; an
/// undeclared or chunked body is counted and throws <see cref="RequestTooLargeException"/>.
/// </summary>
public static class McpRequestLimits
{
    public const int MaxBodyBytes = 64 * 1024;

    public static bool IsMcpPath(PathString path) =>
        path.Equals("/mcp", StringComparison.Ordinal) || path.StartsWithSegments("/mcp", StringComparison.Ordinal);

    public static async Task BodyCapMiddleware(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!McpRequestLimits.IsMcpPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength is { } declared && declared > MaxBodyBytes)
        {
            await Problem.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge, "The request body is too large.");
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        context.Request.Body = new CountingBodyStream(context.Request.Body, MaxBodyBytes);
        await next(context);
    }

    private sealed class CountingBodyStream(Stream inner, long max) : Stream
    {
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _total += read;
            return _total > max ? throw new RequestTooLargeException() : read;
        }
    }
}
