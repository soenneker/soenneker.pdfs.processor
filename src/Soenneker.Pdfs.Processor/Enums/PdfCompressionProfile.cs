using Soenneker.Gen.EnumValues;

namespace Soenneker.Pdfs.Processor.Enums;

/// <summary>
/// Specifies the speed and lossless compression tradeoff used when writing a PDF.
/// </summary>
[EnumValue<string>]
public sealed partial class PdfCompressionProfile
{
    /// <summary>Disables compression when rewriting the PDF.</summary>
    public static readonly PdfCompressionProfile None = new("none");

    /// <summary>Uses faster compression that may produce a larger PDF.</summary>
    public static readonly PdfCompressionProfile Fast = new("fast");

    /// <summary>Uses the default balance between compression speed and output size.</summary>
    public static readonly PdfCompressionProfile Balanced = new("balanced");

    /// <summary>Uses the strongest available lossless compression at the cost of additional processing time.</summary>
    public static readonly PdfCompressionProfile Maximum = new("maximum");
}
