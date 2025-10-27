using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentinelPulse.Grpc;
using System.Diagnostics;

namespace SentinelPulse.Api.Services
{
    public class MetricsServiceImpl : MetricsService.MetricsServiceBase
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<MetricsServiceImpl> _logger;

        public MetricsServiceImpl(IHttpClientFactory httpFactory, IConfiguration config, ILogger<MetricsServiceImpl> logger)
        {
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        public override async Task<MetricsResponse> GetMetrics(Empty request, ServerCallContext context)
        {
            var (timestamp, services) = await FetchMetricsAsync(context.CancellationToken);
            var response = new MetricsResponse { TimestampMs = timestamp };
            response.Services.AddRange(services);
            return response;
        }

        public override async Task StreamMetrics(Empty request, IServerStreamWriter<MetricsResponse> responseStream, ServerCallContext context)
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    var (timestamp, services) = await FetchMetricsAsync(context.CancellationToken);
                    var response = new MetricsResponse { TimestampMs = timestamp };
                    response.Services.AddRange(services);
                    await responseStream.WriteAsync(response);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "StreamMetrics iteration failed");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
                }
                catch (TaskCanceledException) { }
            }
        }

        private async Task<(long timestamp, IEnumerable<ServiceMetric> services)> FetchMetricsAsync(CancellationToken ct)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);

            var targets = _config.GetSection("ExternalTargets").Get<ExternalTarget[]>() ?? Array.Empty<ExternalTarget>();
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

            var tasks = targets.Select(async t =>
            {
                var sw = Stopwatch.StartNew();
                int statusCode = 0;
                int contentLength = 0;
                string status = "unhealthy";
                double errorsPct = 100;
                try
                {
                    using var resp = await client.GetAsync(t.url, HttpCompletionOption.ResponseHeadersRead, ct);
                    statusCode = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        contentLength = bytes?.Length ?? 0;
                        status = "healthy";
                        errorsPct = 0;
                    }
                    else if (statusCode >= 400 && statusCode < 500)
                    {
                        status = "degraded";
                        errorsPct = 50;
                    }
                }
                catch
                {
                    status = "unhealthy";
                    errorsPct = 100;
                }
                finally { sw.Stop(); }

                var latency = sw.Elapsed.TotalMilliseconds;
                var cpu = Math.Clamp(contentLength / 50000.0 * 80, 2, 85);
                var mem = Math.Clamp(contentLength / 100000.0 * 90, 3, 90);
                var anomalyScore = (latency / 1500.0) * 0.6 + (errorsPct / 100.0) * 0.4;
                var anomaly = anomalyScore > 0.75;
                if (status == "healthy" && (latency > 600 || statusCode >= 400)) status = "degraded";
                if (latency > 1200) status = "unhealthy";

                return new ServiceMetric
                {
                    Name = t.name,
                    Status = status,
                    CpuLoad = Math.Round(cpu, 2),
                    MemoryUsage = Math.Round(mem, 2),
                    LatencyMs = Math.Round(latency, 2),
                    ErrorRatePct = Math.Round(errorsPct, 2),
                    AnomalyScore = Math.Round(anomalyScore, 3),
                    Anomaly = anomaly
                };
            });

            var services = await Task.WhenAll(tasks);
            return (nowMs, services);
        }
    }
}
