namespace Soenneker.Pdfs.Processor.Internal;

/// <summary>
/// Identifies a character offset within a parsed PDF text fragment.
/// </summary>
internal readonly record struct FragmentPosition(int Index, int Offset);
