using DonkeyWork.CodeSandbox.McpServer.Endpoints;
using DonkeyWork.CodeSandbox.McpServer.Services;
using Serilog;
using Serilog.Formatting.Compact;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        new CompactJsonFormatter(),
        "logs/mcp-server-.log",
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// StdioBridge is a singleton - one MCP process per container
builder.Services.AddSingleton<StdioBridge>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.WebHost.UseUrls("http://0.0.0.0:8666");
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors();
app.MapMcpEndpoints();
app.UseHealthChecks("/healthz");

var assembly = typeof(StdioBridge).Assembly;
var version = assembly.GetName().Version?.ToString() ?? "unknown";
var informationalVersion = assembly.GetCustomAttributes(false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion ?? version;
Log.Information("Starting MCP stdio-to-HTTP bridge on port 8666 (build: {Version})", informationalVersion);
await app.RunAsync();
