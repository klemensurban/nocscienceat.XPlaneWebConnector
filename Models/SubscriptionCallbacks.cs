namespace nocscienceat.XPlaneWebConnector.Models;

/// <summary>
/// Non-generic base for typed callback storage in the unified subscription dictionary.
/// For any given (Id, Index) key all callbacks must share the same <c>T</c>.
/// </summary>
internal abstract class SubscriptionCallbacks
{
    public abstract Type CallbackType { get; }
    public abstract int Count { get; }
    public abstract bool RemoveCallback(Delegate callback);

    /// <summary>
    /// Downcasts to <see cref="SubscriptionCallbacks{T}"/>.
    /// Throws if the stored callback type does not match <typeparamref name="T"/>.
    /// </summary>
    public SubscriptionCallbacks<T> As<T>() => this as SubscriptionCallbacks<T>
                                               ?? throw new InvalidOperationException($"Subscription holds Action<{CallbackType.Name}> callbacks but Action<{typeof(T).Name}> was requested.");
}

/// <summary>
/// Strongly-typed list of <see cref="Action{T}"/> callbacks for a single subscription slot.
/// </summary>
internal sealed class SubscriptionCallbacks<T> : SubscriptionCallbacks
{
    private readonly List<Action<T>> _list = [];

    public override Type CallbackType => typeof(T);
    public override int Count => _list.Count;
    public IReadOnlyList<Action<T>> Callbacks => _list;

    public void Add(Action<T> callback) => _list.Add(callback);

    public override bool RemoveCallback(Delegate callback) => callback is Action<T> typed && _list.Remove(typed);
}
