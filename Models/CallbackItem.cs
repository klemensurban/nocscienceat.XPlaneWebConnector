namespace nocscienceat.XPlaneWebConnector.Models;

internal abstract record CallbackItem
{
    private CallbackItem(){}

    internal sealed record SimDataRefCb (
        Action<float> Callback,
        float Value
        ) : CallbackItem;

    internal sealed record SimStringDataRefCb (
        Action<string> Callback, 
        string Value
        ) : CallbackItem; 

    internal sealed record CommandCb (
        Action<bool> Callback,
        bool IsActive
        ) : CallbackItem;
}