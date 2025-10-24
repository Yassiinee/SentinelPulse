using System.Net.Http;
using System.Text.Json;

namespace SentinelPulse.Dashboard.Resilience
{
    public class ResilientHttpClient
    {
        private readonly HttpClient _httpClient;
        private int _failureCount = 0;
        private DateTime _circuitOpenUntil = DateTime.MinValue;
        private readonly object _lock = new();

        public ResilientHttpClient(IHttpClientFactory factory)
        {
            _httpClient = factory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(2);
        }

        public async Task<string?> GetStringWithResilienceAsync(string url, CancellationToken ct)
        {
            // Circuit breaker: if open, short-circuit and return null
            if (DateTime.UtcNow < _circuitOpenUntil)
            {
                return null; // caller should fallback
            }

            // Simple retry with backoff
            var delays = new[] { 200, 400, 800 }; // ms
            for (int attempt = 0; attempt <= delays.Length; attempt++)
            {
                try
                {
                    var resp = await _httpClient.GetAsync(url, ct);
                    if ((int)resp.StatusCode >= 500)
                    {
                        throw new HttpRequestException($"Server error {(int)resp.StatusCode}");
                    }
                    resp.EnsureSuccessStatusCode();
                    var content = await resp.Content.ReadAsStringAsync(ct);

                    // success -> reset failures
                    lock (_lock)
                    {
                        _failureCount = 0;
                        _circuitOpenUntil = DateTime.MinValue;
                    }
                    return content;
                }
                catch (Exception)
                {
                    // update failures
                    var now = DateTime.UtcNow;
                    bool openCircuit = false;
                    lock (_lock)
                    {
                        _failureCount++;
                        if (_failureCount >= 5)
                        {
                            // open circuit for 15 seconds
                            _circuitOpenUntil = now.AddSeconds(15);
                            openCircuit = true;
                            _failureCount = 0; // reset after opening
                        }
                    }
                    if (openCircuit)
                    {
                        return null; // short-circuit
                    }

                    if (attempt < delays.Length)
                    {
                        await Task.Delay(delays[attempt], ct);
                        continue;
                    }
                    return null; // out of retries -> fallback
                }
            }
            return null;
        }

        public static string BuildFallbackMetricsJson()
        {
            var rand = new Random();
            var services = new[] { "auth-service", "payment-gateway", "order-service", "iot-sensor-1" };
            var payload = new
            {
                timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                services = services.Select(n =>
                {
                    var cpu = rand.NextDouble() * 30 + 20; // 20-50
                    var mem = rand.NextDouble() * 40 + 20; // 20-60
                    var latency = rand.NextDouble() * 200 + 20; // 20-220
                    var errors = rand.NextDouble() * 2; // 0-2
                    var anomalyScore = (latency / 500.0) * 0.5 + (errors / 5.0) * 0.3 + (cpu / 100.0) * 0.2;
                    var anomaly = anomalyScore > 0.75;
                    var status = anomaly ? "unhealthy" : (errors > 1.5 || latency > 180 ? "degraded" : "healthy");
                    return new
                    {
                        name = n,
                        status,
                        cpu_load = Math.Round(cpu, 2),
                        memory_usage = Math.Round(mem, 2),
                        latency_ms = Math.Round(latency, 2),
                        error_rate_pct = Math.Round(errors, 2),
                        anomaly_score = Math.Round(anomalyScore, 3),
                        anomaly
                    };
                })
            };
            return JsonSerializer.Serialize(payload);
        }
    }
}