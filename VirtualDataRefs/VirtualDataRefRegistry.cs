// Copyright (c) 2026 Klemens Urban <klemens.urban@outlook.com>
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using nocscienceat.XPlaneWebConnector.Interfaces;

namespace nocscienceat.XPlaneWebConnector.VirtualDataRefs;

/// <summary>
/// Registry of virtual datarefs. Owns the multi-consumer subscription state for each
/// provider so that <see cref="IVirtualDataRefProvider"/> implementations stay
/// single-responsibility (value source only).
/// Paths matching <c>xplanewebconnector/{prefix}</c> are routed here instead of X-Plane.
/// </summary>
internal sealed class VirtualDataRefRegistry
{
    private const string PathPrefix = "xplanewebconnector/";
    private readonly Dictionary<string, VirtualEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a provider with an explicit value type <typeparamref name="T"/>.
    /// The provider must implement <see cref="IVirtualDataRefProvider{T}"/>.
    /// </summary>
    public void Register<T>(IVirtualDataRefProvider<T> provider)
    {
        _entries[provider.Prefix] = new VirtualEntry<T>(provider);
    }

    /// <summary>
    /// Returns the entry for the given path if it's a virtual dataref.
    /// </summary>
    public bool TryGetEntry(string dataRefPath, out VirtualEntry? entry)
    {
        entry = null;
        if (!dataRefPath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string suffix = dataRefPath[PathPrefix.Length..];
        return _entries.TryGetValue(suffix, out entry);
    }

    public bool IsVirtualPath(string dataRefPath)
        => dataRefPath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Base class for a virtual dataref subscription entry. Handles consumer registration,
/// lazy initialization, and type validation. The typed subclass <see cref="VirtualEntry{T}"/>
/// provides the concrete consumer list and emit delegate.
/// </summary>
internal abstract class VirtualEntry
{
    public IVirtualDataRefProvider Provider { get; }
    public Type CallbackType => Provider.CallbackType;

    protected VirtualEntry(IVirtualDataRefProvider provider) => Provider = provider;

    /// <summary>Adds a typed consumer callback. Throws if the type doesn't match.</summary>
    public abstract IDisposable AddConsumer(Delegate callback);

    /// <summary>Initializes the provider on first consumer. Idempotent.</summary>
    /// <param name="connector">Connector for subscribing to real X-Plane datarefs.</param>
    /// <param name="callbackWriter">
    /// The connector's callback channel writer. Virtual callbacks are queued here
    /// so they are dispatched on the same Callback Task as native datarefs.
    /// </param>
    public abstract Task EnsureInitializedAsync(IXPlaneWebConnector connector, ChannelWriter<Action> callbackWriter);

    /// <summary>
    /// Routes a written value through the callback channel to
    /// <see cref="IVirtualDataRefProvider{T}.OnValueWritten"/>.
    /// </summary>
    /// <param name="value">The value to write (boxed). Must match <see cref="CallbackType"/>.</param>
    /// <param name="callbackWriter">The connector's callback channel writer.</param>
    public abstract void WriteValue(object value, ChannelWriter<Action> callbackWriter);
}

/// <summary>
/// Typed virtual entry that manages <see cref="Action{T}"/> consumers with
/// copy-on-write for lock-free reads from the callback drain task.
/// </summary>
internal sealed class VirtualEntry<T> : VirtualEntry
{
    private readonly object _lock = new();
    private List<Action<T>> _consumers = [];
    private bool _initialized;

    public VirtualEntry(IVirtualDataRefProvider provider) : base(provider) { }

    public override IDisposable AddConsumer(Delegate callback)
    {
        Action<T> action = (Action<T>)callback;
        lock (_lock)
        {
            _consumers = [.. _consumers, action];
        }
        return new VirtualSubscriptionHandle(() =>
        {
            lock (_lock)
            {
                var list = new List<Action<T>>(_consumers);
                list.Remove(action);
                _consumers = list;
            }
        });
    }

    public override async Task EnsureInitializedAsync(IXPlaneWebConnector connector, ChannelWriter<Action> callbackWriter)
    {
        bool needsInit;
        lock (_lock)
        {
            needsInit = !_initialized;
            _initialized = true;
        }
        if (!needsInit) return;

        // Create the emit delegate that fans out to all consumers via the
        // connector's _callbacks channel — identical to native dataref dispatch.
        Action<T> emit = value =>
        {
            var snapshot = _consumers;
            foreach (var cb in snapshot)
                callbackWriter.TryWrite(() => cb(value));
        };

        await ((IVirtualDataRefProvider<T>)Provider).InitializeAsync(connector, emit);
    }

    public override void WriteValue(object value, ChannelWriter<Action> callbackWriter)
    {
        var typedValue = (T)value;
        callbackWriter.TryWrite(() => ((IVirtualDataRefProvider<T>)Provider).OnValueWritten(typedValue));
    }
}

/// <summary>
/// Lightweight <see cref="IDisposable"/> that invokes a removal action exactly once.
/// </summary>
internal sealed class VirtualSubscriptionHandle(Action onDispose) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        onDispose();
    }
}
