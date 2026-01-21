using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using DonkeyWork.CodeSandbox.Server.Services;

namespace DonkeyWork.CodeSandbox.Server.Controllers;

using DonkeyWork.CodeSandbox.Contracts.Events;
using DonkeyWork.CodeSandbox.Contracts.Requests;

[ApiController]
[Route("api")]
public class ExecutionController : ControllerBase
{
    private readonly ILogger<ExecutionController> _logger;

    public ExecutionController(ILogger<ExecutionController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute a bash command and stream results via Server-Sent Events (SSE)
    /// </summary>
    [HttpPost("execute")]
    public IResult Execute(
        [FromBody] ExecuteCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Execute endpoint called. Command: {Command}, Timeout: {TimeoutSeconds}s",
            request.Command.Length > 50 ? request.Command[..50] + "..." : request.Command,
            request.TimeoutSeconds
        );

        return TypedResults.ServerSentEvents(
            StreamEvents(request, cancellationToken));
    }

    private async IAsyncEnumerable<SseItem<object>> StreamEvents(
        ExecuteCommand request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = new ManagedProcess(request.Command, request.TimeoutSeconds);

        await foreach (var evt in process.ExecuteAsync().WithCancellation(cancellationToken))
        {
            if (evt is OutputEvent outputEvt)
            {
                _logger.LogInformation(
                    "Streaming output. Pid: {Pid}, Stream: {Stream}, Data: {Data}",
                    outputEvt.Pid,
                    outputEvt.Stream,
                    outputEvt.Data
                );

                yield return new SseItem<object>(outputEvt, nameof(OutputEvent))
                {
                    EventId = Guid.NewGuid().ToString()
                };
            }
            else if (evt is CompletedEvent completedEvt)
            {
                _logger.LogInformation(
                    "Process completed. Pid: {Pid}, ExitCode: {ExitCode}, TimedOut: {TimedOut}",
                    completedEvt.Pid,
                    completedEvt.ExitCode,
                    completedEvt.TimedOut
                );

                yield return new SseItem<object>(completedEvt, nameof(CompletedEvent))
                {
                    EventId = Guid.NewGuid().ToString()
                };
            }
        }
    }
}
