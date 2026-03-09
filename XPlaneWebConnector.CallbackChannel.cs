using Microsoft.Extensions.Logging;

namespace nocscienceat.XPlaneWebConnector;

public sealed partial class XPlaneWebConnector
{
    // ========================================================================
    // Callback channel
    // ========================================================================

    private async Task StartCallbacksAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(() => ProcessCallbackChannelAsync(ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while setting up callback Task");
        }
    }

    private async Task ProcessCallbackChannelAsync(CancellationToken ct)
    {
        try
        {
            await foreach (Action callback in _callbacks.Reader.ReadAllAsync(ct))
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing Callback Channel ");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
