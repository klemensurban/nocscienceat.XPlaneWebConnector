using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;

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
            await foreach (CallbackItem callbackItem in _callbacks.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // based on callbackItem record type (simulated discriminated union) do appropriate call back
                    switch (callbackItem)
                    {
                        case CallbackItem.SimDataRefCb cb:
                            cb.Callback(cb.Element);
                            break;
                        case CallbackItem.SimStringDataRefCb cb:
                            cb.Callback(cb.Element);
                            break;
                        case CallbackItem.CommandCb cb:
                            cb.Callback(cb.Id, cb.IsActive);
                            break;
                    }
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
