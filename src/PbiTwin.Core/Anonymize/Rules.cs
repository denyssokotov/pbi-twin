using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Anonymize;

/// <summary>What happens to the values of a column.</summary>
public enum Rule
{
    /// <summary>Values are copied unchanged.</summary>
    Keep,

    /// <summary>Values are replaced by blanks.</summary>
    Blank,

    /// <summary>One-to-one replacement: numbers become 1..n, text becomes <c>AB-000001</c>.</summary>
    Key,

    PersonName,
    Email,
    Phone,
    Iban,
    Address,

    /// <summary>One-to-one replacement with invented city names.</summary>
    City,

    /// <summary>One-to-one replacement with invented country names.</summary>
    Country,

    /// <summary>One-to-one replacement with <c>Label 01</c>-style values.</summary>
    Label,

    /// <summary>Lorem ipsum of similar length.</summary>
    FreeText,

    /// <summary>Multiplied by the shared money factor plus per-row noise.</summary>
    Money,

    /// <summary>Multiplied by the quantity factor plus noise, rounded to whole numbers.</summary>
    Quantity,

    /// <summary>Shifted by the global date offset.</summary>
    DateShift,
}

public static class Rules
{
    public static Rule Default(ColumnClass c, ColumnType type) => c switch
    {
        ColumnClass.Key when type == ColumnType.DateTime => Rule.DateShift,
        ColumnClass.Key => Rule.Key,
        ColumnClass.PersonName => Rule.PersonName,
        ColumnClass.Email => Rule.Email,
        ColumnClass.Phone => Rule.Phone,
        ColumnClass.Iban => Rule.Iban,
        ColumnClass.Address => Rule.Address,
        ColumnClass.City => Rule.City,
        ColumnClass.Country => Rule.Country,
        ColumnClass.Category or ColumnClass.Label => Rule.Label,
        ColumnClass.FreeText => Rule.FreeText,
        ColumnClass.Money => Rule.Money,
        ColumnClass.Quantity => Rule.Quantity,
        ColumnClass.Date => Rule.DateShift,
        ColumnClass.Boolean or ColumnClass.Percent => Rule.Keep,
        _ => Rule.Blank,
    };

    /// <summary>Rules that make sense for a data type (for the UI drop-down).</summary>
    public static IReadOnlyList<Rule> Allowed(ColumnType type) => type switch
    {
        ColumnType.Text => [Rule.Label, Rule.Key, Rule.PersonName, Rule.Email, Rule.Phone, Rule.Iban, Rule.Address, Rule.City, Rule.Country, Rule.FreeText, Rule.Blank, Rule.Keep],
        ColumnType.Int64 => [Rule.Key, Rule.Quantity, Rule.Money, Rule.Blank, Rule.Keep],
        ColumnType.Double or ColumnType.Decimal => [Rule.Money, Rule.Quantity, Rule.Key, Rule.Blank, Rule.Keep],
        ColumnType.DateTime => [Rule.DateShift, Rule.Blank, Rule.Keep],
        ColumnType.Boolean => [Rule.Keep, Rule.Blank],
        _ => [Rule.Blank, Rule.Keep],
    };

    /// <summary>Rules that build a one-to-one dictionary from all distinct values before writing.</summary>
    public static bool UsesDictionary(Rule r) => r is Rule.Key or Rule.City or Rule.Country or Rule.Label;

    /// <summary>Rules whose output is text derived from original text (subject to leak scan and literal rewriting).</summary>
    public static bool ReplacesText(Rule r) => r is not (Rule.Keep or Rule.Money or Rule.Quantity or Rule.DateShift);
}
