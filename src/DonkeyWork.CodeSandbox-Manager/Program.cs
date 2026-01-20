using DonkeyWork.CodeSandbox_Manager.Configuration;
using DonkeyWork.CodeSandbox_Manager.Endpoints;
using DonkeyWork.CodeSandbox_Manager.Services;
using k8s;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure listening address and port
builder.WebHost.UseUrls("http://0.0.0.0:8668");

// Add Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

Log.Information("Starting Kata Container Manager API");

// Configure and validate KubernetesConfig with IOptions
builder.Services.AddOptions<KataContainerManager>()
    .BindConfiguration(nameof(KataContainerManager))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Register Kubernetes client
builder.Services.AddSingleton<IKubernetes>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var config = sp.GetRequiredService<IOptions<KataContainerManager>>().Value;

    // Priority 1: Try in-cluster configuration
    try
    {
        var k8sConfig = KubernetesClientConfiguration.InClusterConfig();
        logger.LogInformation("Using in-cluster Kubernetes configuration");
        return new Kubernetes(k8sConfig);
    }
    catch (Exception)
    {
        logger.LogInformation("Not running in-cluster, checking for direct connection config");
    }

    // Priority 2: Use direct connection from appsettings
    if (config.Connection?.ServerUrl != null && config.Connection?.Token != null)
    {
        logger.LogInformation("Using direct Kubernetes connection: {ServerUrl}", config.Connection.ServerUrl);
        var k8sConfig = new KubernetesClientConfiguration
        {
            Host = config.Connection.ServerUrl,
            AccessToken = config.Connection.Token,
            SkipTlsVerify = config.Connection.SkipTlsVerify
        };
        return new Kubernetes(k8sConfig);
    }

    // Priority 3: Fall back to kubeconfig
    logger.LogInformation("Using kubeconfig from default location");
    var defaultConfig = KubernetesClientConfiguration.BuildDefaultConfig();
    return new Kubernetes(defaultConfig);
});

// Register HTTP client for passthrough requests
builder.Services.AddHttpClient();

// Register application services
builder.Services.AddScoped<IKataContainerService, KataContainerService>();

// Add health checks
builder.Services.AddHealthChecks();

// Add OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Validate configuration on startup
var config = app.Services.GetRequiredService<IOptions<KataContainerManager>>().Value;
Log.Information("Configuration loaded: Namespace={Namespace}, RuntimeClass={RuntimeClass}",
    config.TargetNamespace, config.RuntimeClassName);

// Configure HTTP pipeline
app.UseSerilogRequestLogging();

// Enable OpenAPI and Scalar
app.MapOpenApi();
app.MapScalarApiReference("/scalar/v1", (options, context) =>
{
    // Dynamically add server URLs based on request scheme
    // If accessed via HTTPS (through ingress), add HTTPS server first
    if (context.Request.Scheme == "https")
        options.AddServer(new ScalarServer($"https://{context.Request.Host}"));
    options.AddServer(new ScalarServer($"http://{context.Request.Host}"));
});
Log.Information("API documentation enabled at /scalar/v1");

app.UseHttpsRedirection();

// Map health checks
app.MapHealthChecks("/healthz");

// Map endpoints
app.MapKataContainerEndpoints();

Log.Information("Kata Container Manager API started successfully");
await app.RunAsync();