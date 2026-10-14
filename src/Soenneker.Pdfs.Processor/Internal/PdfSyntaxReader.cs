using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Soenneker.Pdfs.Processor.Internal;

internal sealed class PdfSyntaxReader
{
    private readonly byte[] _data;
    private int _depth;

    internal PdfSyntaxReader(byte[] data, int position)
    {
        _data = data;
        Position = position;
    }

    internal int Position { get; set; }

    internal byte Current => Position < _data.Length ? _data[Position] : (byte)0;

    internal bool End => Position >= _data.Length;

    internal PdfValue ReadValue()
    {
        SkipWhiteSpaceAndComments();
        if (Position >= _data.Length)
            throw Error("Unexpected end of PDF data.");

        PdfProcessingLimits.CheckDepth(++_depth);
        try
        {
            return _data[Position] switch
            {
                (byte)'<' when Peek(1) == (byte)'<' => ReadDictionary(),
                (byte)'<' => ReadHexString(),
                (byte)'(' => ReadLiteralString(),
                (byte)'[' => ReadArray(),
                (byte)'/' => ReadName(),
                (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' => ReadNumberOrReference(),
                _ => ReadKeywordValue()
            };
        }
        finally
        {
            _depth--;
        }
    }

    internal int ReadInteger()
    {
        SkipWhiteSpaceAndComments();
        PdfNumber number = ReadNumber();
        if (!number.IsInteger)
            throw Error("An integer was expected.");
        return number.AsInt32();
    }

    internal void SkipWhiteSpaceAndComments()
    {
        while (Position < _data.Length)
        {
            if (IsWhiteSpace(_data[Position]))
            {
                Position++;
                continue;
            }

            if (_data[Position] != (byte)'%')
                return;
            while (Position < _data.Length && _data[Position] is not ((byte)'\r') and not ((byte)'\n'))
                Position++;
        }
    }

    internal bool TryReadKeyword(ReadOnlySpan<byte> bytes)
    {
        SkipWhiteSpaceAndComments();
        if (Position + bytes.Length > _data.Length || !_data.AsSpan(Position, bytes.Length).SequenceEqual(bytes))
            return false;
        if (Position + bytes.Length < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position + bytes.Length]))
            return false;
        Position += bytes.Length;
        return true;
    }

    internal string ReadOperatorToken()
    {
        SkipWhiteSpaceAndComments();
        return ReadToken();
    }

    internal void ConsumeStreamLineEnding()
    {
        if (Position < _data.Length && _data[Position] == (byte)'\r')
            Position++;
        if (Position < _data.Length && _data[Position] == (byte)'\n')
            Position++;
    }

    internal static bool IsDelimiterOrWhiteSpace(byte value) => IsWhiteSpace(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
        (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private PdfDictionary ReadDictionary()
    {
        Position += 2;
        var items = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (Peek(0) == (byte)'>' && Peek(1) == (byte)'>')
            {
                Position += 2;
                return new PdfDictionary(items);
            }

            PdfName key = ReadName();
            items[key.Value] = ReadValue();
        }
    }

    private PdfArray ReadArray()
    {
        Position++;
        var values = new List<PdfValue>();
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (Peek(0) == (byte)']')
            {
                Position++;
                return new PdfArray(values);
            }
            values.Add(ReadValue());
        }
    }

    private PdfName ReadName()
    {
        if (Peek(0) != (byte)'/')
            throw Error("A PDF name was expected.");
        Position++;
        var bytes = new List<byte>();
        while (Position < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position]))
        {
            if (_data[Position] == (byte)'#' && Position + 2 < _data.Length && TryHex(_data[Position + 1], out int high) && TryHex(_data[Position + 2], out int low))
            {
                bytes.Add((byte)(high * 16 + low));
                Position += 3;
            }
            else
            {
                bytes.Add(_data[Position++]);
            }
        }
        return new PdfName(Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(bytes)));
    }

    private PdfString ReadLiteralString()
    {
        Position++;
        var depth = 1;
        var bytes = new List<byte>();
        while (Position < _data.Length && depth > 0)
        {
            byte value = _data[Position++];
            if (value == (byte)'\\')
            {
                if (Position >= _data.Length)
                    break;
                byte escaped = _data[Position++];
                switch (escaped)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add((byte)'\b'); break;
                    case (byte)'f': bytes.Add((byte)'\f'); break;
                    case (byte)'\r':
                        if (Peek(0) == (byte)'\n') Position++;
                        break;
                    case (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                        var octal = escaped - (byte)'0';
                        for (var count = 1; count < 3 && Position < _data.Length && _data[Position] is >= (byte)'0' and <= (byte)'7'; count++)
                            octal = octal * 8 + _data[Position++] - (byte)'0';
                        bytes.Add((byte)octal);
                        break;
                    default: bytes.Add(escaped); break;
                }
            }
            else if (value == (byte)'(')
            {
                depth++;
                bytes.Add(value);
            }
            else if (value == (byte)')')
            {
                depth--;
                if (depth > 0) bytes.Add(value);
            }
            else
            {
                bytes.Add(value);
            }
        }

        if (depth != 0)
            throw Error("An unterminated PDF string was encountered.");
        return new PdfString(bytes.ToArray());
    }

    private PdfString ReadHexString()
    {
        Position++;
        var digits = new List<int>();
        while (Position < _data.Length && _data[Position] != (byte)'>')
        {
            if (TryHex(_data[Position++], out int digit))
                digits.Add(digit);
        }
        if (Position >= _data.Length)
            throw Error("An unterminated PDF hex string was encountered.");
        Position++;
        if (digits.Count % 2 != 0)
            digits.Add(0);
        var value = new byte[digits.Count / 2];
        for (var index = 0; index < value.Length; index++)
            value[index] = (byte)(digits[index * 2] * 16 + digits[index * 2 + 1]);
        return new PdfString(value, true);
    }

    private PdfValue ReadNumberOrReference()
    {
        PdfNumber first = ReadNumber();
        if (!first.IsInteger)
            return first;

        int afterFirst = Position;
        SkipWhiteSpaceAndComments();
        if (Position < _data.Length && _data[Position] is >= (byte)'0' and <= (byte)'9')
        {
            PdfNumber second = ReadNumber();
            if (second.IsInteger)
            {
                SkipWhiteSpaceAndComments();
                if (TryReadKeyword("R"u8))
                    return new PdfReference(first.AsInt32(), second.AsInt32());
            }
        }
        Position = afterFirst;
        return first;
    }

    private PdfNumber ReadNumber()
    {
        int start = Position;
        if (Peek(0) is (byte)'+' or (byte)'-')
            Position++;
        bool decimalPoint = false;
        while (Position < _data.Length && (_data[Position] is >= (byte)'0' and <= (byte)'9' || !decimalPoint && _data[Position] == (byte)'.'))
        {
            decimalPoint |= _data[Position] == (byte)'.';
            Position++;
        }
        string text = Encoding.ASCII.GetString(_data, start, Position - start);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw Error($"Invalid PDF number '{text}'.");
        return new PdfNumber(value, !decimalPoint);
    }

    private PdfValue ReadKeywordValue()
    {
        string keyword = ReadToken();
        return keyword switch
        {
            "true" => new PdfBoolean(true),
            "false" => new PdfBoolean(false),
            "null" => PdfNull.Instance,
            _ => throw Error($"Unexpected PDF token '{keyword}'.")
        };
    }

    private string ReadToken()
    {
        int start = Position;
        while (Position < _data.Length && !IsDelimiterOrWhiteSpace(_data[Position]))
            Position++;
        return Encoding.ASCII.GetString(_data, start, Position - start);
    }

    private byte Peek(int offset) => Position + offset < _data.Length ? _data[Position + offset] : (byte)0;

    private InvalidDataException Error(string message) => new($"{message} (byte {Position})");

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool TryHex(byte value, out int digit)
    {
        if (value is >= (byte)'0' and <= (byte)'9') digit = value - (byte)'0';
        else if (value is >= (byte)'A' and <= (byte)'F') digit = value - (byte)'A' + 10;
        else if (value is >= (byte)'a' and <= (byte)'f') digit = value - (byte)'a' + 10;
        else { digit = 0; return false; }
        return true;
    }
}
