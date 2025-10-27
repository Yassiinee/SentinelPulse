using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentinelPulse.Dashboard.Hubs;
using SentinelPulse.Dashboard.Resilience;
using Grpc.Net.Client;
using Google.Protobuf;
using SentinelPulse.Grpc;
using Grpc.Core;

namespace SentinelPulse.Dashboard.Services
{
    public class MetricsPollingService : BackgroundService
    {
        private readonly ILogger<MetricsPollingService> _logger;
        private readonly IHubContext<DashboardHub> _hubContext;
        private readonly IConfiguration _config;

        public MetricsPollingService(
            ILogger<MetricsPollingService> logger,
            IHubContext<DashboardHub> hubContext,
            IConfiguration config)
        {
            _logger = logger;
            _hubContext = hubContext;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var apiBase = _config["Backend:BaseUrl"] ?? "http://localhost:5080";
            _logger.LogInformation("MetricsPollingService (gRPC) connecting to {Api}", apiBase);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Create a gRPC channel (HTTP/2 over plaintext on localhost)
                    using var channel = GrpcChannel.ForAddress(apiBase);
                    var client = new MetricsService.MetricsServiceClient(channel);

                    using var call = client.StreamMetrics(new Empty(), cancellationToken: stoppingToken);
                    while (await call.ResponseStream.MoveNext(stoppingToken))
                    {
                        var msg = call.ResponseStream.Current;
                        var json = JsonFormatter.Default.Format(msg);
                        await _hubContext.Clients.All.SendAsync("metricsUpdate", json, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "gRPC stream disconnected. Reconnecting in 2s...");
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { }
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
