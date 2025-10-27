using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentinelPulse.Api.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Bind to a fixed port with HTTP/1.1 + HTTP/2 (for REST + gRPC)
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenLocalhost(5080, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Outbound HTTP
builder.Services.AddHttpClient();

// gRPC
builder.Services.AddGrpc();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
        policy.WithOrigins(
                "http://localhost:5000",
                "https://localhost:5001",
                "http://127.0.0.1:5000",
                "https://127.0.0.1:5001")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.WriteIndented = false;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Swagger middleware
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("Dashboard");

// Map gRPC service
app.MapGrpcService<MetricsServiceImpl>();

app.MapGet("/metrics", async (IHttpClientFactory httpFactory, IConfiguration config) =>
{
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(3);

    var targets = config.GetSection("ExternalTargets").Get<ExternalTarget[]>()
                 ?? Array.Empty<ExternalTarget>();
    if (targets.Length == 0)
    {
        targets = new[]
        {
            new ExternalTarget { name = "public-apis", url = "https://api.publicapis.org/entries" },
            new ExternalTarget { name = "worldtime", url = "https://worldtimeapi.org/api/timezone/Etc/UTC" },
            new ExternalTarget { name = "catfact", url = "https://catfact.ninja/fact" },
            new ExternalTarget { name = "coindesk", url = "https://api.coindesk.com/v1/bpi/currentprice.json" },
        };
    }

    // Run calls in parallel
    var tasks = targets.Select(async t =>
    {
        var sw = Stopwatch.StartNew();
        int statusCode = 0;
        int contentLength = 0;
        string status = "unhealthy";
        double errorsPct = 100;
        try
        {
            using var resp = await client.GetAsync(t.url, HttpCompletionOption.ResponseHeadersRead);
            statusCode = (int)resp.StatusCode;
            if ((int)resp.StatusCode >= 500)
            {
                // server error
            }
            else if (resp.IsSuccessStatusCode)
            {
                // read a small portion to estimate size without heavy processing
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                contentLength = bytes?.Length ?? 0;
                status = "healthy";
                errorsPct = 0;
            }
            else
            {
                // client error
                status = "degraded";
                errorsPct = 50;
            }
        }
        catch
        {
            // timeout or network error -> unhealthy
            status = "unhealthy";
            errorsPct = 100;
        }
        finally
        {
            sw.Stop();
        }

        var latency = sw.Elapsed.TotalMilliseconds;
        // Derive synthetic resource usage from response size to keep fields populated
        var cpu = Math.Clamp(contentLength / 50000.0 * 80, 2, 85); // 0..~80% scaled
        var mem = Math.Clamp(contentLength / 100000.0 * 90, 3, 90); // 0..~90% scaled

        var anomalyScore = (latency / 1500.0) * 0.6 + (errorsPct / 100.0) * 0.4;
        var anomaly = anomalyScore > 0.75;
        // Escalate status by latency thresholds
        if (status == "healthy" && (latency > 600 || statusCode >= 400)) status = "degraded";
        if (latency > 1200) status = "unhealthy";

        return new RestServiceMetric(
            t.name,
            status,
            Math.Round(cpu, 2),
            Math.Round(mem, 2),
            Math.Round(latency, 2),
            Math.Round(errorsPct, 2),
            Math.Round(anomalyScore, 3),
            anomaly
        );
    });

    var services = await Task.WhenAll(tasks);
    return Results.Json(new RestMetricsResponse(nowMs, services));
});

app.MapGet("/", () => Results.Json(new { name = "SentinelPulse.Api", status = "ok" }));

app.Run();

public record RestServiceMetric(
    string name,
    string status,
    double cpu_load,
    double memory_usage,
    double latency_ms,
    double error_rate_pct,
    double anomaly_score,
    bool anomaly
);

public record RestMetricsResponse(long timestamp_ms, IEnumerable<RestServiceMetric> services);

public class ExternalTarget
{
    public string name { get; set; } = string.Empty;
    public string url { get; set; } = string.Empty;
}
