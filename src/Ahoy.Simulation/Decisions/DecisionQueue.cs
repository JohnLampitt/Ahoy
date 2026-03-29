using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Ahoy.Simulation.Decisions;

/// <summary>
/// Thread-safe bridge between the simulation main thread and the background LLM thread.
///
/// Main thread:   Enqueue() → adds requests to the Channel (background consumes)
///                DrainCompleted() → reads from ConcurrentQueue (background produces)
///
/// Background:    Reads from Channel, runs inference, writes to ConcurrentQueue.
///
/// The main thread never touches the Channel reader or ConcurrentQueue writer.
/// The background thread never touches the Channel writer or ConcurrentQueue reader.
/// </summary>
public sealed class DecisionQueue : IDisposable
{
    private readonly Channel<ActorDecisionRequest> _requestChannel;
    private readonly ConcurrentQueue<ActorDecisionRequest> _completedQueue;
    private readonly CancellationTokenSource _cts = new();

    private readonly IAsyncActorDecisionProvider? _llmProvider;
    private readonly ISyncActorDecisionProvider _fallback;
    private readonly Task _backgroundTask;

    public DecisionQueue(ISyncActorDecisionProvider fallback, IAsyncActorDecisionProvider? llmProvider = null)
    {
        _fallback = fallback;
        _llmProvider = llmProvider;
        _requestChannel = Channel.CreateUnbounded<ActorDecisionRequest>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _completedQueue = new ConcurrentQueue<ActorDecisionRequest>();
        _backgroundTask = Task.Run(BackgroundLoop);
    }

    /// <summary>Enqueue a request for background inference.</summary>
    public void Enqueue(ActorDecisionRequest request)
        => _requestChannel.Writer.TryWrite(request);

    /// <summary>
    /// Called each tick by SimulationEngine: drains all completed decisions
    /// back to the main thread for application.
    /// </summary>
    public IReadOnlyList<ActorDecisionRequest> DrainCompleted()
    {
        var results = new List<ActorDecisionRequest>();
        while (_completedQueue.TryDequeue(out var req))
            results.Add(req);
        return results;
    }

    private async Task BackgroundLoop()
    {
        await foreach (var request in _requestChannel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                ActorDecisionMatrix matrix;
                if (_llmProvider is not null)
                {
                    // LLM inference — provider writes matrix to request.PendingMatrix
                    _llmProvider.EnqueueDecision(request);
                    // For this simple queue, we rely on the provider updating PendingMatrix.
                    // A real impl would await a TaskCompletionSource from the provider.
                    // For now, fall through to sync fallback if matrix not set.
                    matrix = request.PendingMatrix ?? _fallback.ResolveMatrix(request.Context);
                }
                else
                {
                    matrix = _fallback.ResolveMatrix(request.Context);
                }

                request.PendingMatrix = matrix;
                _completedQueue.Enqueue(request);
            }
            catch (Exception)
            {
                // On any error, fall back to sync provider
                request.PendingMatrix = _fallback.ResolveMatrix(request.Context);
                _completedQueue.Enqueue(request);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _requestChannel.Writer.TryComplete();
        _backgroundTask.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
