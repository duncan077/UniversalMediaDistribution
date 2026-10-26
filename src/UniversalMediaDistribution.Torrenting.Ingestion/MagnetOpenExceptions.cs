namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class MagnetLinkFormatException : ArgumentException
{
    public MagnetLinkFormatException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class MagnetCompatibilityException : InvalidOperationException
{
    public MagnetCompatibilityException(string message)
        : base(message)
    {
    }
}
