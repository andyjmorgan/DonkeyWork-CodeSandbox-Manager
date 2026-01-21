# Code Execution Client

A modern .NET 10 client library for executing bash commands via HTTP+SSE (Server-Sent Events) with an intuitive async streaming interface.

## Features

- **Async Streaming**: Execute commands and stream events as they arrive using `IAsyncEnumerable<ExecutionEvent>`
- **Native SSE Support**: Uses .NET 10's built-in `System.Net.ServerSentEvents` for spec-compliant SSE parsing
- **Type-Safe Streams**: `OutputStreamType` enum (Stdout/Stderr) instead of strings
- **Concurrent Execution**: Run multiple commands in parallel using `Task.WhenAll`
- **Timeout Support**: Set per-command timeouts with automatic process termination
- **Simple API**: Clean, minimal interface with just Connect, Execute, and Disconnect

## Installation

Add a project reference to `CodeExecutionClient.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/CodeExecutionClient/CodeExecutionClient.csproj" />
</ItemGroup>
```

## Quick Start

```csharp
using DonkeyWork.CodeExecutionClient;
using DonkeyWork.CodeExecutionServer.Contracts;

// Create and connect client
using ICodeExecutionClient client = new StreamingCodeExecutionClient();
await client.ConnectAsync(
    "http://localhost:8666",
    Guid.NewGuid(),  // userId
    Guid.NewGuid(),  // executionId
    Guid.NewGuid(),  // conversationId
    Guid.NewGuid()   // agentId
);

// Execute and stream events
await foreach (var evt in client.ExecuteAsync("echo 'Hello, World!'"))
{
    if (evt is OutputEvent output)
    {
        Console.WriteLine($"[{output.Stream}] {output.Data}");
    }
    else if (evt is CompletedEvent completed)
    {
        Console.WriteLine($"Exit code: {completed.ExitCode}");
    }
}

await client.DisconnectAsync();
```

## Usage Examples

### Basic Command Execution

```csharp
await foreach (var evt in client.ExecuteAsync("ls -la"))
{
    if (evt is OutputEvent output)
    {
        Console.WriteLine($"{output.Data}");
    }
    else if (evt is CompletedEvent completed)
    {
        if (completed.ExitCode != 0)
        {
            Console.Error.WriteLine($"Command failed with exit code {completed.ExitCode}");
        }
    }
}
```

### Streaming Output in Real-Time

```csharp
// Output streams as it arrives
await foreach (var evt in client.ExecuteAsync("for i in {1..5}; do echo $i; sleep 1; done"))
{
    if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
    {
        Console.WriteLine($"Got: {output.Data}");
    }
}
```

### Collecting Complete Output

```csharp
var stdoutLines = new List<string>();
var stderrLines = new List<string>();
int exitCode = 0;

await foreach (var evt in client.ExecuteAsync(command))
{
    if (evt is OutputEvent output)
    {
        if (output.Stream == OutputStreamType.Stdout)
            stdoutLines.Add(output.Data);
        else if (output.Stream == OutputStreamType.Stderr)
            stderrLines.Add(output.Data);
    }
    else if (evt is CompletedEvent completed)
    {
        exitCode = completed.ExitCode;
    }
}

Console.WriteLine("=== STDOUT ===");
Console.WriteLine(string.Join('\n', stdoutLines));
Console.WriteLine("=== STDERR ===");
Console.WriteLine(string.Join('\n', stderrLines));
Console.WriteLine($"Exit code: {exitCode}");
```

### Concurrent Execution

```csharp
// Run multiple commands in parallel
var task1 = CollectOutputAsync(client, "sleep 2; echo 'Task 1'");
var task2 = CollectOutputAsync(client, "sleep 1; echo 'Task 2'");
var task3 = CollectOutputAsync(client, "sleep 3; echo 'Task 3'");

var results = await Task.WhenAll(task1, task2, task3);

foreach (var (output, exitCode) in results)
{
    Console.WriteLine($"Output: {output}, Exit: {exitCode}");
}

static async Task<(string Output, int ExitCode)> CollectOutputAsync(
    ICodeExecutionClient client,
    string command)
{
    var output = new StringBuilder();
    int exitCode = 0;

    await foreach (var evt in client.ExecuteAsync(command))
    {
        if (evt is OutputEvent outputEvt && outputEvt.Stream == OutputStreamType.Stdout)
        {
            output.AppendLine(outputEvt.Data);
        }
        else if (evt is CompletedEvent completed)
        {
            exitCode = completed.ExitCode;
        }
    }

    return (output.ToString().Trim(), exitCode);
}
```

### Timeout Handling

```csharp
await foreach (var evt in client.ExecuteAsync("sleep 100", timeoutSeconds: 5))
{
    if (evt is CompletedEvent completed)
    {
        if (completed.TimedOut)
        {
            Console.WriteLine("Command timed out!");
        }
        else
        {
            Console.WriteLine($"Completed with exit code {completed.ExitCode}");
        }
    }
}
```

### Error Handling

```csharp
try
{
    await foreach (var evt in client.ExecuteAsync(command))
    {
        if (evt is OutputEvent output && output.Stream == OutputStreamType.Stderr)
        {
            Console.Error.WriteLine($"Error: {output.Data}");
        }
        else if (evt is CompletedEvent completed && completed.ExitCode != 0)
        {
            Console.Error.WriteLine($"Command failed with exit code {completed.ExitCode}");
        }
    }
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Network error: {ex.Message}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
}
```

## API Reference

### ICodeExecutionClient Interface

#### Methods

**ConnectAsync**
```csharp
Task ConnectAsync(
    string url,
    Guid userId,
    Guid executionId,
    Guid conversationId,
    Guid agentId,
    CancellationToken cancellationToken = default)
```
Connects to the code execution server. The connection parameters (userId, etc.) are stored but not currently used for authentication.

**ExecuteAsync**
```csharp
IAsyncEnumerable<ExecutionEvent> ExecuteAsync(
    string command,
    int timeoutSeconds = 300,
    CancellationToken cancellationToken = default)
```
Executes a bash command and streams execution events as they arrive. Returns an async enumerable of events (OutputEvent and CompletedEvent).

**DisconnectAsync**
```csharp
Task DisconnectAsync(CancellationToken cancellationToken = default)
```
Disconnects from the server (currently a no-op but maintains the interface contract).

#### Properties

**IsConnected**
```csharp
bool IsConnected { get; }
```
Returns true if the client is connected to the server.

### Event Types

**ExecutionEvent** (abstract base class)
```csharp
public abstract class ExecutionEvent
{
    public int Pid { get; set; }
}
```

**OutputEvent**
```csharp
public class OutputEvent : ExecutionEvent
{
    public OutputStreamType Stream { get; set; }  // Stdout or Stderr
    public string Data { get; set; }
}
```

**CompletedEvent**
```csharp
public class CompletedEvent : ExecutionEvent
{
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
}
```

**OutputStreamType** (enum)
```csharp
public enum OutputStreamType
{
    Stdout,
    Stderr
}
```

## Architecture

The client uses .NET 10's native SSE support:

1. **HTTP POST** to `/api/execute` with command and timeout
2. **Server responds** with `text/event-stream` content type
3. **SseParser** (native .NET 10) parses the SSE stream
4. **Events deserialized** to strongly-typed ExecutionEvent objects
5. **Yielded as IAsyncEnumerable** for consumption with `await foreach`

### SSE Event Format

The server sends events in standard SSE format:

```
data: {"pid":12345,"stream":"Stdout","data":"Hello"}

data: {"pid":12345,"exitCode":0,"timedOut":false}

```

The client automatically:
- Parses the SSE format
- Deserializes JSON to C# objects
- Discriminates between OutputEvent and CompletedEvent
- Handles connection lifecycle

## Advantages Over Previous Versions

**Before (WebSocket + JSON-RPC + Events):**
```csharp
// Complex setup with events
client.OutputReceived += (output) => { /* handler */ };
client.ProcessCompleted += (completed) => { /* handler */ };
var result = await client.ExecuteAsync(command);
// Result doesn't include output, need to collect from events
```

**Now (HTTP+SSE + IAsyncEnumerable):**
```csharp
// Simple, idiomatic streaming
await foreach (var evt in client.ExecuteAsync(command))
{
    // Process events as they arrive
}
// Clean, composable, cancellable
```

Benefits:
- ✅ **Simpler**: No event subscriptions, no cleanup
- ✅ **Standard**: Uses familiar `await foreach` pattern
- ✅ **Composable**: Easy to combine with LINQ, channels, etc.
- ✅ **Cancellable**: Built-in cancellation token support
- ✅ **Type-safe**: Enum for stream types instead of strings

## Testing

The client is thoroughly tested with integration tests using Testcontainers:

```bash
# Run integration tests (requires Docker)
dotnet test test/CodeExecutionServer.IntegrationTests/
```

Test coverage includes:
- Simple commands
- Multi-line output
- Stderr capture
- Non-zero exit codes
- Timeouts
- Concurrent execution
- Long output
- Edge cases (empty commands, pipes, environment variables)

## Performance

- **Low latency**: Output streams with minimal buffering
- **Memory efficient**: Events processed as they arrive, not buffered
- **Concurrent**: Multiple commands can run simultaneously
- **Native performance**: Uses .NET 10's optimized SSE parser

## Dependencies

- **.NET 10.0** - Required for native SSE support
- **System.Net.ServerSentEvents** - Built into .NET 10
- **System.Text.Json** - Built into .NET 10

No external NuGet packages required!

## Example Project

See the full working example in `samples/CodeExecutionClient.Sample/Program.cs`:

```bash
dotnet run --project samples/CodeExecutionClient.Sample/CodeExecutionClient.Sample.csproj
```

## Troubleshooting

**"Client is not connected"**
```csharp
// Make sure to call ConnectAsync first
await client.ConnectAsync("http://localhost:8666", ...);
```

**Events not arriving**
```csharp
// Make sure to enumerate the IAsyncEnumerable
await foreach (var evt in client.ExecuteAsync(command))
{
    // Process events
}
```

**Server not responding**
```bash
# Check if server is running
curl http://localhost:8666/api/health
```

## Contributing

Contributions are welcome! Please:
1. Follow the existing code style
2. Add tests for new features
3. Update documentation

## License

MIT License - See LICENSE file for details
