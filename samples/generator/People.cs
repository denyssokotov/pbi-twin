namespace PbiTwin.SampleGenerator;

/// <summary>Country-specific building blocks for deliberately "sensitive" looking sample data.</summary>
internal sealed record Country(
    string Name,
    string IbanCode,
    int IbanBbanDigits,
    string[] FirstNames,
    string[] LastNames,
    string[] Cities,
    string[] Streets,
    string[] EmailDomains)
{
    public static readonly Country[] All =
    [
        new("Ukraine", "UA", 25,
            ["Olena", "Andrii", "Iryna", "Taras", "Oksana", "Dmytro", "Kateryna", "Bohdan", "Natalia", "Serhii", "Yulia", "Mykola", "Sofiia", "Volodymyr", "Halyna"],
            ["Kovalenko", "Shevchenko", "Bondarenko", "Tkachenko", "Kravchenko", "Melnyk", "Oliinyk", "Polishchuk", "Lysenko", "Moroz", "Savchenko", "Rudenko", "Marchenko", "Hnatiuk", "Pavlenko"],
            ["Kyiv", "Lviv", "Kharkiv", "Odesa", "Dnipro", "Vinnytsia", "Ivano-Frankivsk", "Poltava"],
            ["vul. Khreshchatyk", "vul. Shevchenka", "vul. Franka", "prosp. Peremohy", "vul. Sadova", "vul. Hrushevskoho"],
            ["ukr.net", "gmail.com", "i.ua", "meta.ua"]),
        new("Poland", "PL", 24,
            ["Anna", "Piotr", "Katarzyna", "Tomasz", "Magdalena", "Krzysztof", "Agnieszka", "Pawel", "Joanna", "Marcin", "Ewa", "Grzegorz"],
            ["Nowak", "Kowalski", "Wisniewski", "Wojcik", "Kaminski", "Lewandowski", "Zielinski", "Szymanski", "Wozniak", "Dabrowski", "Mazur", "Krawczyk"],
            ["Warsaw", "Krakow", "Wroclaw", "Gdansk", "Poznan", "Lodz"],
            ["ul. Dluga", "ul. Marszalkowska", "ul. Piotrkowska", "ul. Florianska", "al. Jerozolimskie"],
            ["wp.pl", "onet.pl", "gmail.com", "interia.pl"]),
        new("Germany", "DE", 18,
            ["Lukas", "Hannah", "Felix", "Lea", "Jonas", "Mia", "Maximilian", "Lena", "Paul", "Sophie", "Niklas", "Laura"],
            ["Mueller", "Schmidt", "Schneider", "Fischer", "Weber", "Meyer", "Wagner", "Becker", "Hoffmann", "Schulz", "Koch", "Richter"],
            ["Berlin", "Munich", "Hamburg", "Cologne", "Frankfurt", "Leipzig"],
            ["Hauptstrasse", "Bahnhofstrasse", "Gartenstrasse", "Schillerstrasse", "Lindenallee"],
            ["web.de", "gmx.de", "t-online.de", "gmail.com"]),
        new("USA", "", 0,
            ["James", "Emily", "Michael", "Olivia", "Daniel", "Ava", "Christopher", "Madison", "Matthew", "Abigail", "Joshua", "Samantha"],
            ["Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Anderson", "Thompson", "Harris"],
            ["New York", "Chicago", "Austin", "Seattle", "Denver", "Boston"],
            ["Main St", "Oak Ave", "Maple Dr", "Washington Blvd", "Lake Shore Dr"],
            ["gmail.com", "yahoo.com", "outlook.com", "aol.com"]),
        new("United Kingdom", "GB", 0,
            ["Oliver", "Amelia", "Harry", "Isla", "George", "Poppy", "Jack", "Freya", "Charlie", "Evie"],
            ["Smith", "Taylor", "Evans", "Wilson", "Roberts", "Walker", "Wright", "Thompson", "Hughes", "Edwards"],
            ["London", "Manchester", "Birmingham", "Leeds", "Bristol", "Edinburgh"],
            ["High Street", "Station Road", "Church Lane", "Victoria Road", "Park Avenue"],
            ["btinternet.com", "gmail.com", "hotmail.co.uk", "outlook.com"]),
        new("France", "FR", 23,
            ["Louis", "Chloe", "Hugo", "Camille", "Lucas", "Manon", "Arthur", "Ines", "Jules", "Lea"],
            ["Martin", "Bernard", "Dubois", "Thomas", "Robert", "Richard", "Petit", "Durand", "Leroy", "Moreau"],
            ["Paris", "Lyon", "Marseille", "Toulouse", "Bordeaux", "Lille"],
            ["rue de la Paix", "avenue Victor Hugo", "boulevard Voltaire", "rue Nationale", "rue de Rivoli"],
            ["orange.fr", "free.fr", "gmail.com", "laposte.net"]),
    ];

    /// <summary>Relative share of customers per country (same order as <see cref="All"/>).</summary>
    public static readonly double[] CustomerWeights = [40, 20, 15, 10, 8, 7];

    public string Phone(Rng rng) => Name switch
    {
        "Ukraine" => $"+380 {rng.Pick(["50", "63", "66", "67", "68", "73", "93", "97"])} {rng.Digits(3)} {rng.Digits(2)} {rng.Digits(2)}",
        "Poland" => $"+48 {rng.Next(500, 900)} {rng.Digits(3)} {rng.Digits(3)}",
        "Germany" => $"+49 15{rng.Digits(1)} {rng.Digits(4)} {rng.Digits(4)}",
        "USA" => $"+1 ({rng.Next(201, 990)}) {rng.Digits(3)}-{rng.Digits(4)}",
        "United Kingdom" => $"+44 7{rng.Digits(3)} {rng.Digits(6)}",
        _ => $"+33 {rng.Next(6, 8)} {rng.Digits(2)} {rng.Digits(2)} {rng.Digits(2)} {rng.Digits(2)}",
    };

    public string Address(Rng rng)
    {
        var street = rng.Pick(Streets);
        var no = rng.Next(1, 180);
        return Name switch
        {
            "Ukraine" => rng.Chance(0.6) ? $"{street} {no}, apt {rng.Next(1, 120)}" : $"{street} {no}",
            "USA" or "United Kingdom" => $"{no} {street}",
            "France" => $"{no} {street}",
            _ => $"{street} {no}",
        };
    }

    /// <summary>Syntactically valid IBAN (correct length and mod-97 check digits), or null where IBANs are not used.</summary>
    public string? Iban(Rng rng)
    {
        if (IbanCode.Length == 0)
        {
            return null;
        }

        var bban = IbanCode == "GB"
            ? rng.Pick(["NWBK", "BARC", "LOYD", "HBUK"]) + rng.Digits(14)
            : rng.Digits(IbanBbanDigits);

        return IbanCode + CheckDigits(IbanCode, bban) + bban;
    }

    private static string CheckDigits(string country, string bban)
    {
        var rearranged = bban + country + "00";
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var chunk = char.IsLetter(ch) ? (ch - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : ch.ToString();
            foreach (var d in chunk)
            {
                remainder = ((remainder * 10) + (d - '0')) % 97;
            }
        }

        return (98 - remainder).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
