namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Raw WebSocket data message received from X-Plane.
/// Used as the <c>TData</c> type in the Worker pipeline.
/// </summary>
internal sealed record XPlaneDataMessage(long TimeStamp, byte[] MessageBytes);
