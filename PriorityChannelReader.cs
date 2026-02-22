using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace nocscienceat.XPlaneWebConnector;

/// <summary>
/// Merges a high-priority and a normal-priority channel into a single
/// async stream, with strict high-priority dominance:
/// - Always drain ALL high items first.
/// - Then process at most ONE normal item.
/// - Then re-check high.
/// </summary>
internal sealed class PriorityChannelReader<T>
{
    private readonly ChannelReader<T> _high;
    private readonly ChannelReader<T> _normal;

    public PriorityChannelReader(ChannelReader<T> high, ChannelReader<T> normal)
    {
        _high = high ?? throw new ArgumentNullException(nameof(high));
        _normal = normal ?? throw new ArgumentNullException(nameof(normal));
    }

    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Local functions to avoid re-allocating lambdas/captures in the loop
        static bool TryDrain(ChannelReader<T> reader, out T item)
            => reader.TryRead(out item);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Drain all high-priority items first
            while (TryDrain(_high, out var highItem))
            {
                yield return highItem;
                ct.ThrowIfCancellationRequested();
            }

            // 2. Process ONE normal item (if available)
            if (TryDrain(_normal, out var normalItem))
            {
                yield return normalItem;
                // Immediately loop again to re-check high
                continue;
            }

            // 3. Both are empty at this instant: check completion
            if (_high.Completion.IsCompleted && _normal.Completion.IsCompleted)
            {
                // Final defensive drain in case items were written
                // just before TryComplete()
                while (TryDrain(_high, out var h)) yield return h;
                while (TryDrain(_normal, out var n)) yield return n;
                yield break;
            }

            // 4. Wait until *either* channel is likely readable again or completed.
            //    Use a minimal-allocation strategy: wait on both ValueTasks,
            //    but avoid wrapping them in Task where possible.

            var highWait = _high.WaitToReadAsync(ct);
            var normalWait = _normal.WaitToReadAsync(ct);

            // Fast path: if either is already completed synchronously,
            // just continue the loop to re-check TryRead.
            if (highWait.IsCompletedSuccessfully || normalWait.IsCompletedSuccessfully)
                continue;

            // At this point, both waits are genuinely pending.
            // Convert to Tasks once, then await Task.WhenAny.
            // This is the only place we pay the "ValueTask -> Task" cost,
            // and only when we actually have to wait.
            var highTask = highWait.AsTask();
            var normalTask = normalWait.AsTask();

            // Wait until at least one of them signals.
            await Task.WhenAny(highTask, normalTask).ConfigureAwait(false);

            // Loop back: we don't care *which* became ready first,
            // because we always check high first in the next iteration.
        }
    }
}