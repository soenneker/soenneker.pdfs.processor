using System.Buffers;
using Soenneker.Utils.MemoryStream.Abstract;
using System.Text;

namespace Soenneker.Pdfs.Processor.Internal;

internal sealed class PdfContentDocument
{
    internal List<PdfContentEntry> Entries { get; } = [];

    internal static PdfContentDocument Parse(byte[] data)
    {
        var result = new PdfContentDocument();
        var reader = new PdfSyntaxReader(data, 0);
        var operands = new List<PdfValue>();

        while (true)
        {
            reader.SkipWhiteSpaceAndComments();
            if (reader.End)
                break;

            int tokenStart = reader.Position;
            if (reader.Current is (byte)'<' or (byte)'(' or (byte)'[' or (byte)'/' or
                (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' or
                (byte)'t' or (byte)'f' or (byte)'n')
            {
                try
                {
                    operands.Add(reader.ReadValue());
                    continue;
                }
                catch (InvalidDataException)
                {
                    reader.Position = tokenStart;
                }
            }

            string operation = reader.ReadOperatorToken();
            if (operation.Length == 0)
                throw new InvalidDataException($"An invalid PDF content token was encountered at byte {tokenStart}.");

            if (operation == "BI")
            {
                if (operands.Count > 0)
                    throw new InvalidDataException("An inline PDF image was preceded by unresolved operands.");
                int end = FindInlineImageEnd(data, reader.Position);
                result.Entries.Add(new PdfContentRaw(data.AsSpan(tokenStart, end - tokenStart).ToArray()));
                reader.Position = end;
                continue;
            }

            result.Entries.Add(new PdfContentOperation(operation, operands));
            operands = [];
        }

        if (operands.Count > 0)
            throw new InvalidDataException("The PDF content stream ended with unresolved operands.");
        return result;
    }

    internal byte[] ToBytes(IMemoryStreamUtil memoryStreamUtil)
    {
        using MemoryStream output = memoryStreamUtil.GetSync();
        foreach (PdfContentEntry entry in Entries)
        {
            if (entry is PdfContentRaw raw)
            {
                output.Write(raw.Value);
                output.WriteByte((byte)'\n');
                continue;
            }

            var operation = (PdfContentOperation)entry;
            foreach (PdfValue operand in operation.Operands)
            {
                WriteValue(output, operand);
                output.WriteByte((byte)' ');
            }
            WriteAscii(output, operation.Name);
            output.WriteByte((byte)'\n');
        }
        return output.ToArray();
    }

    private static int FindInlineImageEnd(byte[] data, int start)
    {
        for (var index = start + 2; index + 2 < data.Length; index++)
        {
            if (data[index] == (byte)'E' && data[index + 1] == (byte)'I' &&
                IsWhiteSpace(data[index - 1]) && IsWhiteSpace(data[index + 2]))
                return index + 2;
        }
        throw new NotSupportedException("An inline PDF image could not be delimited safely.");
    }

    private static void WriteValue(Stream output, PdfValue value)
    {
        switch (value)
        {
            case PdfNull: WriteAscii(output, "null"); break;
            case PdfBoolean boolean: WriteAscii(output, boolean.Value ? "true" : "false"); break;
            case PdfNumber number: WriteAscii(output, number.ToString()); break;
            case PdfName name: WriteAscii(output, "/" + name.Value); break;
            case PdfString text: WriteString(output, text); break;
            case PdfReference reference: WriteAscii(output, $"{reference.ObjectNumber} {reference.Generation} R"); break;
            case PdfArray array:
                output.WriteByte((byte)'[');
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) output.WriteByte((byte)' ');
                    WriteValue(output, array.Items[index]);
                }
                output.WriteByte((byte)']');
                break;
            case PdfDictionary dictionary:
                WriteAscii(output, "<<");
                foreach ((string key, PdfValue item) in dictionary.Items)
                {
                    WriteAscii(output, " /" + key + " ");
                    WriteValue(output, item);
                }
                WriteAscii(output, " >>");
                break;
            default: throw new InvalidDataException($"Unsupported PDF content value {value.GetType().Name}.");
        }
    }

    private static void WriteString(Stream output, PdfString text)
    {
        if (text.Hex)
        {
            WriteAscii(output, "<" + Convert.ToHexString(text.Value) + ">");
            return;
        }

        output.WriteByte((byte)'(');
        foreach (byte character in text.Value)
        {
            switch (character)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(character);
                    break;
                case (byte)'\n': WriteAscii(output, "\\n"); break;
                case (byte)'\r': WriteAscii(output, "\\r"); break;
                case (byte)'\t': WriteAscii(output, "\\t"); break;
                case < 32 or > 126: WriteAscii(output, "\\" + Convert.ToString(character, 8)!.PadLeft(3, '0')); break;
                default: output.WriteByte(character); break;
            }
        }
        output.WriteByte((byte)')');
    }

    private static void WriteAscii(Stream output, string value)
    {
        int length = Encoding.Latin1.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> bytes = length <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            int written = Encoding.Latin1.GetBytes(value.AsSpan(), bytes);
            output.Write(bytes[..written]);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
}

internal abstract record PdfContentEntry;

internal sealed record PdfContentOperation(string Name, List<PdfValue> Operands) : PdfContentEntry;

internal sealed record PdfContentRaw(byte[] Value) : PdfContentEntry;
