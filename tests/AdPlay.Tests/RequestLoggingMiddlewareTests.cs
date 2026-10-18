using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AdPlay.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdPlay.Tests;

public class RequestLoggingMiddlewareTests
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }

    private static async Task<(IHost Host, HttpClient Client, CapturingLoggerProvider Logs)> StartAsync(Action<WebApplication> map)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // Category filter beats the "Default: Information" rule from the copied appsettings.json.
        builder.Logging.ClearProviders().AddProvider(logs).AddFilter("AdPlay.Api.Middleware", LogLevel.Debug);
        builder.Services.Configure<RequestLoggingOptions>(o => o.MaxBodyBytes = 64);

        var app = builder.Build();
        app.UseMiddleware<RequestLoggingMiddleware>();
        map(app);
        await app.StartAsync();
        return (app, app.GetTestClient(), logs);
    }

    [Fact]
    public async Task Request_body_is_still_readable_downstream_and_logged_redacted()
    {
        var (host, client, logs) = await StartAsync(app =>
            app.MapPost("/echo", async (HttpRequest r) => Results.Text(await new StreamReader(r.Body).ReadToEndAsync(), "application/json")));
        await using var _ = (IAsyncDisposable)host;

        const string body = """{"username":"bob","password":"hunter2"}""";
        var response = await client.PostAsync("/echo", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(body, await response.Content.ReadAsStringAsync()); // pipeline got the full, untouched body
        var all = string.Join("\n", logs.Entries.Select(e => e.Message));
        Assert.DoesNotContain("hunter2", all);
        Assert.Contains("\"password\":\"***\"", all);
        Assert.Contains("responded 200", all);
    }

    [Fact]
    public async Task Large_response_streams_through_intact_while_capture_is_capped()
    {
        var payload = new string('x', 10_000);
        var (host, client, logs) = await StartAsync(app => app.MapGet("/big", () => Results.Text(payload)));
        await using var _ = (IAsyncDisposable)host;

        var text = await client.GetStringAsync("/big");

        Assert.Equal(payload, text);
        Assert.Contains(logs.Entries, e => e.Message.Contains("truncated, 10000 bytes total"));
    }

    [Fact]
    public async Task Correlation_id_is_echoed_when_safe_and_generated_otherwise()
    {
        var (host, client, _) = await StartAsync(app => app.MapGet("/ping", () => "pong"));
        await using var _h = (IAsyncDisposable)host;

        var withId = new HttpRequestMessage(HttpMethod.Get, "/ping");
        withId.Headers.Add(RequestLoggingOptions.CorrelationHeader, "abc-123-def");
        var echoed = await client.SendAsync(withId);
        Assert.Equal("abc-123-def", echoed.Headers.GetValues(RequestLoggingOptions.CorrelationHeader).Single());

        var hostile = new HttpRequestMessage(HttpMethod.Get, "/ping");
        hostile.Headers.TryAddWithoutValidation(RequestLoggingOptions.CorrelationHeader, "x\" injected=1 <script>");
        var generated = await client.SendAsync(hostile);
        Assert.Matches("^[0-9a-f]{32}$", generated.Headers.GetValues(RequestLoggingOptions.CorrelationHeader).Single());
    }

    [Fact]
    public async Task Unhandled_exception_returns_problem_details_with_correlation_id_and_is_logged()
    {
        var (host, client, logs) = await StartAsync(app =>
            app.MapGet("/boom", string () => throw new InvalidOperationException("db password=secret leaked?")));
        await using var _ = (IAsyncDisposable)host;

        var response = await client.GetAsync("/boom");
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(response.Headers.GetValues(RequestLoggingOptions.CorrelationHeader).Single(), problem!["correlationId"].ToString());
        Assert.DoesNotContain("password", await response.Content.ReadAsStringAsync()); // no internals leak to clients
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
