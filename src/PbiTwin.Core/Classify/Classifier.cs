using System.Text.RegularExpressions;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Classify;

public enum ColumnClass
{
    Key,
    PersonName,
    Email,
    Phone,
    Iban,
    Address,
    City,
    Country,
    Category,
    Label,
    FreeText,
    Money,
    Quantity,
    Date,
    Boolean,
    Percent,
    Other,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

public sealed record Classification(ColumnClass Class, Confidence Confidence, string Reason);

/// <summary>
/// Plain heuristics (column name, data type, TOM data category, value patterns). No AI.
/// Every result can be overridden by the user.
/// </summary>
public static partial class Classifier
{
    public const int CategoryMaxDistinct = 50;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"^\+?[0-9 ()\-./]{7,22}$")]
    private static partial Regex PhonePattern();

    /// <summary>1–4 capitalised words; "Jean-Luc" and "O'Neil" count as one word each.</summary>
    [GeneratedRegex(@"^\p{Lu}\p{Ll}*(?:['\-]\p{Lu}?\p{Ll}+)*(?: \p{Lu}\p{Ll}*(?:['\-]\p{Lu}?\p{Ll}+)*){0,3}$")]
    private static partial Regex PersonPattern();

    [GeneratedRegex(@"^[A-Za-z]{0,8}[-_/#]?[0-9][0-9A-Za-z\-_/.]*$")]
    private static partial Regex CodePattern();

    /// <summary>Applied to the original (case-preserving) name, so "Paid" or "Valid" do not count.</summary>
    [GeneratedRegex(@"(?:Key|KEY|ID|Id|Code|CODE|Number|No|Num|^id|^key|_id|_key)$")]
    private static partial Regex KeyName();

    [GeneratedRegex(@"(amount|price|cost|revenue|sales|salary|wage|margin|profit|value|total|budget|tax|fee|income|expense|balance|payment|spend)")]
    private static partial Regex MoneyName();

    [GeneratedRegex(@"(pct|percent|rate|ratio|share|discount)")]
    private static partial Regex PercentName();

    private static readonly string[] PersonNames =
    [
        "name", "fullname", "firstname", "lastname", "surname", "givenname", "familyname", "middlename", "personname",
        "contactname", "customername", "employeename", "managername", "username", "ownername", "salesperson", "salesrep",
        "contact", "person", "owner", "author", "manager", "employee", "representative",
    ];

    // Column names in other languages (letters only, lower case, as Normalize produces them).
    private static readonly string[] PersonHints = ["прізвище", "призвище", "імя", "піб", "фіо", "фамилия", "имя", "фио", "nachname", "vorname", "kundenname", "prenom", "apellido", "nazwisko", "imie"];

    private static readonly string[] PhoneHints = ["телефон", "мобільний", "мобильный", "telefon", "telefono", "téléphone", "komorka", "handy"];

    private static readonly string[] AddressHints = ["адреса", "адрес", "вулиця", "улица", "adresse", "direccion", "dirección", "strasse", "ulica", "adres"];

    private static readonly string[] CityHints = ["місто", "город", "stadt", "ville", "ciudad", "miasto"];

    private static readonly string[] CountryHints = ["країна", "страна", "país", "kraj"];

    /// <summary>Numbers that identify a person or account (phone, tax id, passport): mapped one-to-one, not scaled.</summary>
    private static readonly string[] NumericIdHints = ["phone", "mobile", "ssn", "passport", "taxid", "nationalid", "iban", "телефон", "паспорт", "інн", "іпн"];

    private static bool HasAny(string name, string[] hints) => hints.Any(h => name.Contains(h, StringComparison.Ordinal));

    public static bool IsEmail(string s) => EmailPattern().IsMatch(s);

    public static bool IsIban(string s) => IbanPattern().IsMatch(s.Replace(" ", string.Empty, StringComparison.Ordinal));

    public static bool IsPhone(string s) => PhonePattern().IsMatch(s) && s.Count(char.IsAsciiDigit) >= 7;

    public static bool IsPersonName(string s) => PersonPattern().IsMatch(s);

    public static bool IsCode(string s) => s.Length <= 40 && CodePattern().IsMatch(s);

    public static Classification Classify(ColumnInfo column, ColumnProfile profile, bool inRelationship)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(profile);

        var name = Normalize(column.Name);
        var category = column.DataCategory?.ToUpperInvariant();

        if (column.Type == ColumnType.DateTime)
        {
            return new(ColumnClass.Date, Confidence.High, "date/time column");
        }

        if (inRelationship)
        {
            return new(ColumnClass.Key, Confidence.High, "used in a relationship");
        }

        if (column.Type == ColumnType.Boolean)
        {
            return new(ColumnClass.Boolean, Confidence.High, "true/false column");
        }

        if (column.IsNumeric)
        {
            return ClassifyNumber(column, profile, name);
        }

        if (column.Type != ColumnType.Text)
        {
            return new(ColumnClass.Other, Confidence.Low, "unsupported data type, values are replaced by blanks");
        }

        return ClassifyText(column, profile, name, category);
    }

    private static Classification ClassifyNumber(ColumnInfo column, ColumnProfile p, string name)
    {
        if (column.Type == ColumnType.Int64 && NumericIdHints.Any(h => name.StartsWith(h, StringComparison.Ordinal) || name.EndsWith(h, StringComparison.Ordinal)))
        {
            return new(ColumnClass.Key, Confidence.Medium, "number that identifies a person or account");
        }

        if (column.IsKey || (column.Type == ColumnType.Int64 && KeyName().IsMatch(column.Name)))
        {
            return new(ColumnClass.Key, column.IsKey ? Confidence.High : Confidence.Medium, column.IsKey ? "marked as key" : "key-like name");
        }

        if (column.Type != ColumnType.Int64 && p.Min >= 0 && p.Max <= 1 && (PercentName().IsMatch(name) || p.Max < 1))
        {
            return new(ColumnClass.Percent, PercentName().IsMatch(name) ? Confidence.High : Confidence.Medium, "values between 0 and 1");
        }

        if (MoneyName().IsMatch(name) || column.Type == ColumnType.Decimal)
        {
            return new(ColumnClass.Money, MoneyName().IsMatch(name) ? Confidence.High : Confidence.Medium, "amount-like column");
        }

        return column.Type == ColumnType.Int64
            ? new(ColumnClass.Quantity, Confidence.Medium, "whole number, not a key")
            : new(ColumnClass.Money, Confidence.Low, "decimal number");
    }

    private static Classification ClassifyText(ColumnInfo column, ColumnProfile p, string name, string? category)
    {
        if (p.Rate(IsEmail) >= 0.8 || name.Contains("email", StringComparison.Ordinal))
        {
            return new(ColumnClass.Email, p.Rate(IsEmail) >= 0.8 ? Confidence.High : Confidence.Medium, "email addresses");
        }

        if (p.Rate(IsIban) >= 0.8 || name.Contains("iban", StringComparison.Ordinal))
        {
            return new(ColumnClass.Iban, Confidence.High, "IBAN account numbers");
        }

        if (name.Contains("phone", StringComparison.Ordinal) || name.Contains("mobile", StringComparison.Ordinal)
            || name is "tel" or "telephone" or "fax" || HasAny(name, PhoneHints) || (p.Rate(IsPhone) >= 0.8 && p.Rate(IsCode) < 0.5))
        {
            return new(ColumnClass.Phone, Confidence.High, "phone numbers");
        }

        if (category is "ADDRESS" || name.Contains("address", StringComparison.Ordinal) || name.Contains("street", StringComparison.Ordinal) || HasAny(name, AddressHints))
        {
            return new(ColumnClass.Address, Confidence.High, "street addresses");
        }

        if (category is "CITY" || name is "city" or "town" || name.EndsWith("city", StringComparison.Ordinal) || HasAny(name, CityHints))
        {
            return new(ColumnClass.City, Confidence.High, "city names");
        }

        if (category is "COUNTRY" or "STATEORPROVINCE" or "COUNTY" or "CONTINENT" || name.Contains("country", StringComparison.Ordinal)
            || name is "nation" or "state" or "province" || name is "pays" or "pais" || HasAny(name, CountryHints))
        {
            return new(ColumnClass.Country, Confidence.High, "country / region names");
        }

        if (PersonNames.Contains(name) || name.EndsWith("fullname", StringComparison.Ordinal) || name.EndsWith("firstname", StringComparison.Ordinal)
            || name.EndsWith("lastname", StringComparison.Ordinal) || HasAny(name, PersonHints))
        {
            var looksLikePeople = p.Rate(IsPersonName) >= 0.7;
            if (looksLikePeople || name != "name")
            {
                return new(ColumnClass.PersonName, looksLikePeople ? Confidence.High : Confidence.Medium, "person names");
            }
        }

        if (p.AvgLength > 30 || p.AvgWords >= 5)
        {
            return new(ColumnClass.FreeText, Confidence.Medium, "long free text");
        }

        if (p.Distinct > CategoryMaxDistinct && p.Rate(IsCode) >= 0.9)
        {
            return new(ColumnClass.Key, Confidence.Medium, "identifier-like codes");
        }

        return p.Distinct <= CategoryMaxDistinct
            ? new(ColumnClass.Category, Confidence.Medium, $"{p.Distinct} distinct values")
            : new(ColumnClass.Label, Confidence.Medium, "names / labels");
    }

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
