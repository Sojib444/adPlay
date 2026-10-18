using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AdPlay.Api.Middleware;

public sealed class RequestLoggingOptions
{
    public const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>Bodies are captured only when this is on and Debug logging is enabled.</summary>
    public bool CaptureBodies { get; set; } = true;

    /// <summary>Maximum bytes captured per body; the rest streams through untouched.</summary>
    public int MaxBodyBytes { get; set; } = 16 * 1024;

    /// <summary>Larger request bodies are never buffered for logging.</summary>
    public long MaxRequestBodyToBuffer { get; set; } = 1024 * 1024;

    /// <summary>Path prefixes whose bodies are never captured (payments, credentials).</summary>
    public string[] ExcludedBodyPaths { get; set; } = ["/api/payments", "/api/auth"];

    public string[] LoggableContentTypes { get; set; } =
        ["application/json", "application/problem+json", "text/plain", "application/x-www-form-urlencoded"];
}

/// <summary>
/// Correlation ID, timing, unhandled-exception handling and optional redacted body capture.
/// Register it first so it covers the whole pipeline.
/// </summary>
public sealed partial class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger,
    IOptions<RequestLoggingOptions> options)
{
    private readonly RequestLoggingOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var correlationId = ResolveCorrelationId(request);
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestLoggingOptions.CorrelationHeader] = correlationId;
            return Task.CompletedTask;
        });

        // Every log line further down the pipeline carries these.
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString()
        });

        var started = Stopwatch.GetTimestamp();
        var captureBodies = _options.CaptureBodies && logger.IsEnabled(LogLevel.Debug) && !IsExcludedPath(request.Path);

        string? requestBody = null;
        if (captureBodies && ShouldCaptureRequestBody(request))
            requestBody = await ReadRequestBodyAsync(request, context.RequestAborted);

        var originalBody = context.Response.Body;
        var tee = captureBodies ? new CappedTeeStream(originalBody, _options.MaxBodyBytes) : null;
        if (tee is not null) context.Response.Body = tee;

        var outcome = "completed";
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected: not a server fault.
            outcome = "aborted";
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception ex)
        {
            outcome = "failed";
            LogUnhandledException(ex, request.Method, request.Path.Value);

            // Headers already sent: rethrow so the server aborts instead of sending a truncated 200.
            if (context.Response.HasStarted) throw;

            tee?.ResetCapture();
            context.Response.Clear();
            await Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "An unexpected error occurred.",
                extensions: new Dictionary<string, object?> { ["correlationId"] = correlationId })
                .ExecuteAsync(context);
        }
        finally
        {
            if (tee is not null) context.Response.Body = originalBody;
            LogCompletion(context, outcome, Stopwatch.GetElapsedTime(started), requestBody, tee);
        }
    }

    private void LogCompletion(HttpContext context, string outcome, TimeSpan elapsed, string? requestBody, CappedTeeStream? tee)
    {
        var request = context.Request;
        var status = context.Response.StatusCode;
        var level = outcome == "failed" || status >= 500 ? LogLevel.Error : LogLevel.Information;
        var query = request.QueryString.HasValue ? SensitiveDataRedactor.RedactText(request.QueryString.Value!) : "";

        LogRequestCompleted(level, request.Method, request.Path.Value, query, status, elapsed.TotalMilliseconds, outcome);

        if (tee is null) return;

        var responseBody = DescribeBody(tee.Captured, tee.Truncated, tee.TotalBytesWritten, context.Response.ContentType);
        LogBodies(request.Method, request.Path.Value, requestBody ?? "(not captured)", responseBody);
    }

    private string DescribeBody(ReadOnlySpan<byte> captured, bool truncated, long totalBytes, string? contentType)
    {
        if (totalBytes == 0) return "(empty)";
        if (!IsLoggableContentType(contentType)) return $"({totalBytes} bytes of {contentType ?? "unknown"})";

        var text = SensitiveDataRedactor.Redact(Encoding.UTF8.GetString(captured), contentType);
        return truncated ? $"{text}…(truncated, {totalBytes} bytes total)" : text;
    }

    private bool ShouldCaptureRequestBody(HttpRequest request) =>
        request.ContentLength is > 0 && request.ContentLength <= _options.MaxRequestBodyToBuffer
        && IsLoggableContentType(request.ContentType);

    /// <summary>Reads the first N bytes through a seekable buffered body, then rewinds for the rest of the pipeline.</summary>
    private async Task<string> ReadRequestBodyAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        var limit = _options.MaxBodyBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            var read = 0;
            while (read < limit)
            {
                var n = await request.Body.ReadAsync(buffer.AsMemory(read, limit - read), ct);
                if (n == 0) break;
                read += n;
            }

            var truncated = request.ContentLength > read;
            var text = SensitiveDataRedactor.Redact(Encoding.UTF8.GetString(buffer, 0, read), request.ContentType);
            return truncated ? $"{text}…(truncated, {request.ContentLength} bytes total)" : text;
        }
        finally
        {
            request.Body.Position = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool IsExcludedPath(PathString path) =>
        _options.ExcludedBodyPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private bool IsLoggableContentType(string? contentType) =>
        contentType is not null &&
        _options.LoggableContentTypes.Any(t => contentType.StartsWith(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reuses a caller-supplied ID only if it is short and safe, to prevent log injection.</summary>
    private static string ResolveCorrelationId(HttpRequest request)
    {
        var incoming = request.Headers[RequestLoggingOptions.CorrelationHeader].ToString();
        return SafeCorrelationId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeCorrelationId();

    [LoggerMessage(Message = "HTTP {Method} {Path}{Query} responded {StatusCode} in {ElapsedMs:0.0} ms ({Outcome})")]
    private partial void LogRequestCompleted(
        LogLevel level, string method, string? path, string query, int statusCode, double elapsedMs, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP {Method} {Path} request body: {RequestBody} | response body: {ResponseBody}")]
    private partial void LogBodies(string method, string? path, string requestBody, string responseBody);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private partial void LogUnhandledException(Exception ex, string method, string? path);
}
