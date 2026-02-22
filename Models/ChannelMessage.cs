using System;
using System.Collections.Generic;
using System.Text;

namespace nocscienceat.XPlaneWebConnector.Models;

// Baseclass with empty private ctr
internal abstract record ChannelMessage
{
    private ChannelMessage() {}

    // Holding XPlane data
    internal sealed record DataMessage(
        long TimeStamp,
        byte[] Data) : ChannelMessage;

    internal sealed record SubscribeNumericMessage(
        long Id,
        int Index,
        SimDataRef Element,
        Action<SimDataRef, float> Callback,
        TaskCompletionSource Done) : ChannelMessage;

    internal sealed record SubscribeStringMessage(
        long Id,
        int Index,
        SimStringDataRef Element,
        Action<SimStringDataRef, string> Callback,
        TaskCompletionSource Done) : ChannelMessage;

    internal sealed record SubscribeCommandMessage(
        IEnumerable<long> CommandIds,
        Action<long, bool> OnUpdate,
        TaskCompletionSource Done) : ChannelMessage;

}


