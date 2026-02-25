namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Commands sent through the Worker's command channel to modify subscription state.
/// Used as the <c>TCommand</c> type in the Worker pipeline.
/// Processed on the Worker's single thread, serializing access to subscription dictionaries.
/// </summary>
internal abstract record WorkerCommand
{
    private WorkerCommand() { }

    internal sealed record SubscribeNumeric(
        long Id, int Index, SimDataRef Element, Action<SimDataRef> Callback) : WorkerCommand;

    internal sealed record SubscribeString(
        long Id, int Index, SimStringDataRef Element, Action<SimStringDataRef> Callback) : WorkerCommand;

    internal sealed record SubscribeCommandUpdates(
        IEnumerable<long> CommandIds, Action<long, bool> OnUpdate) : WorkerCommand;

    internal sealed record UnsubscribeAllDataRefs() : WorkerCommand;

    internal sealed record UnsubscribeAllCommands() : WorkerCommand;
}
