namespace Soenneker.Pdfs.Processor.Internal;

// Kept separate from InvalidDataException so tolerant parsing cannot swallow resource-limit failures.
internal sealed class PdfLimitExceededException(string message) : IOException(message);
