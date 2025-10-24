using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentinelPulse.Dashboard.Hubs;
using SentinelPulse.Dashboard.Resilience;
using SentinelPulse.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ResilientHttpClient>();
builder.Services.AddHostedService<MetricsPollingService>();

var app = builder.Build();

// Middleware
app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoints
app.MapHub<DashboardHub>("/hubs/dashboard");

// Basic health endpoint
app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();
