namespace NegotiationsTool.Console.Services;

public sealed class ApplicationInitializationState
{
    private readonly object _syncRoot = new();

    public bool IsInitializing { get; private set; }

    public bool IsReady { get; private set; }

    public string? ErrorMessage { get; private set; }

    public void MarkInitializing()
    {
        lock (_syncRoot)
        {
            IsInitializing = true;
            IsReady = false;
            ErrorMessage = null;
        }
    }

    public void MarkReady()
    {
        lock (_syncRoot)
        {
            IsInitializing = false;
            IsReady = true;
            ErrorMessage = null;
        }
    }

    public void MarkFailed(string errorMessage)
    {
        lock (_syncRoot)
        {
            IsInitializing = false;
            IsReady = false;
            ErrorMessage = errorMessage;
        }
    }
}