using Newtonsoft.Json;
using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Tests.HostedUnit;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace Soenneker.Pdfs.Processor.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed partial class PdfProcessorTests : HostedUnitTest
{
    private readonly IPdfProcessor _processor;

    public PdfProcessorTests(Host host) : base(host)
    {
        _processor = Resolve<IPdfProcessor>(true);
    }

    [Test]
    public async Task Merge_preserves_pages_and_selected_ranges(CancellationToken cancellationToken)
    {
        using MemoryStream first = CreatePdf("First", 2);
        using MemoryStream second = CreatePdf("Second", 1);
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.Merge(
        [
            new PdfMergeSource { Stream = first, StartPage = 2, EndPage = 2 },
            new PdfMergeSource { Stream = second }
        ], output, new PdfMergeOptions { Output = NoCompression() }, cancellationToken);

        string pdf = GetPdfText(output);
        await Assert.That(result.PageCount).IsEqualTo(2);
        await Assert.That(PageObjectRegex().Count(pdf)).IsEqualTo(2);
        await Assert.That(pdf).Contains("First 2");
        await Assert.That(pdf).Contains("Second 1");
        AssertXrefOffsets(pdf);
    }

    [Test]
    public async Task ReplaceText_rewrites_operand_and_preserves_font_operator(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf("Hello TOKEN", 1);
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "WORLD" }
        ], new PdfReplaceOptions { Output = NoCompression() }, cancellationToken);

        string pdf = GetPdfText(output);
        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(result.Replacements[0].FontResourcePreserved).IsTrue();
        await Assert.That(pdf).Contains("Hello WORLD");
        await Assert.That(pdf).DoesNotContain("TOKEN");
        await Assert.That(pdf).Contains("/F1 12 Tf");
    }

    [Test]
    public async Task ReplaceText_matches_across_TJ_fragments_and_preserves_positioning_operands(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreateRawTextPdf("[(Hello TO) 0 (K) 0 (EN tail)] TJ");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "token", Replacement = "WONDERFUL WORLD" }
        ], new PdfReplaceOptions { Comparison = PdfTextComparison.OrdinalIgnoreCase, Output = NoCompression() }, cancellationToken);

        string pdf = GetPdfText(output);
        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(pdf.Replace(" ", string.Empty)).Contains("HelloWONDERFULWORLD");
        await Assert.That(pdf).Contains(" 0 ");
    }

    [Test]
    public async Task ReplaceText_can_disable_cross_fragment_matching(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreateRawTextPdf("[(TO) 0 (KEN)] TJ");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }
        ], new PdfReplaceOptions { MatchAcrossTextFragments = false, Output = NoCompression() }, cancellationToken);

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(0);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReplaceText_matches_consecutive_text_operators_but_not_across_positioning_changes(CancellationToken cancellationToken)
    {
        using MemoryStream contiguousSource = CreateRawTextPdf("(TO) Tj (KEN) Tj");
        using MemoryStream positionedSource = CreateRawTextPdf("(TO) Tj 10 0 Td (KEN) Tj");
        using var contiguousOutput = new MemoryStream();
        using var positionedOutput = new MemoryStream();
        PdfTextReplacement[] replacements = [new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }];
        var options = new PdfReplaceOptions { Output = NoCompression() };

        PdfProcessResult contiguous = await _processor.ReplaceText(contiguousSource, contiguousOutput, replacements, options, cancellationToken: cancellationToken);
        PdfProcessResult positioned = await _processor.ReplaceText(positionedSource, positionedOutput, replacements, options, cancellationToken: cancellationToken);

        await Assert.That(contiguous.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(contiguous.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(positioned.Replacements[0].ReplacementCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReplaceText_applies_match_limits_across_fragmented_runs(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreateRawTextPdf("[(TO) 0 (KEN TOKEN TOKEN)] TJ");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "X", MaximumReplacements = 2 }
        ], new PdfReplaceOptions { Output = NoCompression() }, cancellationToken);

        string pdf = GetPdfText(output);
        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(2);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(pdf).Contains("TOKEN");
    }

    [Test]
    public async Task ReplaceText_reads_Flate_compressed_content(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf("Compressed TOKEN", 1);
        using var compressed = new MemoryStream();
        using var output = new MemoryStream();
        await _processor.Optimize(source, compressed, new PdfOutputOptions { Compression = PdfCompressionProfile.Maximum }, cancellationToken: cancellationToken);
        compressed.Position = 0;

        PdfProcessResult result = await _processor.ReplaceText(compressed, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }
        ], new PdfReplaceOptions { Output = NoCompression() }, cancellationToken);

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(GetPdfText(output)).Contains("Compressed VALUE");
    }

    [Test]
    public async Task Optimize_removes_metadata_from_the_file(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf("Metadata", 1, "Private title");
        using var output = new MemoryStream();

        await _processor.Optimize(source, output, new PdfOutputOptions { RemoveMetadata = true, Compression = PdfCompressionProfile.None }, cancellationToken: cancellationToken);

        string pdf = GetPdfText(output);
        await Assert.That(pdf).DoesNotContain("Private title");
        await Assert.That(pdf).DoesNotContain("/Info");
    }

    [Test]
    public async Task Public_models_have_dual_json_names_and_enum_values_serialize_by_value()
    {
        Type[] modelTypes =
        [
            typeof(PdfMergeSource), typeof(PdfTextReplacement), typeof(PdfTextReplacementResult), typeof(PdfProcessResult),
            typeof(PdfOutputOptions), typeof(PdfMergeOptions), typeof(PdfReplaceOptions)
        ];

        foreach (PropertyInfo property in modelTypes.SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
        {
            await Assert.That(property.GetCustomAttribute<JsonPropertyNameAttribute>()).IsNotNull();
            await Assert.That(property.GetCustomAttribute<JsonPropertyAttribute>()).IsNotNull();
        }

        var options = new PdfOutputOptions { Compression = PdfCompressionProfile.Maximum };
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(options)).Contains("\"compression\":\"maximum\"");
        await Assert.That(JsonConvert.SerializeObject(options)).Contains("\"compression\":\"maximum\"");
    }

    private static PdfOutputOptions NoCompression() => new() { Compression = PdfCompressionProfile.None };

    private static MemoryStream CreatePdf(string text, int pageCount, string? title = null)
    {
        var objects = new List<string>();
        int fontNumber = pageCount * 2 + 3;
        int infoNumber = fontNumber + 1;
        string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(index => $"{3 + index * 2} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        for (var index = 0; index < pageCount; index++)
        {
            int pageNumber = 3 + index * 2;
            int contentNumber = pageNumber + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontNumber} 0 R >> >> /Contents {contentNumber} 0 R >>");
            string content = $"BT /F1 12 Tf 72 720 Td ({EscapeLiteral(text)} {index + 1}) Tj ET";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add($"<< /Title ({EscapeLiteral(title ?? string.Empty)}) >>");
        return WritePdf(objects, infoNumber);
    }

    private static MemoryStream CreateRawTextPdf(string textOperations)
    {
        string content = $"q BT /F1 12 Tf 72 720 Td {textOperations} ET Q";
        return WritePdf(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ]);
    }

    private static MemoryStream WritePdf(IReadOnlyList<string> objects, int? infoNumber = null)
    {
        var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            WriteAscii(stream, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        long xref = stream.Position;
        WriteAscii(stream, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var index = 1; index < offsets.Count; index++)
            WriteAscii(stream, $"{offsets[index]:0000000000} 00000 n \n");
        WriteAscii(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R");
        if (infoNumber != null)
            WriteAscii(stream, $" /Info {infoNumber} 0 R");
        WriteAscii(stream, $" >>\nstartxref\n{xref}\n%%EOF\n");
        stream.Position = 0;
        return stream;
    }

    private static string GetPdfText(MemoryStream stream) => Encoding.Latin1.GetString(stream.ToArray());

    private static void AssertXrefOffsets(string pdf)
    {
        foreach (Match match in ObjectRegex().Matches(pdf))
        {
            string offset = match.Index.ToString("0000000000");
            if (!pdf.Contains(offset + " 00000 n", StringComparison.Ordinal))
                throw new InvalidDataException($"The xref table does not contain object {match.Groups[1].Value} at byte {match.Index}.");
        }
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));

    private static string EscapeLiteral(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    [GeneratedRegex(@"(?m)^\d+ 0 obj\s*\r?\n<<\s*/Type\s*/Page(?:\s|/)")]
    private static partial Regex PageObjectRegex();

    [GeneratedRegex(@"(?m)^(\d+) 0 obj$")]
    private static partial Regex ObjectRegex();
}
