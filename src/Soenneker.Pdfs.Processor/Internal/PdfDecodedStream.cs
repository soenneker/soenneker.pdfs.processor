namespace Soenneker.Pdfs.Processor.Internal;

internal sealed class PdfDecodedStream(MemoryStream output, PdfDecodeBudget budget) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => output.Length;
    public override long Position { get => output.Position; set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        budget.Charge(output.Length, buffer.Length);
        output.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        budget.Charge(output.Length, 1);
        output.WriteByte(value);
    }

    internal byte[] ToArray() => output.ToArray();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            output.Dispose();
        base.Dispose(disposing);
    }
}
