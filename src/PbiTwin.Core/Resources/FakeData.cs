namespace PbiTwin.Core.Resources;

/// <summary>
/// Built-in word lists for pseudonyms. Deliberately whimsical so a fake value is easy to recognise as fake.
/// </summary>
internal static class FakeData
{
    public static readonly string[] FirstNames =
    [
        "Arlen", "Brisa", "Corvin", "Delphine", "Emrys", "Faye", "Galen", "Hollis", "Ione", "Jory",
        "Kestrel", "Liora", "Maren", "Nimue", "Orin", "Perrin", "Quinta", "Rhea", "Soren", "Tamsin",
        "Ulla", "Vale", "Wren", "Xanthe", "Yara", "Zev", "Alder", "Bryony", "Caspian", "Darcy",
        "Elowen", "Fennick", "Greer", "Hale", "Isolde", "Juno", "Kit", "Linnea", "Merritt", "Nell",
        "Oriel", "Pippa", "Rowe", "Sable", "Thane", "Una", "Vesper", "Wynn", "Ysolde", "Zinnia",
    ];

    public static readonly string[] LastNames =
    [
        "Ashdown", "Brackwater", "Coldbrook", "Dunmore", "Elderwood", "Fernhill", "Greyfield", "Hawthorne",
        "Ironside", "Juniperlane", "Kettleby", "Larkspur", "Mossgrove", "Northcott", "Oakhurst", "Pennywhistle",
        "Quarrington", "Ravensworth", "Stonebridge", "Thistlewood", "Underhill", "Vexley", "Whitlock", "Yarrowby",
        "Zennor", "Applegarth", "Bramblecote", "Copperfield", "Driftwood", "Emberly", "Foxglove", "Goldthwaite",
        "Hollowell", "Inglenook", "Jessamine", "Kingsmere", "Lanternfield", "Marchbank", "Nettlefold", "Oddingley",
    ];

    public static readonly string[] CityPrefixes =
        ["Ash", "Bel", "Cor", "Dun", "Elm", "Fen", "Glen", "Har", "Ivy", "Kel", "Lin", "Mar", "Nor", "Oak", "Pen", "Ros", "Sil", "Tor", "Val", "Wil"];

    public static readonly string[] CitySuffixes =
        ["ford", "haven", "mont", "stead", "wick", "bury", "dale", "port", "field", "moor", "gate", "holm", "ridge", "mere"];

    public static readonly string[] Countries =
    [
        "Avaloria", "Brenmark", "Caldonia", "Dravenia", "Eskaria", "Fennmark", "Galoria", "Hestria", "Istovia", "Jorvania",
        "Kelvora", "Lumeria", "Marovia", "Norvania", "Ostravel", "Pellaria", "Quorvia", "Rostania", "Selvaria", "Tervania",
        "Ulmaria", "Valdoria", "Westmarch", "Xandria", "Yllaria", "Zemaria",
    ];

    public static readonly string[] StreetNames =
        ["Maple", "Cedar", "Willow", "Birch", "Aspen", "Juniper", "Hazel", "Linden", "Rowan", "Alder", "Sorrel", "Bracken"];

    public static readonly string[] StreetTypes = ["Lane", "Road", "Street", "Way", "Avenue", "Court", "Row", "Close"];

    public static readonly string[] EmailDomains = ["example.com", "example.org", "example.net"];

    public static readonly string[] Lorem =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do",
        "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim",
        "ad", "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris", "nisi", "aliquip",
        "ex", "ea", "commodo", "consequat", "duis", "aute", "irure", "in", "reprehenderit", "voluptate",
        "velit", "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint", "occaecat", "cupidatat",
        "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt", "mollit", "anim", "id", "est", "laborum",
    ];
}
