using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using Newtonsoft.Json;
using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Tests.HostedUnit;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace Soenneker.Pdfs.Processor.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class PdfProcessorTests : HostedUnitTest
{
    private readonly IPdfProcessor _processor;

    public PdfProcessorTests(Host host) : base(host)
    {
        _processor = Resolve<IPdfProcessor>(true);
    }

    [Test]
    public async Task Merge_preserves_pages_and_selected_ranges()
    {
        using MemoryStream first = CreatePdf("First", 2);
        using MemoryStream second = CreatePdf("Second", 1);
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.Merge(
        [
            new PdfMergeSource { Stream = first, StartPage = 2, EndPage = 2 },
            new PdfMergeSource { Stream = second }
        ], output);

        output.Position = 0;
        using PdfDocument merged = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        await Assert.That(result.PageCount).IsEqualTo(2);
        await Assert.That(merged.PageCount).IsEqualTo(2);
    }

    [Test]
    public async Task ReplaceText_rewrites_operand_and_preserves_font_operator()
    {
        using MemoryStream source = CreatePdf("Hello TOKEN", 1);
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "WORLD" }
        ]);

        output.Position = 0;
        using PdfDocument replaced = PdfReader.Open(output, PdfDocumentOpenMode.Modify);
        CSequence content = ContentReader.ReadContent(replaced.Pages[0]);
        string text = GetText(content);

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(result.Replacements[0].FontResourcePreserved).IsTrue();
        await Assert.That(text).Contains("Hello WORLD");
        await Assert.That(text).DoesNotContain("TOKEN");
    }

    [Test]
    public async Task ReplaceText_matches_across_TJ_fragments_and_preserves_positioning_operands()
    {
        using MemoryStream source = CreateFragmentedPdf("Hello TO", "K", "EN tail");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "token", Replacement = "WONDERFUL WORLD" }
        ], new PdfReplaceOptions { Comparison = PdfTextComparison.OrdinalIgnoreCase });

        output.Position = 0;
        using PdfDocument replaced = PdfReader.Open(output, PdfDocumentOpenMode.Modify);
        CSequence content = ContentReader.ReadContent(replaced.Pages[0]);
        COperator textOperator = FindOperator(content, "TJ");

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(GetText(content).Replace(" ", string.Empty)).Contains("HelloWONDERFULWORLDtail");
        await Assert.That(ContainsInteger(textOperator.Operands)).IsTrue();
    }

    [Test]
    public async Task ReplaceText_can_disable_cross_fragment_matching()
    {
        using MemoryStream source = CreateFragmentedPdf("TO", "KEN");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }
        ], new PdfReplaceOptions { MatchAcrossTextFragments = false });

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(0);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReplaceText_matches_consecutive_text_operators_but_not_across_positioning_changes()
    {
        using MemoryStream contiguousSource = CreateRawTextPdf("(TO) Tj (KEN) Tj");
        using MemoryStream positionedSource = CreateRawTextPdf("(TO) Tj 10 0 Td (KEN) Tj");
        using var contiguousOutput = new MemoryStream();
        using var positionedOutput = new MemoryStream();
        PdfTextReplacement[] replacements = [new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }];

        PdfProcessResult contiguous = await _processor.ReplaceText(contiguousSource, contiguousOutput, replacements);
        PdfProcessResult positioned = await _processor.ReplaceText(positionedSource, positionedOutput, replacements);

        await Assert.That(contiguous.Replacements[0].ReplacementCount).IsEqualTo(1);
        await Assert.That(contiguous.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(positioned.Replacements[0].ReplacementCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReplaceText_applies_match_limits_across_fragmented_runs()
    {
        using MemoryStream source = CreateFragmentedPdf("TO", "KEN TOKEN TOKEN");
        using var output = new MemoryStream();

        PdfProcessResult result = await _processor.ReplaceText(source, output,
        [
            new PdfTextReplacement { Search = "TOKEN", Replacement = "X", MaximumReplacements = 2 }
        ]);

        output.Position = 0;
        using PdfDocument replaced = PdfReader.Open(output, PdfDocumentOpenMode.Modify);
        string text = GetText(ContentReader.ReadContent(replaced.Pages[0]));

        await Assert.That(result.Replacements[0].ReplacementCount).IsEqualTo(2);
        await Assert.That(result.Replacements[0].CrossFragmentReplacementCount).IsEqualTo(1);
        await Assert.That(text).Contains("TOKEN");
    }

    [Test]
    public async Task Optimize_removes_metadata()
    {
        using MemoryStream source = CreatePdf("Metadata", 1, "Private title");
        using var output = new MemoryStream();

        await _processor.Optimize(source, output, new PdfOutputOptions { RemoveMetadata = true });

        output.Position = 0;
        using PdfDocument optimized = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        await Assert.That(optimized.Info.Title).IsEmpty();
    }

    [Test]
    public async Task Public_models_have_dual_json_names_and_enum_values_serialize_by_value()
    {
        Type[] modelTypes =
        [
            typeof(PdfMergeSource),
            typeof(PdfTextReplacement),
            typeof(PdfTextReplacementResult),
            typeof(PdfProcessResult),
            typeof(PdfOutputOptions),
            typeof(PdfMergeOptions),
            typeof(PdfReplaceOptions)
        ];

        foreach (PropertyInfo property in modelTypes.SelectMany(static type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
        {
            await Assert.That(property.GetCustomAttribute<JsonPropertyNameAttribute>()).IsNotNull();
            await Assert.That(property.GetCustomAttribute<JsonPropertyAttribute>()).IsNotNull();
        }

        var options = new PdfOutputOptions { Compression = PdfCompressionProfile.Maximum };
        string systemTextJson = System.Text.Json.JsonSerializer.Serialize(options);
        string newtonsoftJson = JsonConvert.SerializeObject(options);

        await Assert.That(systemTextJson).Contains("\"compression\":\"maximum\"");
        await Assert.That(newtonsoftJson).Contains("\"compression\":\"maximum\"");
    }

    private static MemoryStream CreatePdf(string text, int pageCount, string? title = null)
    {
        var stream = new MemoryStream();
        using (var document = new PdfDocument())
        {
            document.Info.Title = title ?? string.Empty;
            var font = new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));

            for (var index = 0; index < pageCount; index++)
            {
                PdfPage page = document.AddPage();
                using XGraphics graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString($"{text} {index + 1}", font, XBrushes.Black, 72, 72);
            }

            document.Save(stream, false);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateFragmentedPdf(params string[] fragments)
    {
        string textArray = string.Join(" 0 ", fragments.Select(static fragment => $"({fragment})"));
        return CreateRawTextPdf($"[{textArray}] TJ");
    }

    private static MemoryStream CreateRawTextPdf(string textOperations)
    {
        var stream = new MemoryStream();
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            using (XGraphics graphics = XGraphics.FromPdfPage(page))
            {
                var font = new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
                graphics.DrawString("seed", font, XBrushes.Black, 72, 72);
            }

            PdfDictionary fonts = page.Resources.Elements.GetDictionary("/Font")!;
            string fontResource = fonts.Elements.Keys.First();
            string rawContent = $"q BT {fontResource} 12 Tf 72 720 Td {textOperations} ET Q";
            var content = new PdfContent(document);
            content.CreateStream(Encoding.ASCII.GetBytes(rawContent));
            document.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference;
            document.Save(stream, false);
        }

        stream.Position = 0;
        return stream;
    }

    private static COperator FindOperator(CSequence sequence, string name)
    {
        foreach (CObject item in sequence)
        {
            if (item is COperator operation && operation.Name == name)
                return operation;
            if (item is COperator nestedOperation)
            {
                try
                {
                    return FindOperator(nestedOperation.Operands, name);
                }
                catch (InvalidOperationException)
                {
                }
            }
            else if (item is CSequence nested)
            {
                try
                {
                    return FindOperator(nested, name);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        throw new InvalidOperationException($"The PDF operator '{name}' was not found.");
    }

    private static bool ContainsInteger(CSequence sequence)
    {
        foreach (CObject item in sequence)
        {
            if (item is CInteger)
                return true;
            if (item is CSequence nested && ContainsInteger(nested))
                return true;
        }

        return false;
    }

    private static string GetText(CSequence sequence)
    {
        var values = new List<string>();
        CollectStrings(sequence, values);
        return string.Join(' ', values);
    }

    private static void CollectStrings(CSequence sequence, List<string> values)
    {
        foreach (CObject item in sequence)
        {
            if (item is CString text)
                values.Add(text.Value);
            else if (item is CSequence nested)
                CollectStrings(nested, values);
            else if (item is COperator operation)
                CollectStrings(operation.Operands, values);
        }
    }
}
