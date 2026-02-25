using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Models;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace nocscienceat.XPlaneWebConnector;

public sealed partial class XPlaneWebConnector
{
    // ========================================================================
    // WebSocket connection and receive loop
    // ========================================================================


    /// <summary>
    /// Establishes a WebSocket connection to the X-Plane server and enters a receive loop.
    /// Automatically reconnects on transient failures with a 3-second delay.
    /// Stops reconnecting if the server closes the connection cleanly, allowing graceful shutdown.
    /// </summary>
    /// <param name="ct">Cancellation token to signal shutdown.</param>
    private async Task ConnectWebSocketAndReceiveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri(_wsUrl), ct);
                _logger.LogInformation("WebSocket connected to {Url}", _wsUrl);

                await ReceiveLoopAsync(ct);

                // If the server closed cleanly the ConnectionClosed event was
                // already raised — stop the reconnect loop so the host can
                // shut down gracefully instead of retrying with stale state.
                if (_webSocket.State == WebSocketState.CloseReceived)
                {
                    _logger.LogInformation("Server closed connection, stopping reconnect loop");
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WebSocket connection lost ({Error}), retrying once in 3s...", ex.Message);
                _logger.LogDebug(ex, "WebSocket connection lost details");
                try
                {
                    await Task.Delay(3000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    _webSocket?.Dispose();
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(_wsUrl), ct);
                    _logger.LogInformation("WebSocket reconnected to {Url}", _wsUrl);
                    continue; // success — re-enter the loop to start ReceiveLoopAsync
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning("WebSocket reconnect failed ({Error}), signalling connection closed",
                        retryEx.Message);
                    _logger.LogDebug(retryEx, "WebSocket reconnect failure details");
                    ConnectionClosed?.Invoke();
                    break;
                }
            }
        }
    }


    /// <summary>
    /// Reads WebSocket frames as fast as possible and enqueues them for
    /// processing.  Never calls user callbacks — that happens on the
    /// processing task — so serial-port writes cannot stall the read.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        int counter = 0;
        long timeStamp;
        while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _webSocket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                ConnectionClosed?.Invoke();
                return;
            }

            byte[] messageBytes;

            if (result.EndOfMessage)
            {
                // Fast path: single-frame message (most common).
                // One exact-size allocation, no MemoryStream, no ToArray() copy.
                messageBytes = GC.AllocateUninitializedArray<byte>(result.Count);
                buffer.AsSpan(0, result.Count).CopyTo(messageBytes);
            }
            else
            {
                // Slow path: multi-frame message — assemble with ArrayPool
                _logger.LogInformation("Multiframe Message from XPlane - take slow path");
                messageBytes = await AssembleMultiFrameMessageAsync(buffer, result.Count, ct);
            }

            bool statistic = false;
            // if (counter++ == 1024) timeStamp = Stopwatch.GetTimestamp(),  0 otherwise
            if ((counter++ & 128) == 0)
                timeStamp = 0;
            else
            {
                timeStamp = Stopwatch.GetTimestamp();
                counter = 0;
                statistic = true;
            }

            XPlaneDataMessage dataMessage = new(timeStamp, messageBytes);
            _dataChannel.Writer.TryWrite(dataMessage);
            if (statistic)
                _logger.LogInformation("Number of Messages in Data-Queue: {n}", _dataChannel.Reader.Count);
        }
    }

    /// <summary>
    /// Assembles a multi-frame WebSocket message using pooled buffers
    /// for intermediate storage. Returns an exact-size byte[].
    /// </summary>
    private async Task<byte[]> AssembleMultiFrameMessageAsync(byte[] buffer, int firstFrameCount, CancellationToken ct)
    {
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var assembled = pool.Rent(buffer.Length * 2);
        int totalLength = 0;

        try
        {
            // Copy first frame data
            buffer.AsSpan(0, firstFrameCount).CopyTo(assembled);
            totalLength = firstFrameCount;

            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket!.ReceiveAsync(buffer, ct);

                // Grow pooled buffer if needed
                if (totalLength + result.Count > assembled.Length)
                {
                    var larger = pool.Rent((totalLength + result.Count) * 2);
                    assembled.AsSpan(0, totalLength).CopyTo(larger);
                    pool.Return(assembled);
                    assembled = larger;
                }

                buffer.AsSpan(0, result.Count).CopyTo(assembled.AsSpan(totalLength));
                totalLength += result.Count;
            }
            while (!result.EndOfMessage);

            // Exact-size copy for the channel (pooled buffer is returned below)
            var final = GC.AllocateUninitializedArray<byte>(totalLength);
            assembled.AsSpan(0, totalLength).CopyTo(final);
            return final;
        }
        finally
        {
            pool.Return(assembled);
        }
    }

    // ========================================================================
    // WebSocket send helper
    // ========================================================================

    /// <summary>
    /// Sends a fire-and-forget WebSocket message.
    /// X-Plane does not reliably deliver "result" responses while the receive loop
    /// is busy dispatching subscription callbacks (serial port writes, etc.),
    /// so we never block waiting for acknowledgements.
    /// </summary>
    private async Task SendWebSocketFireAndForgetAsync<T>(T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, typeInfo);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
    }
}
