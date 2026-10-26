namespace UniversalMediaDistribution.Domain.Reading;

public sealed class ImageSequenceValidationException : Exception
{
    public ImageSequenceValidationException(string message)
        : base(message)
    {
    }
}

