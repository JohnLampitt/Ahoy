namespace Ahoy.Simulation.Decisions;

/// <summary>
/// Synchronous decision provider — always available, used as fallback.
/// Rule-based: deterministic, low latency, no external dependencies.
/// </summary>
public interface ISyncActorDecisionProvider
{
    ActorDecisionMatrix ResolveMatrix(ActorDecisionContext context);
}

/// <summary>
/// Asynchronous decision provider — LLM-backed, queued for background inference.
/// Enqueue a request and retrieve the result later via DecisionQueue.
/// </summary>
public interface IAsyncActorDecisionProvider
{
    void EnqueueDecision(ActorDecisionRequest request);
}
