using System.Text;

namespace PbiTwin.Core.Text;

public enum DaxTokenKind
{
    /// <summary>Anything else: keywords, functions, operators, numbers, whitespace, unquoted names.</summary>
    Code,

    /// <summary>String literal <c>"..."</c>; <see cref="DaxToken.Value"/> has <c>""</c> unescaped.</summary>
    String,

    /// <summary>Quoted table name <c>'...'</c>; <see cref="DaxToken.Value"/> has <c>''</c> unescaped.</summary>
    QuotedName,

    /// <summary>Column or measure reference <c>[...]</c>; <see cref="DaxToken.Value"/> has <c>]]</c> unescaped.</summary>
    BracketName,

    /// <summary><c>// ...</c>, <c>-- ...</c> or <c>/* ... */</c>.</summary>
    Comment,
}

public readonly record struct DaxToken(DaxTokenKind Kind, string Raw, string Value);

/// <summary>
/// A small lexer that splits DAX into code, strings, quoted names, bracketed names and comments.
/// It does not parse DAX — it only needs to know reliably what is a literal and what is a name.
/// </summary>
public static class DaxTokenizer
{
    public static List<DaxToken> Tokenize(string dax)
    {
        ArgumentNullException.ThrowIfNull(dax);

        var tokens = new List<DaxToken>();
        var code = new StringBuilder();
        var i = 0;
        while (i < dax.Length)
        {
            var c = dax[i];
            var next = i + 1 < dax.Length ? dax[i + 1] : '\0';

            if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                var end = dax.IndexOf('\n', i);
                end = end < 0 ? dax.Length : end;
                Add(DaxTokenKind.Comment, i, end, null);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = dax.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? dax.Length : end + 2;
                Add(DaxTokenKind.Comment, i, end, null);
                i = end;
            }
            else if (c is '"' or '\'' or '[')
            {
                var close = c == '[' ? ']' : c;
                var kind = c switch { '"' => DaxTokenKind.String, '\'' => DaxTokenKind.QuotedName, _ => DaxTokenKind.BracketName };
                var value = new StringBuilder();
                var j = i + 1;
                while (j < dax.Length)
                {
                    if (dax[j] == close)
                    {
                        if (j + 1 < dax.Length && dax[j + 1] == close)
                        {
                            value.Append(close);
                            j += 2;
                            continue;
                        }

                        j++;
                        break;
                    }

                    value.Append(dax[j]);
                    j++;
                }

                Add(kind, i, j, value.ToString());
                i = j;
            }
            else
            {
                code.Append(c);
                i++;
            }
        }

        FlushCode();
        return tokens;

        void Add(DaxTokenKind kind, int start, int end, string? value)
        {
            FlushCode();
            var raw = dax[start..end];
            tokens.Add(new DaxToken(kind, raw, value ?? raw));
        }

        void FlushCode()
        {
            if (code.Length > 0)
            {
                var s = code.ToString();
                tokens.Add(new DaxToken(DaxTokenKind.Code, s, s));
                code.Clear();
            }
        }
    }

    public static string StringLiteral(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Join(IEnumerable<DaxToken> tokens) => string.Concat(tokens.Select(t => t.Raw));
}
