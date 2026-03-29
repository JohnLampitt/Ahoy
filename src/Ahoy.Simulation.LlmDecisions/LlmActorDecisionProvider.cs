using Ahoy.Simulation.Decisions;

namespace Ahoy.Simulation.LlmDecisions;

/// <summary>
/// LLM-backed asynchronous decision provider using LLamaSharp + Phi-3 Mini.
/// This is a stub — full implementation requires the LLamaSharp NuGet package.
///
/// To enable: add LLamaSharp to Ahoy.Simulation.LlmDecisions.csproj and
/// implement the inference logic below.
/// </summary>
public sealed class LlmActorDecisionProvider : IAsyncActorDecisionProvider
{
    private readonly string _modelPath;

    public LlmActorDecisionProvider(string modelPath)
    {
        _modelPath = modelPath;
    }

    public void EnqueueDecision(ActorDecisionRequest request)
    {
        // TODO: Build prompt via PromptBuilder, run inference via LLamaSharp,
        // parse response via ResponseParser, write result to request.PendingMatrix.
        // This runs on a background thread via DecisionQueue.
        throw new NotImplementedException(
            "LLM inference not yet wired. Add LLamaSharp and implement inference here.");
    }
}
