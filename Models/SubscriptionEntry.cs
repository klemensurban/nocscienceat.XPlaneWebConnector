namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Discriminates which subscription dictionary holds the callback.
/// </summary>
internal enum SubscriptionKind { Data, Command }

/// <summary>
/// Tracks one consumer's subscription to a specific dataRef or command.
/// Stored in the subscription registry keyed by a unique <see cref="Guid"/>.
/// Contains all information needed to surgically remove this single consumer
/// and, when the last consumer for a given (Id, Index) is gone,
/// unsubscribe from X-Plane.
/// </summary>
internal sealed record SubscriptionEntry(
    long Id,
    int Index,
    SubscriptionKind Kind,
    Delegate Callback);
