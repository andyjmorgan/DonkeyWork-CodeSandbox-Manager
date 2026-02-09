using DonkeyWork.CodeSandbox.AuthProxy.Configuration;
using DonkeyWork.CodeSandbox.AuthProxy.Health;
using DonkeyWork.CodeSandbox.AuthProxy.Proxy;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

Log.Information("Starting Auth Proxy Sidecar");

// Bind configuration
var proxyConfig = new ProxyConfiguration();
builder.Configuration.GetSection(nameof(ProxyConfiguration)).Bind(proxyConfig);
builder.Services.AddSingleton(proxyConfig);

// Configure Kestrel to listen on the health port
builder.WebHost.UseUrls($"http://0.0.0.0:{proxyConfig.HealthPort}");

// Load or generate CA certificate
using var loggerFactory = LoggerFactory.Create(lb => lb.AddSerilog(Log.Logger));
var startupLogger = loggerFactory.CreateLogger("AuthProxy.Startup");
var caCert = CertificateGenerator.LoadOrGenerateCaCertificate(
    proxyConfig.CaCertificatePath,
    proxyConfig.CaPrivateKeyPath,
    startupLogger);

// Register services
builder.Services.AddSingleton(sp => new CertificateGenerator(
    caCert,
    sp.GetRequiredService<ILogger<CertificateGenerator>>()));

builder.Services.AddSingleton<TlsMitmHandler>();
builder.Services.AddHostedService<ProxyServer>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Map health endpoints
app.MapHealthEndpoints();
app.MapHealthChecks("/health");

Log.Information("Auth Proxy configured: proxy port {ProxyPort}, health port {HealthPort}, allowed domains: {Domains}",
    proxyConfig.ProxyPort, proxyConfig.HealthPort, string.Join(", ", proxyConfig.AllowedDomains));

await app.RunAsync();
