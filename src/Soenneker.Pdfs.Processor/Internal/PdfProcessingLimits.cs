namespace Soenneker.Pdfs.Processor.Internal;

internal static class PdfProcessingLimits
{
    internal const int MaximumDepth = 128;
    internal const int MaximumInputBytes = 64 * 1024 * 1024;
    internal const int MaximumStreamBytes = 16 * 1024 * 1024;
    internal const int MaximumDecodedBytes = 128 * 1024 * 1024;
    internal const int MaximumObjects = 100_000;
    internal const int MaximumFilters = 16;

    internal static void CheckDepth(int depth)
    {
        if (depth > MaximumDepth)
            throw new PdfLimitExceededException("The PDF exceeds the maximum nesting depth.");
    }
}
