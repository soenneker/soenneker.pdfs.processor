namespace Soenneker.Pdfs.Processor.Internal;

internal sealed class PdfDecodeBudget(CancellationToken cancellationToken = default)
{
    private long _decodedBytes;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal void Charge(long streamLength, int count)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (streamLength + count > PdfProcessingLimits.MaximumStreamBytes ||
            _decodedBytes + count > PdfProcessingLimits.MaximumDecodedBytes)
            throw new PdfLimitExceededException("The PDF exceeds the decoded-stream byte budget.");
        _decodedBytes += count;
    }
}
