namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Discriminates which subscription dictionary holds the callback.
/// </summary>
internal enum SubscriptionKind { Numeric, String }

/// <summary>
/// Tracks one consumer's subscription to a specific dataRef.
/// Stored in the subscription registry keyed by a unique <see cref="Guid"/>.
/// Contains all information needed to surgically remove this single consumer
/// and, when the last consumer for a given (DataRefId, Index) is gone,
/// unsubscribe from X-Plane.
/// </summary>
internal sealed record SubscriptionEntry(
    long DataRefId,
    int Index,
    SubscriptionKind Kind,
    Delegate Callback);
