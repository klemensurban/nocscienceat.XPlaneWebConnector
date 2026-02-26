namespace nocscienceat.XPlaneWebConnector.Models;

internal abstract record CallbackItem
{
    private CallbackItem(){}

    internal sealed record SimDataRefCb (
        Action<SimDataRef> Callback,
        SimDataRef Element
        ) : CallbackItem;

    internal sealed record SimStringDataRefCb (
        Action<SimStringDataRef> Callback, 
        SimStringDataRef Element
        ) : CallbackItem; 

    internal sealed record CommandCb (
        Action<SimCommand, bool> Callback,
        SimCommand Element,
        bool IsActive
        ) : CallbackItem;
}