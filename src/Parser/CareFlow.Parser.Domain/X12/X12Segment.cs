namespace CareFlow.Parser.Domain.X12;

/// <summary>Delimiters of one interchange, read from its ISA header. <see cref="Repetition"/> is <c>'\0'</c> when unused.</summary>
public readonly record struct X12Delimiters(char Element, char Component, char Repetition, char Segment);

/// <summary>A segment split into elements. Element indexes are 1-based, as in the X12 reference designators (CLM01 = <c>[1]</c>).</summary>
public sealed class X12Segment
{
    private readonly string[] _elements;
    private readonly X12Delimiters _delimiters;

    internal X12Segment(string[] elements, int ordinal, X12Delimiters delimiters)
    {
        _elements = elements;
        _delimiters = delimiters;
        Ordinal = ordinal;
    }

    public string Id => _elements[0];

    /// <summary>1-based position of the segment in the interchange.</summary>
    public int Ordinal { get; }

    public int ElementCount => _elements.Length - 1;

    /// <summary>Element value, or an empty string when the element is absent.</summary>
    public string this[int index] => index >= 1 && index < _elements.Length ? _elements[index] : string.Empty;

    public bool Has(int index) => this[index].Length > 0;

    /// <summary>1-based component of a composite element, or an empty string.</summary>
    public string Component(int element, int component)
    {
        var parts = Components(element);
        return component >= 1 && component <= parts.Length ? parts[component - 1] : string.Empty;
    }

    public string[] Components(int element) => this[element].Split(_delimiters.Component);

    /// <summary>The raw element values, for building another segment with the same content.</summary>
    internal IReadOnlyList<string> Elements => _elements;

    public override string ToString() => $"{Id} (segment {Ordinal})";
}
