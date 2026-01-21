using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DonkeyWork.CodeSandbox.Client;

using DonkeyWork.CodeSandbox.Contracts.Events;
using DonkeyWork.CodeSandbox.Contracts.Requests;

/// <summary>
/// HTTP+SSE client for code execution server using .NET 10's native SSE support
/// </summary>
public class StreamingCodeExecutionClient : ICodeExecutionClient
{
    private readonly HttpClient _httpClient;
    private string? _baseUrl;
    private bool _isConnected;

    public bool IsConnected => _isConnected;

    public StreamingCodeExecutionClient()
    {
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task ConnectAsync(string url, Guid userId, Guid executionId, Guid conversationId, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (_isConnected)
        {
            throw new InvalidOperationException("Already connected");
        }

        _baseUrl = url.TrimEnd('/');
        _isConnected = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
        string command,
        int timeoutSeconds = 300,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _baseUrl == null)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/execute")
        {
            Content = JsonContent.Create(new ExecuteCommand
            {
                Command = command,
                TimeoutSeconds = timeoutSeconds
            })
        };

        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Use .NET 10's native SseParser with custom deserializer
        var parser = SseParser.Create(stream, (eventType, bytes) =>
        {
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            return DeserializeEvent(json, eventType);
        });

        await foreach (var item in parser.EnumerateAsync(cancellationToken))
        {
            if (item.Data != null)
            {
                yield return item.Data;
            }
        }
    }

    private ExecutionEvent DeserializeEvent(string json, string eventType)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Use SSE event type to determine which concrete type to deserialize
        return eventType switch
        {
            nameof(OutputEvent) => JsonSerializer.Deserialize<OutputEvent>(json, options)
                ?? throw new InvalidOperationException($"Failed to deserialize OutputEvent: {json}"),
            nameof(CompletedEvent) => JsonSerializer.Deserialize<CompletedEvent>(json, options)
                ?? throw new InvalidOperationException($"Failed to deserialize CompletedEvent: {json}"),
            _ => throw new InvalidOperationException($"Unknown event type: {eventType}")
        };
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = false;
        _baseUrl = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
