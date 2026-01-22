namespace DonkeyWork.CodeSandbox.Manager.Filters;

public class ApiKeyEndpointFilter : IEndpointFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<ApiKeyEndpointFilter>>();

        var configuredApiKey = configuration.GetValue<string>("ApiKey");

        if (string.IsNullOrEmpty(configuredApiKey))
        {
            logger.LogWarning("API key is not configured in appsettings. Denying access to protected endpoint.");
            return Results.Problem(
                detail: "API key is not configured on the server",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Configuration Error");
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedApiKey))
        {
            logger.LogWarning("API key header missing for protected endpoint: {Path}", context.HttpContext.Request.Path);
            return Results.Problem(
                detail: $"API key is required. Provide it in the '{ApiKeyHeaderName}' header.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized");
        }

        if (!string.Equals(configuredApiKey, providedApiKey.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning("Invalid API key provided for protected endpoint: {Path}", context.HttpContext.Request.Path);
            return Results.Problem(
                detail: "Invalid API key",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized");
        }

        return await next(context);
    }
}
