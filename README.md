# SentinelPulse

Real-time health monitoring and alerting platform for microservices and IoT. SentinelPulse ingests live metrics, applies resilient fetching and basic anomaly heuristics, and streams updates to a web dashboard via SignalR.

## Features
- SignalR-powered live dashboard (no manual refresh)
- ASP.NET Core Minimal API with Swagger UI
- Dynamic metrics from public APIs (latency/status), mapped to service health
- Resilient dashboard poller with retries/circuit-breaker/timeout + fallback data
- Lightweight UI with service cards and live charts (Chart.js)

## Architecture
- Dashboard: ASP.NET Core Web app + SignalR hub (`SentinelPulse.Dashboard`)
- Backend API: ASP.NET Core Minimal API (`SentinelPulse.Api`)
- Real-time: Dashboard background service polls API and broadcasts `metricsUpdate`
- FE: Static HTML + JS renders cards, latency bar chart, and status doughnut

```
[External Public APIs] -> [SentinelPulse.Api /metrics] -> [Dashboard Poller]
                                                   -> SignalR -> [Browser UI]
```

## Tech Stack
- .NET 9, ASP.NET Core Minimal API
- SignalR
- Swashbuckle (Swagger)
- Chart.js (FE charts)
- Custom resilience (retry, circuit breaker, fallback, timeout)

## Getting Started

### Prerequisites
- .NET SDK 9 (or matching SDK in csproj)
- Internet access for public API calls and FE CDNs

### Run the Backend API
```
dotnet run --project "SentinelPulse.Api" --launch-profile "SentinelPulse.Api"
```
- API base URL: http://localhost:5080
- Swagger UI: http://localhost:5080/swagger
- Live metrics: http://localhost:5080/metrics

### Run the Dashboard
```
dotnet run --project "SentinelPulse.Dashboard"
```
- Open the printed URL (e.g., http://localhost:5034)
- The UI connects to `/hubs/dashboard` and updates every ~2 seconds

### Configuration
- Dashboard polling (edit `SentinelPulse.Dashboard/appsettings.json`):
```json
{
  "Backend": {
    "BaseUrl": "http://localhost:5080",
    "MetricsPath": "/metrics",
    "PollingIntervalSeconds": 2
  }
}
```
- API external targets (edit `SentinelPulse.Api/appsettings.json`):
```json
{
  "ExternalTargets": [
    { "name": "public-apis", "url": "https://api.publicapis.org/entries" },
    { "name": "worldtime",  "url": "https://worldtimeapi.org/api/timezone/Etc/UTC" },
    { "name": "catfact",    "url": "https://catfact.ninja/fact" },
    { "name": "coindesk",   "url": "https://api.coindesk.com/v1/bpi/currentprice.json" }
  ]
}
```

## API Endpoints
- `GET /` → Basic health `{ name, status }`
- `GET /metrics` → Response model:
```json
{
  "timestamp_ms": 1730000000000,
  "services": [
    {
      "name": "coindesk",
      "status": "healthy | degraded | unhealthy",
      "cpu_load": 23.4,
      "memory_usage": 45.1,
      "latency_ms": 180.5,
      "error_rate_pct": 0.0,
      "anomaly_score": 0.42,
      "anomaly": false
    }
  ]
}
```
- Swagger: `/swagger`

## Resilience
- Dashboard uses a `ResilientHttpClient` with:
  - Timeout
  - Retry with backoff
  - Simple circuit breaker
  - Fallback to synthetic metrics payload

## Frontend
- Static HTML under `SentinelPulse.Dashboard/wwwroot/index.html`
- Live charts with Chart.js:
  - Latency by service (bar)
  - Status distribution (doughnut)

## Roadmap / Extensions
- Auth: JWT between dashboard and API
- Charts: rolling time-series and alert banners
- Observability: Prometheus/Grafana integration
- ML: anomaly detection with ML.NET
- Docker: Dockerfiles + docker-compose
- Polly in API outbound HTTP (currently uses timeout + basic logic)

## Development
- Hot reload: `dotnet watch run --project "SentinelPulse.Api"`
- Fix dashboard port via launchSettings or `--urls`:
  - `dotnet run --project "SentinelPulse.Dashboard" --urls "http://localhost:5000"`

## Contributing
- Issues and PRs welcome. Please include steps to reproduce and screenshots for UI changes.

## License
- MIT (or your preferred license). Replace this section if needed.
