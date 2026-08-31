using Soenneker.Gen.EnumValues;

namespace Soenneker.Pdfs.Processor.Enums;

/// <summary>
/// Describes the outcome of one requested text replacement.
/// </summary>
[EnumValue<string>]
public sealed partial class PdfReplacementStatus
{
    /// <summary>At least one supported text operand was replaced.</summary>
    public static readonly PdfReplacementStatus Replaced = new("replaced");

    /// <summary>No matching text was found in a supported text operand.</summary>
    public static readonly PdfReplacementStatus NotFound = new("notFound");

    /// <summary>The requested replacement cannot be represented under the configured replacement constraints.</summary>
    public static readonly PdfReplacementStatus UnsupportedReplacement = new("unsupportedReplacement");

    /// <summary>The configured maximum replacement count was reached.</summary>
    public static readonly PdfReplacementStatus MatchLimitReached = new("matchLimitReached");
}
