using System.Diagnostics;
using System.Threading.Channels;
using DonkeyWork.CodeSandbox.Contracts;

namespace DonkeyWork.CodeSandbox.Server.Services;

using DonkeyWork.CodeSandbox.Contracts.Events;

public class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _timeoutCts;
    private readonly int _timeoutSeconds;
    private readonly string _command;
    private int _pid;
    private bool _timedOut;
    private bool _disposed;

    public int Pid => _pid;

    public ManagedProcess(string command, int timeoutSeconds)
    {
        _command = command;
        _timeoutSeconds = timeoutSeconds;
        _timeoutCts = new CancellationTokenSource();

        // Use /home/user if it exists (production), otherwise fall back to HOME env var or temp directory
        var workingDirectory = Directory.Exists("/home/user")
            ? "/home/user"
            : Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true,
        };
    }

    /// <summary>
    /// Executes the process and streams all events (output + completion) in real-time
    /// </summary>
    public async IAsyncEnumerable<ExecutionEvent> ExecuteAsync()
    {
        var channel = Channel.CreateUnbounded<ExecutionEvent>();

        // Subscribe to output events and push to channel
        DataReceivedEventHandler outputHandler = (sender, e) =>
        {
            if (e.Data != null)
            {
                channel.Writer.TryWrite(new OutputEvent
                {
                    Pid = _pid,
                    Stream = OutputStreamType.Stdout,
                    Data = e.Data
                });
            }
        };

        DataReceivedEventHandler errorHandler = (sender, e) =>
        {
            if (e.Data != null)
            {
                channel.Writer.TryWrite(new OutputEvent
                {
                    Pid = _pid,
                    Stream = OutputStreamType.Stderr,
                    Data = e.Data
                });
            }
        };

        _process.OutputDataReceived += outputHandler;
        _process.ErrorDataReceived += errorHandler;

        // Start process execution
        var executionTask = ExecuteProcessAsync(channel.Writer, outputHandler, errorHandler);

        // Stream events to caller as they arrive (true streaming!)
        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            yield return evt;
        }

        // Ensure execution completes
        await executionTask;
    }

    private async Task ExecuteProcessAsync(
        ChannelWriter<ExecutionEvent> writer,
        DataReceivedEventHandler outputHandler,
        DataReceivedEventHandler errorHandler)
    {
        try
        {
            // Start the process
            _process.Start();
            _pid = _process.Id;

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Set up automatic cancellation after timeout
            _timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            try
            {
                await _process.WaitForExitAsync(_timeoutCts.Token);
            }
            catch (TaskCanceledException)
            {
                // Timeout occurred
                _timedOut = true;
                KillProcessTree(_pid);
                await _process.WaitForExitAsync(); // Wait for kill to complete
            }

            // CRITICAL: WaitForExitAsync() only waits for process exit, not for event handlers to finish
            // Call again to ensure all OutputDataReceived events have been processed
            await _process.WaitForExitAsync();

            // NOW all output is truly flushed and event handlers have completed
            // Send completion event
            writer.TryWrite(new CompletedEvent
            {
                Pid = _pid,
                ExitCode = _process.ExitCode,
                TimedOut = _timedOut
            });
        }
        finally
        {
            _process.OutputDataReceived -= outputHandler;
            _process.ErrorDataReceived -= errorHandler;
            writer.Complete();
        }
    }

    private void KillProcessTree(int pid)
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore if already exited
        }
        // TODO: Add logging for kill operations
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _timeoutCts.Cancel();
        _timeoutCts.Dispose();

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore
            }
        }

        _process.Dispose();
    }
}
