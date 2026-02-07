namespace DonkeyWork.CodeSandbox.AuthProxy.Health;

public static class HealthEndpoint
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        return app;
    }
}
