using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Options;

/// <summary>
/// Configures page merging and the final PDF rewrite.
/// </summary>
public sealed class PdfMergeOptions
{
    /// <summary>Gets or sets whether standard metadata from the first source is copied to the produced PDF.</summary>
    [JsonPropertyName("preserveFirstDocumentMetadata")]
    [JsonProperty("preserveFirstDocumentMetadata")]
    public bool PreserveFirstDocumentMetadata { get; set; } = true;

    /// <summary>Gets or sets the final-output settings.</summary>
    [JsonPropertyName("output")]
    [JsonProperty("output")]
    public PdfOutputOptions Output { get; set; } = new();
}
