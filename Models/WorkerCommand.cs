namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Commands sent through the Worker's command channel to modify subscription state.
/// Used as the <c>TCommand</c> type in the Worker pipeline.
/// Processed on the Worker's single thread, serializing access to subscription dictionaries.
/// </summary>
internal abstract record WorkerCommand
{
    private WorkerCommand() { }

    internal sealed record SubscribeNumeric(Guid SubscriptionId, string dataRefPath, long Id, int Index, Action<float> Callback) : WorkerCommand;

    internal sealed record SubscribeString(Guid SubscriptionId, string dataRefPath, long Id, int Index, Action<string> Callback) : WorkerCommand;

    internal sealed record SubscribeCommand(
        Guid SubscriptionId, long Id, SimCommand Element, Action<bool> Callback) : WorkerCommand;

    internal sealed record UnsubscribeByGuid(Guid SubscriptionId) : WorkerCommand;

    internal sealed record UnsubscribeAllDataRefs() : WorkerCommand;

    internal sealed record UnsubscribeAllCommands() : WorkerCommand;
}
