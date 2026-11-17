using PbiTwin.Core.Classify;
using PbiTwin.Core.Metadata;

namespace PbiTwin.Core.Tests.Classify;

public class ClassifierTests
{
    [Theory]
    [InlineData("olena.k@ukr.net", true)]
    [InlineData("first.last+tag@example.co.uk", true)]
    [InlineData("not an email", false)]
    [InlineData("a@b", false)]
    public void Email(string s, bool expected) => Assert.Equal(expected, Classifier.IsEmail(s));

    [Theory]
    [InlineData("UA213223130000026007233566001", true)]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("GB29NWBK60161331926819", true)]
    [InlineData("SO-2024-000123", false)]
    public void Iban(string s, bool expected) => Assert.Equal(expected, Classifier.IsIban(s));

    [Theory]
    [InlineData("+380 67 123 45 67", true)]
    [InlineData("+1 (415) 555-0123", true)]
    [InlineData("12345", false)]
    [InlineData("SO-2024-000123", false)]
    public void Phone(string s, bool expected) => Assert.Equal(expected, Classifier.IsPhone(s));

    [Theory]
    [InlineData("Olena Kovalenko", true)]
    [InlineData("Jean-Luc O'Neil", true)]
    [InlineData("Woodgrove Pro Laptop X200", false)]
    [InlineData("lower case", false)]
    public void PersonName(string s, bool expected) => Assert.Equal(expected, Classifier.IsPersonName(s));

    private static ColumnProfile Profile(params object?[] values)
    {
        var p = new ColumnProfile();
        foreach (var v in values)
        {
            p.Add(v);
        }

        return p;
    }

    private static ColumnInfo Col(string name, ColumnType type, bool isKey = false, string? category = null) =>
        new("T", name, type, name, isKey, false, category, false);

    [Fact]
    public void Relationship_columns_are_keys() =>
        Assert.Equal(ColumnClass.Key, Classifier.Classify(Col("Anything", ColumnType.Int64), Profile(1L, 2L), inRelationship: true).Class);

    [Fact]
    public void Paid_is_not_a_key_name() =>
        Assert.NotEqual(ColumnClass.Key, Classifier.Classify(Col("Paid", ColumnType.Int64), Profile(1L, 2L), false).Class);

    [Theory]
    [InlineData("SalesAmount", ColumnType.Decimal, ColumnClass.Money)]
    [InlineData("Salary", ColumnType.Decimal, ColumnClass.Money)]
    [InlineData("Quantity", ColumnType.Int64, ColumnClass.Quantity)]
    [InlineData("ManagerKey", ColumnType.Int64, ColumnClass.Key)]
    [InlineData("OrderDate", ColumnType.DateTime, ColumnClass.Date)]
    public void Numbers_and_dates(string name, ColumnType type, ColumnClass expected)
    {
        object? sample = type switch { ColumnType.Decimal => 120.5m, ColumnType.DateTime => DateTime.Today, _ => 3L };
        Assert.Equal(expected, Classifier.Classify(Col(name, type), Profile(sample), false).Class);
    }

    [Theory]
    [InlineData("Прізвище", ColumnClass.PersonName)]
    [InlineData("ПІБ клієнта", ColumnClass.PersonName)]
    [InlineData("Nachname", ColumnClass.PersonName)]
    [InlineData("Imię i nazwisko", ColumnClass.PersonName)]
    [InlineData("Телефон", ColumnClass.Phone)]
    [InlineData("Adresse", ColumnClass.Address)]
    [InlineData("Адреса доставки", ColumnClass.Address)]
    [InlineData("Місто", ColumnClass.City)]
    [InlineData("Stadt", ColumnClass.City)]
    [InlineData("Країна", ColumnClass.Country)]
    [InlineData("Pays", ColumnClass.Country)]
    public void Column_names_in_other_languages_are_recognised(string name, ColumnClass expected) =>
        Assert.Equal(expected, Classifier.Classify(Col(name, ColumnType.Text), Profile("x"), false).Class);

    [Theory]
    [InlineData("PhoneNumber", ColumnClass.Key)]
    [InlineData("MobilePhone", ColumnClass.Key)]
    [InlineData("CustomerSSN", ColumnClass.Key)]
    [InlineData("PassportNo", ColumnClass.Key)]
    [InlineData("ClassSnacks", ColumnClass.Quantity)]
    [InlineData("iPhonesSold", ColumnClass.Quantity)]
    [InlineData("Payslips", ColumnClass.Quantity)]
    public void Numbers_that_identify_someone_are_mapped_but_look_alikes_are_not(string name, ColumnClass expected) =>
        Assert.Equal(expected, Classifier.Classify(Col(name, ColumnType.Int64), Profile(3L), false).Class);

    [Fact]
    public void Discount_between_zero_and_one_is_kept_as_percent() =>
        Assert.Equal(ColumnClass.Percent, Classifier.Classify(Col("Discount", ColumnType.Double), Profile(0.1, 0.2, 0.0), false).Class);

    [Fact]
    public void Text_columns_by_name_category_and_values()
    {
        Assert.Equal(ColumnClass.City, Classifier.Classify(Col("Town", ColumnType.Text), Profile("Kyiv"), false).Class);
        Assert.Equal(ColumnClass.City, Classifier.Classify(Col("Place", ColumnType.Text, category: "City"), Profile("Kyiv"), false).Class);
        Assert.Equal(ColumnClass.Country, Classifier.Classify(Col("Country", ColumnType.Text), Profile("Ukraine"), false).Class);
        Assert.Equal(ColumnClass.Email, Classifier.Classify(Col("Contact", ColumnType.Text), Profile("a.b@c.com", "d.e@f.org"), false).Class);
        Assert.Equal(ColumnClass.PersonName, Classifier.Classify(Col("FullName", ColumnType.Text), Profile("Olena Kovalenko"), false).Class);
        Assert.Equal(ColumnClass.Category, Classifier.Classify(Col("Segment", ColumnType.Text), Profile("SMB", "Enterprise"), false).Class);
        Assert.Equal(ColumnClass.FreeText, Classifier.Classify(Col("Comment", ColumnType.Text), Profile("Customer asked to deliver the parcel after six in the evening"), false).Class);

        var codes = Enumerable.Range(1, 80).Select(i => (object?)$"SO-2024-{i:000000}").ToArray();
        Assert.Equal(ColumnClass.Key, Classifier.Classify(Col("OrderNumber", ColumnType.Text), Profile(codes), false).Class);

        var labels = Enumerable.Range(1, 80).Select(i => (object?)$"Product {i} deluxe").ToArray();
        Assert.Equal(ColumnClass.Label, Classifier.Classify(Col("ProductName", ColumnType.Text), Profile(labels), false).Class);
    }
}
