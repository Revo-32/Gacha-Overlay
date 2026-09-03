namespace LSOverlay.RemoteClient;

public sealed class RemoteAuthenticationRequiredException : Exception
{
    public RemoteAuthenticationRequiredException()
        : base("LS Overlay authentication is required.")
    {
    }
}

public sealed class RemoteResyncRequiredException : Exception
{
    public RemoteResyncRequiredException()
        : base("A fresh LS Overlay bootstrap is required.")
    {
    }
}
