using Newtonsoft.Json;
using Soenneker.Pdfs.Processor.Enums;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Options;

/// <summary>
/// Configures text matching and the final PDF rewrite.
/// </summary>
public sealed class PdfReplaceOptions
{
    /// <summary>Gets or sets the text comparison mode. The default is <see cref="PdfTextComparison.Ordinal"/>.</summary>
    [JsonPropertyName("comparison")]
    [JsonProperty("comparison")]
    public PdfTextComparison Comparison { get; set; } = PdfTextComparison.Ordinal;

    /// <summary>Gets or sets whether processing fails when any requested search text is not replaced.</summary>
    [JsonPropertyName("requireAll")]
    [JsonProperty("requireAll")]
    public bool RequireAll { get; set; }

    /// <summary>Gets or sets whether replacement strings must contain only characters representable in a single-byte PDF string.</summary>
    [JsonPropertyName("requireSingleByteReplacement")]
    [JsonProperty("requireSingleByteReplacement")]
    public bool RequireSingleByteReplacement { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a match may span multiple string fragments within the same PDF text-showing operation. The default is <see langword="true"/>.
    /// </summary>
    [JsonPropertyName("matchAcrossTextFragments")]
    [JsonProperty("matchAcrossTextFragments")]
    public bool MatchAcrossTextFragments { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a match may span consecutive <c>Tj</c> or <c>TJ</c> operations when no intervening PDF operator changes text positioning or graphics state. The default is <see langword="true"/>.
    /// </summary>
    [JsonPropertyName("matchAcrossTextOperators")]
    [JsonProperty("matchAcrossTextOperators")]
    public bool MatchAcrossTextOperators { get; set; } = true;

    /// <summary>Gets or sets the final-output settings.</summary>
    [JsonPropertyName("output")]
    [JsonProperty("output")]
    public PdfOutputOptions Output { get; set; } = new();
}
