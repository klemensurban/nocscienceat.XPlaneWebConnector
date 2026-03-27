// Copyright (c) 2026 Klemens Urban <klemens.urban@outlook.com>
// SPDX-License-Identifier: MIT

using nocscienceat.XPlaneWebConnector.Interfaces;

namespace nocscienceat.XPlaneWebConnector.VirtualDataRefs;

/// <summary>
/// Non-generic base for virtual dataref providers. Used internally by the
/// connector to store heterogeneous providers. Implement
/// <see cref="IVirtualDataRefProvider{T}"/> instead of this interface directly.
/// </summary>
public interface IVirtualDataRefProvider
{
    /// <summary>Path suffix after <c>xplanewebconnector/</c>, e.g. <c>"teleport"</c>.</summary>
    string Prefix { get; }

    /// <summary>The <see cref="Action{T}"/> element type consumers must use (e.g. <c>typeof(int)</c>).</summary>
    Type CallbackType { get; }
}

/// <summary>
/// Strongly-typed value source for a synthetic ("virtual") dataref whose path begins
/// with <c>xplanewebconnector/{Prefix}</c>. Register via
/// <see cref="IXPlaneWebConnector.RegisterVirtualDataRef{T}"/>.
/// <para>
/// Providers do NOT manage consumers — the connector handles multi-consumer fanout
/// internally. Providers receive an <see cref="Action{T}"/> <paramref name="emit"/>
/// delegate in <see cref="InitializeAsync"/> and call it to push values.
/// </para>
/// </summary>
/// <typeparam name="T">
/// The callback value type consumers subscribe with (e.g. <c>int</c> for teleport).
/// </typeparam>
public interface IVirtualDataRefProvider<T> : IVirtualDataRefProvider
{
    /// <summary>Derived automatically from <typeparamref name="T"/>.</summary>
    Type IVirtualDataRefProvider.CallbackType => typeof(T);

    /// <summary>
    /// Called once when the first consumer subscribes. The provider should subscribe
    /// to the real X-Plane datarefs it depends on here and call <paramref name="emit"/>
    /// whenever a new value should be pushed to consumers. Subscriptions to X-Plane
    /// MUST NOT be disposed — they stay alive for the lifetime of the connector.
    /// </summary>
    /// <param name="connector">The connector for subscribing to real X-Plane datarefs.</param>
    /// <param name="emit">
    /// Typed delegate the provider calls to fan out values to all subscribed consumers.
    /// Managed by the connector; routes through the internal callback channel.
    /// </param>
    Task InitializeAsync(IXPlaneWebConnector connector, Action<T> emit);

    /// <summary>
    /// Called when a consumer writes a value to this virtual dataref via
    /// <see cref="IXPlaneWebConnector.SetDataRefValueAsync"/>.
    /// Routed through the connector's callback channel so it runs on the same
    /// Callback Task as <see cref="InitializeAsync"/> callbacks — no locks needed.
    /// <para>Default: no-op (read-only provider). Override to handle writes.</para>
    /// </summary>
    /// <param name="value">The value written by the consumer.</param>
    void OnValueWritten(T value) { }
}
