using Soenneker.Pdfs.Processor.Models;

namespace Soenneker.Pdfs.Processor.Internal;

/// <summary>
/// Tracks mutable processing state for one PDF text replacement request.
/// </summary>
internal sealed class ReplacementState
{
    internal ReplacementState(PdfTextReplacement replacement, bool unsupported)
    {
        Replacement = replacement;
        Unsupported = unsupported;
    }

    internal PdfTextReplacement Replacement { get; }

    internal bool Unsupported { get; }

    internal int Count { get; set; }

    internal int CrossFragmentCount { get; set; }

    internal HashSet<int> Pages { get; } = [];

    internal bool IsEligible(int pageNumber) => !Unsupported &&
                                                pageNumber >= (Replacement.StartPage ?? 1) &&
                                                pageNumber <= (Replacement.EndPage ?? int.MaxValue);
}
