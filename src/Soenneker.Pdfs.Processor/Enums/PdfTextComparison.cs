using Soenneker.Gen.EnumValues;

namespace Soenneker.Pdfs.Processor.Enums;

/// <summary>
/// Specifies how text operands are compared when locating replacement matches.
/// </summary>
[EnumValue<string>]
public sealed partial class PdfTextComparison
{
    /// <summary>Uses case-sensitive ordinal comparison.</summary>
    public static readonly PdfTextComparison Ordinal = new("ordinal");

    /// <summary>Uses case-insensitive ordinal comparison.</summary>
    public static readonly PdfTextComparison OrdinalIgnoreCase = new("ordinalIgnoreCase");
}
