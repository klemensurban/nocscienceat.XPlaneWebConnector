using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChannelWorker;

// ── Generic envelope and handler contract ──────────────────────────────

public record CommandEnvelope<TCommand, TResult>(TCommand Command, TaskCompletionSource<TResult> Completion);

/// <summary>
/// Implement this interface to define how commands and data are processed.
/// All methods are guaranteed to run on the worker's single thread.
/// </summary>
public interface IWorkerHandler<TCommand, TResult, TData>
{
    Task<TResult> HandleCommandAsync(TCommand command, CancellationToken ct);
    void HandleData(TData data);
}

// ── Single-threaded SynchronizationContext ──────────────────────────────

/// <summary>
/// A single-threaded SynchronizationContext that runs all posted callbacks
/// on the thread that calls <see cref="RunOnCurrentThread"/>.
/// </summary>
public sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    /// <summary>
    /// Queues a callback for execution on the dedicated thread.
    /// </summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _queue.Add((d, state));
    }

    /// <summary>
    /// Executes synchronously by posting and blocking. Avoids deadlocks
    /// when called from a different thread.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        using var done = new ManualResetEventSlim();
        _queue.Add((s =>
        {
            try { d(s); }
            finally { done.Set(); }
        }, state));
        done.Wait();
    }

    /// <summary>
    /// Pumps the queue on the current thread until <see cref="Complete"/> is called.
    /// </summary>
    public void RunOnCurrentThread()
    {
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    /// <summary>
    /// Signals the pump to exit after draining remaining items.
    /// </summary>
    public void Complete() => _queue.CompleteAdding();
}

// ── Generic Worker ─────────────────────────────────────────────────────

/// <summary>
/// A reusable, single-threaded worker that prioritizes commands over data,
/// interleaving data processing while commands await I/O.
/// All async continuations are serialized to a dedicated thread via a custom
/// <see cref="SynchronizationContext"/>, making shared state between
/// <see cref="IWorkerHandler{TCommand,TResult,TData}.HandleCommandAsync"/> and
/// <see cref="IWorkerHandler{TCommand,TResult,TData}.HandleData"/> thread-safe
/// without locks.
/// </summary>
/// <typeparam name="TCommand">The command type sent through the command channel.</typeparam>
/// <typeparam name="TResult">The result type returned to the caller via <see cref="TaskCompletionSource{TResult}"/>.</typeparam>
/// <typeparam name="TData">The data type sent through the data channel.</typeparam>
public sealed class Worker<TCommand, TResult, TData>
{
    private readonly ChannelReader<CommandEnvelope<TCommand, TResult>> _commands;
    private readonly ChannelReader<TData> _data;
    private readonly IWorkerHandler<TCommand, TResult, TData> _handler;
    private readonly ILogger _logger;

    public Worker(
        Channel<CommandEnvelope<TCommand, TResult>> commandChannel,
        Channel<TData> dataChannel,
        IWorkerHandler<TCommand, TResult, TData> handler,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(commandChannel);
        ArgumentNullException.ThrowIfNull(dataChannel);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        _commands = commandChannel.Reader;
        _data = dataChannel.Reader;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>
    /// Starts the worker loop on a dedicated thread with a custom
    /// SynchronizationContext, ensuring all async continuations
    /// are serialized to that single thread.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource();

        var thread = new Thread(() =>
        {
            // Install a custom SynchronizationContext so that all await
            // continuations (including those from handler async methods)
            // are posted back to this thread's pump rather than dispatched
            // to arbitrary thread-pool threads.
            var syncCtx = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            // Start the async loop. When it completes (normally or via
            // cancellation), signal the pump to stop accepting new work.
            ExecuteLoopAsync(ct).ContinueWith(_ => syncCtx.Complete());

            // Block this thread and pump queued continuations until
            // Complete() is called. This is what keeps the thread alive
            // and processes all async callbacks in order.
            syncCtx.RunOnCurrentThread();

            // The pump has drained — the worker is fully shut down.
            tcs.SetResult();
        })
        {
            IsBackground = true,
            Name = $"Worker-Loop-{typeof(TCommand).Name}"
        };

        thread.Start();
        return tcs.Task;
    }

    private async Task ExecuteLoopAsync(CancellationToken ct)
    {
        // Tracks the currently running command task (null = idle).
        Task? activeCommand = null;

        while (!ct.IsCancellationRequested)
        {
            // ── Phase 1: No command in flight ──────────────────────────
            // Priority order: commands first, then data, then async wait.
            // This ensures commands are always picked up before data when
            // both channels have items available.
            if (activeCommand is null)
            {
                // 1a) Non-blocking check for a command (highest priority).
                if (_commands.TryRead(out var envelope))
                {
                    activeCommand = ProcessCommandAsync(envelope, ct);
                    continue;
                }

                // 1b) No command available — process one data item and
                //     loop back to re-check commands before processing more.
                if (TryHandleNextData())
                    continue;

                // 1c) Both channels empty — suspend until either channel
                //     receives an item. Returns a started command task if
                //     a command arrived, or null if data was handled inline.
                activeCommand = await WaitForNextWorkAsync(ct);

                // If nothing was returned and both channels are permanently
                // closed by their writers, exit gracefully to avoid a hot loop.
                if (activeCommand is null && BothChannelsCompleted())
                    return;

                continue;
            }

            // ── Phase 2: Command in flight — interleave data ───────────
            // A command is awaiting async I/O (e.g. HTTP call). While the
            // thread would otherwise be idle, use it to process data items.
            // InterleaveDataAsync returns when the command completes or the
            // data channel closes.
            await InterleaveDataAsync(activeCommand, ct);

            // Observe the command's result. If it faulted, the exception
            // was already forwarded to the caller via TCS; log it here
            // for worker-side visibility.
            await FinalizeCommandAsync(activeCommand);
            activeCommand = null;
        }
    }

    /// <summary>
    /// Suspends until either channel has an item available.
    /// Both WaitToReadAsync calls are started concurrently, then raced
    /// with Task.WhenAny. Returns a started command task if a command
    /// arrived first, or null if data was handled (or a channel closed).
    /// </summary>
    private async Task<Task?> WaitForNextWorkAsync(CancellationToken ct)
    {
        // Start listening on both channels simultaneously.
        var cmdReady = _commands.WaitToReadAsync(ct).AsTask();
        var dataReady = _data.WaitToReadAsync(ct).AsTask();

        // Whichever channel signals first wins the race.
        var winner = await Task.WhenAny(cmdReady, dataReady);

        if (winner == cmdReady)
        {
            // WaitToReadAsync returns true if items are available,
            // false if the channel was completed by the writer.
            if (await cmdReady && _commands.TryRead(out var envelope))
                return ProcessCommandAsync(envelope, ct);
        }
        else
        {
            // Data arrived first — process one item inline.
            // The losing cmdReady task is abandoned; its signal is not
            // lost because the next loop iteration re-checks TryRead.
            if (await dataReady)
                TryHandleNextData();
        }

        // null = no command was started (data was handled, or a channel
        // signaled completion). Caller checks BothChannelsCompleted().
        return null;
    }

    /// <summary>
    /// Processes data items while a command is awaiting async I/O.
    /// Races the command's completion against data channel availability.
    /// Returns as soon as the command completes or the data channel closes.
    /// </summary>
    private async Task InterleaveDataAsync(Task commandTask, CancellationToken ct)
    {
        while (!commandTask.IsCompleted)
        {
            // Race: did the command finish, or is new data available?
            var dataReady = _data.WaitToReadAsync(ct).AsTask();
            var winner = await Task.WhenAny(commandTask, dataReady);

            // Command finished while we were waiting — stop interleaving.
            if (winner == commandTask)
                return;

            // Data channel was closed by the writer — nothing more to
            // interleave. Let the caller await the command to completion.
            if (!await dataReady)
                return;

            // Drain available data items one at a time.
            while (_data.TryRead(out var data))
            {
                SafeHandleData(data);

                // Yield to the SynchronizationContext pump after each item.
                // This allows queued command-completion continuations to
                // execute between data items, so the TCS result is reported
                // promptly rather than waiting for the entire buffer to drain.
                await Task.Yield();

                // Check if the command completed during the yield.
                if (commandTask.IsCompleted)
                    return;
            }
        }
    }

    /// <summary>
    /// Delegates command processing to the handler and forwards the outcome
    /// to the caller via the envelope's <see cref="TaskCompletionSource{TResult}"/>.
    /// Success → SetResult, cancellation → TrySetCanceled, failure → TrySetException.
    /// The caller (who called SendCommandAsync) receives the result by awaiting tcs.Task.
    /// </summary>
    private async Task ProcessCommandAsync(CommandEnvelope<TCommand, TResult> envelope, CancellationToken ct)
    {
        try
        {
            var result = await _handler.HandleCommandAsync(envelope.Command, ct);
            envelope.Completion.SetResult(result);
        }
        catch (OperationCanceledException)
        {
            envelope.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            envelope.Completion.TrySetException(ex);
        }
    }

    private async Task FinalizeCommandAsync(Task commandTask)
    {
        try
        {
            await commandTask;
        }
        catch (Exception ex)
        {
            // Exception is already forwarded to the caller via TCS;
            // log it here for worker-side observability.
            _logger.LogError(ex, "Command processing failed");
        }
    }

    /// <summary>
    /// Non-blocking attempt to read and process one data item.
    /// Returns true if an item was processed, false if the channel was empty.
    /// Used in Phase 1 to process data between command availability checks.
    /// </summary>
    private bool TryHandleNextData()
    {
        if (!_data.TryRead(out var data))
            return false;

        SafeHandleData(data);
        return true;
    }

    /// <summary>
    /// Wraps the handler's data processing in a try/catch so that a
    /// faulting data item never crashes the worker loop.
    /// </summary>
    private void SafeHandleData(TData data)
    {
        try
        {
            _handler.HandleData(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data processing failed");
        }
    }

    /// <summary>
    /// Returns true when both channel writers have called Complete(),
    /// meaning no new items will ever arrive. Used to detect a clean
    /// shutdown condition and exit the loop without hot-spinning.
    /// </summary>
    private bool BothChannelsCompleted() =>
        _commands.Completion.IsCompleted && _data.Completion.IsCompleted;
}

/// <summary>
/// Helper for sending commands through a <see cref="CommandEnvelope{TCommand, TResult}"/> channel.
/// </summary>
public static class WorkerExtensions
{
    /// <summary>
    /// Wraps a command in an envelope and awaits the result from the worker.
    /// </summary>
    public static async Task<TResult> SendCommandAsync<TCommand, TResult>(
        this ChannelWriter<CommandEnvelope<TCommand, TResult>> writer,
        TCommand command,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await writer.WriteAsync(new CommandEnvelope<TCommand, TResult>(command, tcs), ct);
        return await tcs.Task;
    }
}
