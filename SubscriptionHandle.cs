using ChannelWorker;
using nocscienceat.XPlaneWebConnector.Models;
using System.Threading.Channels;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Represents a single consumer's subscription to a dataRef or command.
/// Disposing this handle removes the consumer's callback from the subscription;
/// when the last consumer for a given dataRef/command is removed, X-Plane is
/// notified to stop sending updates.
/// </summary>
public sealed class SubscriptionHandle : IDisposable
{
    private readonly Guid _subscriptionId;
    private readonly ChannelWriter<CommandEnvelope<WorkerCommand, bool>> _commandWriter;
    private int _disposed;

    internal SubscriptionHandle(Guid subscriptionId, ChannelWriter<CommandEnvelope<WorkerCommand, bool>> commandWriter)
    {
        _subscriptionId = subscriptionId;
        _commandWriter = commandWriter;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Fire-and-forget: the consumer does not need to await unsubscription.
        // The Worker will process the command on its dedicated thread.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandWriter.TryWrite(new CommandEnvelope<WorkerCommand, bool>(
            new WorkerCommand.UnsubscribeByGuid(_subscriptionId), tcs));
    }
}
