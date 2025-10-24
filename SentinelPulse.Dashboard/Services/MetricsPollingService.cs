using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPulse.Dashboard.Hubs;
using SentinelPulse.Dashboard.Resilience;

namespace SentinelPulse.Dashboard.Services
{
    public class MetricsPollingService : BackgroundService
    {
        private readonly ILogger<MetricsPollingService> _logger;
        private readonly IHubContext<DashboardHub> _hubContext;
        private readonly ResilientHttpClient _client;
        private readonly IConfiguration _config;

        public MetricsPollingService(
            ILogger<MetricsPollingService> logger,
            IHubContext<DashboardHub> hubContext,
            ResilientHttpClient client,
            IConfiguration config)
        {
            _logger = logger;
            _hubContext = hubContext;
            _client = client;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var baseUrl = _config["Backend:BaseUrl"] ?? "http://127.0.0.1:8000";
            var metricsPath = _config["Backend:MetricsPath"] ?? "/metrics";
            var intervalSec = int.TryParse(_config["Backend:PollingIntervalSeconds"], out var s) ? s : 2;
            var url = CombineUrl(baseUrl, metricsPath);

            _logger.LogInformation("MetricsPollingService started. Polling {Url} every {Interval}s", url, intervalSec);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var json = await _client.GetStringWithResilienceAsync(url, stoppingToken);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        json = ResilientHttpClient.BuildFallbackMetricsJson();
                    }

                    // Validate it's JSON to avoid sending garbage
                    using var doc = JsonDocument.Parse(json);

                    await _hubContext.Clients.All.SendAsync("metricsUpdate", json, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch/broadcast metrics");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // ignore on shutdown
                }
            }
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrEmpty(baseUrl)) return path;
            if (string.IsNullOrEmpty(path)) return baseUrl;
            return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }
    }
}
