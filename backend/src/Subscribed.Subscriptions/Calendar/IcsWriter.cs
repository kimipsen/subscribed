using System.Text;

namespace Subscribed.Subscriptions.Calendar;

/// <summary>
/// Writes content lines the way RFC 5545 asks: CRLF line ends, text escaping, no control characters, and folding at 75 octets.
/// Folding walks runes, so a line is never broken inside a multi-byte UTF-8 character or a surrogate pair.
/// </summary>
internal sealed class IcsWriter
{
    private const int MaxOctets = 75;

    private readonly StringBuilder _out = new();

    public void Begin(string component) => Line("BEGIN:" + component);

    public void End(string component) => Line("END:" + component);

    /// <summary>A property whose value is already safe (numbers, dates, fixed words).</summary>
    public void Property(string nameAndParameters, string value) => Line(nameAndParameters + ":" + value);

    /// <summary>A TEXT property: the value is escaped.</summary>
    public void Text(string name, string text) => Line(name + ":" + EscapeText(text));

    /// <summary>Appends one content line, folded, with a CRLF end.</summary>
    public void Line(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var used = 0;
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in content.EnumerateRunes())
        {
            var octets = rune.Utf8SequenceLength;
            if (used + octets > MaxOctets)
            {
                _out.Append("\r\n ");
                used = 1;
            }

            var length = rune.EncodeToUtf16(buffer);
            _out.Append(buffer[..length]);
            used += octets;
        }

        _out.Append("\r\n");
    }

    public override string ToString() => _out.ToString();

    /// <summary>Escapes <c>\ ; ,</c> and newlines, and drops every other control character.</summary>
    public static string EscapeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new StringBuilder(normalised.Length);
        foreach (var rune in normalised.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\n': result.Append("\\n"); break;
                case '\\': result.Append("\\\\"); break;
                case ';': result.Append("\\;"); break;
                case ',': result.Append("\\,"); break;
                default:
                    if (!Rune.IsControl(rune))
                    {
                        result.Append(rune);
                    }

                    break;
            }
        }

        return result.ToString();
    }
}
