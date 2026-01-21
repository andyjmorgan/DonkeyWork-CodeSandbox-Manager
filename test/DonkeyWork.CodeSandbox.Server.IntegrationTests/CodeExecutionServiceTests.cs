using DonkeyWork.CodeSandbox.Client;
using Xunit;

namespace DonkeyWork.CodeSandbox.Server.IntegrationTests;

using DonkeyWork.CodeSandbox.Contracts.Events;

/// <summary>
/// Comprehensive integration tests for CodeExecutionServer using Testcontainers
/// </summary>
public class CodeExecutionServiceTests : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly ServerFixture _fixture;
    private ICodeExecutionClient _client = null!;

    public CodeExecutionServiceTests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Create and connect a fresh client for each test
        _client = new StreamingCodeExecutionClient();
        await _client.ConnectAsync(
            _fixture.ServerUrl,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        );
    }

    public async Task DisposeAsync()
    {
        // Disconnect and dispose client after each test
        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch
            {
                // Connection may already be closed by server
            }
        }

        _client.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_SimpleEchoCommand_ReturnsOutput()
    {
        // Arrange & Act
        var events = new List<ExecutionEvent>();
        await foreach (var evt in _client.ExecuteAsync("echo 'Hello, World!'"))
        {
            events.Add(evt);
        }

        // Assert
        var outputEvents = events.OfType<OutputEvent>().ToList();
        var completedEvent = events.OfType<CompletedEvent>().Single();

        Assert.NotEmpty(outputEvents);
        Assert.Contains(outputEvents, e => e.Data.Contains("Hello, World!"));
        Assert.True(completedEvent.Pid > 0);
        Assert.Equal(0, completedEvent.ExitCode);
        Assert.False(completedEvent.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_MultiLineOutput_CapturesAllLines()
    {
        // Act
        var outputLines = new List<string>();
        int exitCode = -1;

        await foreach (var evt in _client.ExecuteAsync("echo 'Line 1'; sleep 1; echo 'Line 2'; sleep 1; echo 'Line 3'"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
            {
                outputLines.Add(output.Data);
            }
            else if (evt is CompletedEvent completed)
            {
                exitCode = completed.ExitCode;
            }
        }

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("Line 1", outputLines);
        Assert.Contains("Line 2", outputLines);
        Assert.Contains("Line 3", outputLines);
    }

    [Fact]
    public async Task ExecuteAsync_StderrOutput_CapturesErrorStream()
    {
        // Act
        var errorLines = new List<string>();
        await foreach (var evt in _client.ExecuteAsync("echo 'This is an error' >&2"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stderr)
            {
                errorLines.Add(output.Data);
            }
        }

        // Assert
        Assert.Contains("This is an error", errorLines);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReturnsCorrectExitCode()
    {
        // Act
        CompletedEvent? completedEvent = null;
        await foreach (var evt in _client.ExecuteAsync("exit 42"))
        {
            if (evt is CompletedEvent completed)
            {
                completedEvent = completed;
            }
        }

        // Assert
        Assert.NotNull(completedEvent);
        Assert.Equal(42, completedEvent!.ExitCode);
        Assert.False(completedEvent.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleProcesses_RunConcurrently()
    {
        // Act
        var task1 = CollectEventsAsync(_client, "sleep 2; echo 'Process A done'");
        var task2 = CollectEventsAsync(_client, "sleep 1; echo 'Process B done'");
        var task3 = CollectEventsAsync(_client, "sleep 3; echo 'Process C done'");

        var results = await Task.WhenAll(task1, task2, task3);

        // Assert
        Assert.Equal(3, results.Length);
        Assert.All(results, r => Assert.Equal(0, r.ExitCode));
        Assert.Contains("Process A done", results[0].Stdout);
        Assert.Contains("Process B done", results[1].Stdout);
        Assert.Contains("Process C done", results[2].Stdout);
    }

    [Fact]
    public void IsConnected_AfterConnection_ReturnsTrue()
    {
        // Assert
        Assert.True(_client.IsConnected);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleStderr_CapturesAllErrors()
    {
        // Act
        var errorLines = new List<string>();
        await foreach (var evt in _client.ExecuteAsync("echo 'Error 1' >&2; sleep 1; echo 'Error 2' >&2; sleep 1; echo 'Error 3' >&2"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stderr)
            {
                errorLines.Add(output.Data);
            }
        }

        // Assert
        Assert.Contains("Error 1", errorLines);
        Assert.Contains("Error 2", errorLines);
        Assert.Contains("Error 3", errorLines);
    }

    [Fact]
    public async Task ExecuteAsync_MixedStdoutStderr_CapturesBothStreams()
    {
        // Act
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        await foreach (var evt in _client.ExecuteAsync("echo 'stdout message'; echo 'stderr message' >&2"))
        {
            if (evt is OutputEvent output)
            {
                if (output.Stream == OutputStreamType.Stdout)
                    stdoutLines.Add(output.Data);
                else if (output.Stream == OutputStreamType.Stderr)
                    stderrLines.Add(output.Data);
            }
        }

        // Assert
        Assert.Contains("stdout message", stdoutLines);
        Assert.Contains("stderr message", stderrLines);
    }

    [Fact]
    public async Task ExecuteAsync_ContainsPidInformation()
    {
        // Act
        OutputEvent? outputEvent = null;
        CompletedEvent? completedEvent = null;

        await foreach (var evt in _client.ExecuteAsync("echo testmarker123"))
        {
            if (evt is OutputEvent output && output.Data.Contains("testmarker123"))
            {
                outputEvent = output;
            }
            else if (evt is CompletedEvent completed)
            {
                completedEvent = completed;
            }
        }

        // Assert
        Assert.NotNull(outputEvent);
        Assert.NotNull(completedEvent);
        Assert.Equal(completedEvent!.Pid, outputEvent!.Pid);
        Assert.Equal(OutputStreamType.Stdout, outputEvent.Stream);
    }

    [Fact]
    public async Task ExecuteAsync_CompletedEvent_ContainsCorrectExitCode()
    {
        // Act
        CompletedEvent? completedEvent = null;
        int pid = 0;

        await foreach (var evt in _client.ExecuteAsync("exit 7"))
        {
            if (evt is OutputEvent output)
            {
                pid = output.Pid;
            }
            else if (evt is CompletedEvent completed)
            {
                completedEvent = completed;
            }
        }

        // Assert
        Assert.NotNull(completedEvent);
        Assert.True(completedEvent!.Pid > 0);
        Assert.Equal(7, completedEvent.ExitCode);
        Assert.False(completedEvent.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_VeryLongOutput_CapturesEverything()
    {
        // Act - Generate 50 lines of output with small delays
        var outputLines = new List<string>();
        int exitCode = -1;

        await foreach (var evt in _client.ExecuteAsync("for i in $(seq 1 50); do echo \"Line $i\"; sleep 0.01; done"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
            {
                outputLines.Add(output.Data);
            }
            else if (evt is CompletedEvent completed)
            {
                exitCode = completed.ExitCode;
            }
        }

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains(outputLines, line => line.Contains("Line 1"));
        Assert.Contains(outputLines, line => line.Contains("Line 25"));
        Assert.Contains(outputLines, line => line.Contains("Line 50"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCommand_HandlesGracefully()
    {
        // Act
        CompletedEvent? completedEvent = null;
        await foreach (var evt in _client.ExecuteAsync(""))
        {
            if (evt is CompletedEvent completed)
            {
                completedEvent = completed;
            }
        }

        // Assert - Empty command should complete with exit code 0
        Assert.NotNull(completedEvent);
        Assert.Equal(0, completedEvent!.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_CommandWithEnvironmentVariables_UsesVariables()
    {
        // Act
        var outputLines = new List<string>();
        await foreach (var evt in _client.ExecuteAsync("TEST_VAR=hello; echo $TEST_VAR"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
            {
                outputLines.Add(output.Data);
            }
        }

        // Assert
        Assert.Contains("hello", outputLines);
    }

    [Fact]
    public async Task ExecuteAsync_PipedCommands_ExecutesCorrectly()
    {
        // Act
        var outputLines = new List<string>();
        await foreach (var evt in _client.ExecuteAsync("echo hello world | grep world"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
            {
                outputLines.Add(output.Data);
            }
        }

        // Assert
        Assert.Contains(outputLines, line => line.Contains("hello world"));
    }

    [Fact]
    public async Task ExecuteAsync_BackgroundProcess_CompletesSuccessfully()
    {
        // Act - Sleep in background, then continue
        var outputLines = new List<string>();
        int exitCode = -1;

        await foreach (var evt in _client.ExecuteAsync("(sleep 1 &); echo 'done'"))
        {
            if (evt is OutputEvent output && output.Stream == OutputStreamType.Stdout)
            {
                outputLines.Add(output.Data);
            }
            else if (evt is CompletedEvent completed)
            {
                exitCode = completed.ExitCode;
            }
        }

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("done", outputLines);
    }

    private static async Task<(int Pid, int ExitCode, string Stdout, string Stderr)> CollectEventsAsync(
        ICodeExecutionClient client,
        string command)
    {
        var stdout = new List<string>();
        var stderr = new List<string>();
        int pid = 0;
        int exitCode = 0;

        await foreach (var evt in client.ExecuteAsync(command))
        {
            if (evt is OutputEvent output)
            {
                pid = output.Pid;
                if (output.Stream == OutputStreamType.Stdout)
                    stdout.Add(output.Data);
                else if (output.Stream == OutputStreamType.Stderr)
                    stderr.Add(output.Data);
            }
            else if (evt is CompletedEvent completed)
            {
                pid = completed.Pid;
                exitCode = completed.ExitCode;
            }
        }

        return (pid, exitCode, string.Join('\n', stdout), string.Join('\n', stderr));
    }
}
