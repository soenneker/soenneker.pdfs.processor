using System.IO.Compression;
using System.Text;
using System.Threading;
using Soenneker.Pdfs.Processor.Abstract;
using Soenneker.Pdfs.Processor.Enums;
using Soenneker.Pdfs.Processor.Models;
using Soenneker.Pdfs.Processor.Options;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Pdfs.Processor.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class PdfSecurityTests : HostedUnitTest
{
    private readonly IPdfProcessor _processor;

    public PdfSecurityTests(Host host) : base(host) => _processor = Resolve<IPdfProcessor>(true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rejects_deep_syntax_without_swallowing_the_limit(bool dictionaries, CancellationToken cancellationToken)
    {
        string nested = dictionaries
            ? string.Concat(Enumerable.Repeat("<< /K ", 2000)) + "null" + string.Concat(Enumerable.Repeat(" >>", 2000))
            : new string('[', 2000) + "null" + new string(']', 2000);
        using MemoryStream source = CreatePdf($"1 0 obj << /Type /Catalog /Pages 2 0 R /Extra {nested} >> endobj");
        using var output = new MemoryStream();
        await ExpectFailure<IOException>(() => _processor.Optimize(source, output, cancellationToken: cancellationToken).AsTask(), "nesting");
    }

    [Test]
    public async Task Rejects_deep_indirect_graphs_during_merge_and_rewrite(CancellationToken cancellationToken)
    {
        var objects = new StringBuilder("1 0 obj << /Type /Catalog /Pages 2 0 R /Extra 10 0 R >> endobj\n");
        objects.Append("3 0 obj << /Type /Page /Parent 2 0 R /Extra 10 0 R >> endobj\n");
        for (var number = 10; number < 1010; number++)
            objects.Append($"{number} 0 obj << /Next {number + 1} 0 R >> endobj\n");
        objects.Append("1010 0 obj null endobj");
        using MemoryStream source = CreatePdf(objects.ToString());
        using var output = new MemoryStream();
        await ExpectFailure<IOException>(() => _processor.Optimize(source, output, cancellationToken: cancellationToken).AsTask(), "nesting");
        await ExpectFailure<IOException>(() => _processor.Merge([new PdfMergeSource { Stream = source }], output,
            cancellationToken: cancellationToken).AsTask(), "nesting");
    }

    [Test]
    [Arguments("FlateDecode")]
    [Arguments("RunLengthDecode")]
    public async Task Rejects_stream_expansion_before_exhausting_memory(string filter, CancellationToken cancellationToken)
    {
        byte[] encoded = filter == "FlateDecode"
            ? CompressZeros(17 * 1024 * 1024)
            : Enumerable.Range(0, 17 * 1024 * 1024 / 128).SelectMany(_ => new byte[] { 129, 0 }).ToArray();
        using MemoryStream source = CreateStreamPdf(encoded, $"/Filter /{filter}");
        using var output = new MemoryStream();
        if (filter == "FlateDecode")
            await ExpectFailure<IOException>(() => _processor.Optimize(source, output, cancellationToken: cancellationToken).AsTask(), "budget");
        else
            await ExpectFailure<IOException>(() => _processor.ReplaceText(source, output,
                [new PdfTextReplacement { Search = "TOKEN", Replacement = "VALUE" }], cancellationToken: cancellationToken).AsTask(), "budget");
    }

    [Test]
    public async Task Rejects_cumulative_decoding_across_streams(CancellationToken cancellationToken)
    {
        byte[] compressed = CompressZeros(15 * 1024 * 1024);
        string streamData = Encoding.Latin1.GetString(compressed);
        string objects = string.Concat(Enumerable.Range(10, 9).Select(number =>
            $"{number} 0 obj << /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n{streamData}\nendstream\nendobj\n"));
        using MemoryStream source = CreatePdf(objects);
        using var output = new MemoryStream();
        await ExpectFailure<IOException>(() => _processor.Optimize(source, output, cancellationToken: cancellationToken).AsTask(), "budget");
    }

    [Test]
    [Arguments(2147483647, 0, "")]
    [Arguments(1, 4, "5 -1 null")]
    [Arguments(1, 4, "5 2147483647 null")]
    public async Task Rejects_invalid_object_stream_counts_and_offsets(int count, int first, string data, CancellationToken cancellationToken)
    {
        using MemoryStream source = CreateStreamPdf(Encoding.ASCII.GetBytes(data), $"/Type /ObjStm /N {count} /First {first}");
        using var output = new MemoryStream();
        await ExpectFailure<InvalidDataException>(() => _processor.Optimize(source, output, cancellationToken: cancellationToken).AsTask(), "object-stream");
    }

    [Test]
    [Arguments(100000)]
    [Arguments(2147483647)]
    public async Task Sparse_object_ids_produce_small_valid_xref_subsections(int number, CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf($"1 0 obj << /Type /Catalog /Pages 2 0 R /Extra {number} 0 R >> endobj\n{number} 0 obj (retained) endobj");
        using var output = new MemoryStream();
        await _processor.Optimize(source, output, cancellationToken: cancellationToken);
        string pdf = Encoding.Latin1.GetString(output.ToArray());
        await Assert.That(output.Length < 2000).IsTrue();
        await Assert.That(pdf).Contains($"/Size {(long)number + 1}");
        await Assert.That(pdf).Contains($"{number} 1\n");
        string objectHeader = $"{number} 0 obj\n";
        int offset = pdf.IndexOf(objectHeader, StringComparison.Ordinal);
        await Assert.That(pdf).Contains($"{offset:0000000000} 00000 n");
        using var roundTrip = new MemoryStream();
        await _processor.Optimize(output, roundTrip, cancellationToken: cancellationToken);
        await Assert.That(Encoding.Latin1.GetString(roundTrip.ToArray())).Contains("retained");
    }

    [Test]
    public async Task Removes_annotation_field_outline_and_chained_actions_but_preserves_annotations(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf("""
            1 0 obj << /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] >> /Outlines 11 0 R >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R /Annots [<< /Subtype /Widget /Contents (keep annotation) /AA << /PO 12 0 R >> /A << /S /URI /URI (https://example.invalid) /Next 12 0 R >> >>] >> endobj
            10 0 obj << /FT /Tx /T (keep field) /Kids [<< /AA << /K 12 0 R >> >>] /AA << /V 12 0 R >> >> endobj
            11 0 obj << /First << /Title (keep outline) /A 12 0 R >> >> endobj
            12 0 obj << /S /JavaScript /JS (security-marker) /Next 12 0 R >> endobj
            """);
        using var output = new MemoryStream();
        await _processor.Optimize(source, output, new PdfOutputOptions { RemoveJavaScriptAndActions = true }, cancellationToken);
        string pdf = Encoding.Latin1.GetString(output.ToArray());
        await Assert.That(pdf).DoesNotContain("security-marker");
        await Assert.That(pdf).DoesNotContain("/JavaScript");
        await Assert.That(pdf).DoesNotContain("/AA");
        await Assert.That(pdf).DoesNotContain("/URI");
        await Assert.That(pdf).Contains("keep annotation");
        await Assert.That(pdf).Contains("keep field");
        await Assert.That(pdf).Contains("keep outline");
    }

    [Test]
    public async Task Keeps_actions_when_removal_is_disabled(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreatePdf("3 0 obj << /Type /Page /Parent 2 0 R /Annots [<< /A << /S /JavaScript /JS (keep-script) >> >>] >> endobj");
        using var output = new MemoryStream();
        await _processor.Optimize(source, output, cancellationToken: cancellationToken);
        await Assert.That(Encoding.Latin1.GetString(output.ToArray())).Contains("keep-script");
    }

    [Test]
    public async Task Accepts_valid_object_streams_and_normal_compressed_documents(CancellationToken cancellationToken)
    {
        using MemoryStream source = CreateStreamPdf(Encoding.ASCII.GetBytes("5 0 (valid)"), "/Type /ObjStm /N 1 /First 4");
        using var output = new MemoryStream();
        await _processor.Optimize(source, output, cancellationToken: cancellationToken);

        byte[] compressed = CompressZeros(1024);
        using MemoryStream compressedSource = CreateStreamPdf(compressed, "/Filter /FlateDecode");
        using var decodedOutput = new MemoryStream();
        await _processor.Optimize(compressedSource, decodedOutput, new PdfOutputOptions { Compression = PdfCompressionProfile.None }, cancellationToken);
        await Assert.That(decodedOutput.Length > 1024).IsTrue();
    }

    private static async Task ExpectFailure<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            await Assert.That(exception.Message).Contains(message);
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{message}'.");
    }

    private static byte[] CompressZeros(int count)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, true))
        {
            var chunk = new byte[8192];
            for (int remaining = count; remaining > 0; remaining -= Math.Min(chunk.Length, remaining))
                compressor.Write(chunk, 0, Math.Min(chunk.Length, remaining));
        }
        return output.ToArray();
    }

    private static MemoryStream CreateStreamPdf(byte[] data, string attributes) =>
        CreatePdf($"4 0 obj << /Length {data.Length} {attributes} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream\nendobj");

    private static MemoryStream CreatePdf(string extraObjects) => new(Encoding.Latin1.GetBytes($"""
        %PDF-1.7
        1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
        3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >> endobj
        4 0 obj << /Length 0 >> stream

        endstream
        endobj
        {extraObjects}
        trailer << /Root 1 0 R >>
        %%EOF
        """));
}

