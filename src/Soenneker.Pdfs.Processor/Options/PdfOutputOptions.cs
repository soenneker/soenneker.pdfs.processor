using Soenneker.Pdfs.Processor.Enums;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Options;

/// <summary>
/// Configures the final PDF rewrite.
/// </summary>
public sealed class PdfOutputOptions
{
    /// <summary>Gets or sets the lossless compression profile. The default is <see cref="PdfCompressionProfile.Balanced"/>.</summary>
    [JsonPropertyName("compression")]
    public PdfCompressionProfile Compression { get; set; } = PdfCompressionProfile.Balanced;

    /// <summary>Gets or sets whether document information metadata is removed.</summary>
    [JsonPropertyName("removeMetadata")]
    public bool RemoveMetadata { get; set; }

    /// <summary>Gets or sets whether page annotations are removed.</summary>
    [JsonPropertyName("removeAnnotations")]
    public bool RemoveAnnotations { get; set; }

    /// <summary>Gets or sets whether embedded-file name trees are removed.</summary>
    [JsonPropertyName("removeEmbeddedFiles")]
    public bool RemoveEmbeddedFiles { get; set; }

    /// <summary>Gets or sets whether document JavaScript, automatic actions, and page automatic actions are removed.</summary>
    [JsonPropertyName("removeJavaScriptAndActions")]
    public bool RemoveJavaScriptAndActions { get; set; }
}
