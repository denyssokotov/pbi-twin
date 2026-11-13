namespace PbiTwin.SampleGenerator;

internal sealed class SampleData(ulong seed)
{
    private static readonly string[] Regions = ["North", "South", "East", "West"];
    private static readonly string[] Segments = ["Consumer", "SMB", "Enterprise"];
    private static readonly double[] SegmentWeights = [70, 22, 8];

    private static readonly (string Category, string Sub, decimal MinCost, decimal MaxCost, string[] Nouns)[] Subcategories =
    [
        ("Electronics", "Laptops", 380m, 1400m, ["Laptop", "Notebook", "Ultrabook"]),
        ("Electronics", "Phones", 140m, 900m, ["Phone", "Smartphone"]),
        ("Electronics", "Headphones", 18m, 260m, ["Headphones", "Earbuds", "Headset"]),
        ("Electronics", "Monitors", 110m, 700m, ["Monitor", "Display"]),
        ("Home", "Kitchen", 9m, 240m, ["Kettle", "Blender", "Pan Set", "Coffee Maker"]),
        ("Home", "Furniture", 45m, 650m, ["Chair", "Desk", "Shelf", "Sofa"]),
        ("Home", "Lighting", 7m, 120m, ["Lamp", "LED Strip", "Pendant Light"]),
        ("Sports", "Cycling", 25m, 1100m, ["Bike", "Helmet", "Bike Light"]),
        ("Sports", "Fitness", 6m, 480m, ["Dumbbells", "Yoga Mat", "Treadmill"]),
        ("Sports", "Outdoor", 12m, 320m, ["Tent", "Backpack", "Sleeping Bag"]),
        ("Clothing", "Jackets", 22m, 210m, ["Jacket", "Parka", "Windbreaker"]),
        ("Clothing", "Shoes", 18m, 160m, ["Sneakers", "Boots", "Running Shoes"]),
        ("Clothing", "Accessories", 4m, 70m, ["Cap", "Scarf", "Belt", "Gloves"]),
        ("Toys", "Board Games", 8m, 60m, ["Board Game", "Puzzle"]),
        ("Toys", "Building Sets", 10m, 140m, ["Building Set", "Robot Kit"]),
        ("Books", "Fiction", 3m, 18m, ["Novel", "Story Collection"]),
        ("Books", "Business", 5m, 28m, ["Handbook", "Guide"]),
    ];

    private static readonly string[] Brands =
        ["Northwind", "Fabrikam", "Litware", "Proseware", "Tailspin", "Wingtip", "Coho", "Lucerne", "Trey", "Margie", "Woodgrove", "Relecloud"];

    private static readonly string[] Adjectives = ["Pro", "Lite", "Max", "Air", "Classic", "Smart", "Ultra", "Eco", "Prime", "Go"];

    private static readonly string[] StoreSuffixes = ["Central", "Mall", "Outlet", "Station", "Riverside", "Plaza", "Park", "Market", "Old Town", "Airport"];

    private static readonly (string City, string Region)[] StoreCities =
    [
        ("Kyiv", "North"), ("Chernihiv", "North"), ("Zhytomyr", "North"), ("Sumy", "North"),
        ("Odesa", "South"), ("Mykolaiv", "South"), ("Kherson", "South"), ("Zaporizhzhia", "South"),
        ("Kharkiv", "East"), ("Dnipro", "East"), ("Poltava", "East"), ("Kramatorsk", "East"),
        ("Lviv", "West"), ("Ivano-Frankivsk", "West"), ("Ternopil", "West"), ("Uzhhorod", "West"),
    ];

    // Seasonality: strong Nov-Dec peak. Growth: ~+18% year over year.
    private static readonly double[] MonthWeights = [0.75, 0.70, 0.85, 0.90, 0.95, 0.90, 0.85, 0.90, 0.95, 1.05, 1.45, 1.75];
    private static readonly DateOnly FirstDay = new(2022, 1, 1);
    private static readonly DateOnly LastDay = new(2024, 12, 31);
    private const int TargetOrders = 80_000;

    // Anomalies that a twin should reproduce.
    private static readonly DateOnly[] BlackFridays = [new(2022, 11, 25), new(2023, 11, 24), new(2024, 11, 29)];
    private const int OutageStoreKey = 17;
    private static readonly DateOnly OutageStart = new(2023, 3, 1);
    private static readonly DateOnly OutageEnd = new(2023, 3, 21);
    private const int LaunchProductKey = 42;
    private static readonly DateOnly LaunchDate = new(2024, 6, 1);

    private readonly Rng _rng = new(seed);

    private sealed record Customer(int Key, string FullName, string Email, string Phone, string City, string Address, string Segment);

    private sealed record Product(int Key, decimal UnitCost, decimal ListPrice);

    private sealed record Employee(int Key, string FullName, string Region);

    private sealed record Store(int Key, string Region);

    public List<(string Table, int Rows)> Write(string dir)
    {
        var customers = WriteCustomers(Path.Combine(dir, "DimCustomer.csv"));
        var products = WriteProducts(Path.Combine(dir, "DimProduct.csv"));
        var employees = WriteEmployees(Path.Combine(dir, "DimEmployee.csv"));
        var stores = WriteStores(Path.Combine(dir, "DimStore.csv"));
        var sales = WriteSales(Path.Combine(dir, "FactSales.csv"), customers, products, employees, stores);

        return
        [
            ("DimCustomer", customers.Count),
            ("DimProduct", products.Count),
            ("DimEmployee", employees.Count),
            ("DimStore", stores.Count),
            ("FactSales", sales),
        ];
    }

    private List<Customer> WriteCustomers(string path)
    {
        using var csv = new CsvFile(path, "CustomerKey", "FullName", "Email", "Phone", "City", "Country", "Address", "IBAN", "Segment");
        var usedEmails = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Customer>();

        for (var key = 1; key <= 2000; key++)
        {
            var country = Country.All[_rng.PickWeighted(Country.CustomerWeights)];
            var first = _rng.Pick(country.FirstNames);
            var last = _rng.Pick(country.LastNames);
            var fullName = $"{first} {last}";

            var local = $"{first}.{last}".ToLowerInvariant();
            var email = $"{local}@{_rng.Pick(country.EmailDomains)}";
            for (var n = 2; !usedEmails.Add(email); n++)
            {
                email = $"{local}{n}@{_rng.Pick(country.EmailDomains)}";
            }

            var city = _rng.Pick(country.Cities);
            var phone = country.Phone(_rng);
            var address = country.Address(_rng);
            var iban = country.Iban(_rng);
            var segment = Segments[_rng.PickWeighted(SegmentWeights)];

            csv.Row(key, fullName, email, phone, city, country.Name, address, iban, segment);
            list.Add(new Customer(key, fullName, email, phone, city, address, segment));
        }

        return list;
    }

    private List<Product> WriteProducts(string path)
    {
        using var csv = new CsvFile(path, "ProductKey", "ProductName", "Category", "Subcategory", "Brand", "UnitCost");
        var list = new List<Product>();

        for (var key = 1; key <= 300; key++)
        {
            var sub = Subcategories[_rng.Next(0, Subcategories.Length)];
            var brand = _rng.Pick(Brands);
            var name = $"{brand} {_rng.Pick(Adjectives)} {_rng.Pick(sub.Nouns)} {(char)('A' + _rng.Next(0, 26))}{100 + key}";

            // Log-uniform cost inside the subcategory range: many cheap items, few expensive ones.
            var ratio = (double)(sub.MaxCost / sub.MinCost);
            var unitCost = Math.Round(sub.MinCost * (decimal)Math.Pow(ratio, _rng.NextDouble()), 2);
            var listPrice = Math.Round(unitCost * (decimal)(1.3 + (_rng.NextDouble() * 1.1)), 2);

            csv.Row(key, name, sub.Category, sub.Sub, brand, unitCost);
            list.Add(new Product(key, unitCost, listPrice));
        }

        return list;
    }

    private List<Employee> WriteEmployees(string path)
    {
        using var csv = new CsvFile(path, "EmployeeKey", "FullName", "Email", "Salary", "ManagerKey", "Region");
        var list = new List<Employee>();
        var ua = Country.All[0];

        for (var key = 1; key <= 80; key++)
        {
            var first = _rng.Pick(ua.FirstNames);
            var last = _rng.Pick(ua.LastNames);
            var fullName = $"{first} {last}";
            var email = $"{first[0]}.{last}{key}@contoso-pii.com".ToLowerInvariant();

            // 1 = CEO, 2..5 = regional managers, the rest report to the manager of their region.
            string region;
            int? manager;
            decimal salary;
            if (key == 1)
            {
                region = "North";
                manager = null;
                salary = 240_000m;
            }
            else if (key <= 5)
            {
                region = Regions[key - 2];
                manager = 1;
                salary = 120_000m + (_rng.Next(0, 31) * 1_000m);
            }
            else
            {
                var r = _rng.Next(0, Regions.Length);
                region = Regions[r];
                manager = r + 2;
                salary = 38_000m + (_rng.Next(0, 33) * 1_000m);
            }

            csv.Row(key, fullName, email, salary, manager, region);
            list.Add(new Employee(key, fullName, region));
        }

        return list;
    }

    private static List<Store> WriteStores(string path)
    {
        using var csv = new CsvFile(path, "StoreKey", "StoreName", "City", "Region");
        var list = new List<Store>();

        for (var key = 1; key <= 40; key++)
        {
            var (city, region) = StoreCities[(key - 1) % StoreCities.Length];
            var suffix = StoreSuffixes[(key - 1) / StoreCities.Length * 3 % StoreSuffixes.Length + (key % 3)];
            csv.Row(key, $"Contoso {city} {suffix}", city, region);
            list.Add(new Store(key, region));
        }

        return list;
    }

    private int WriteSales(string path, List<Customer> customers, List<Product> products, List<Employee> employees, List<Store> stores)
    {
        using var csv = new CsvFile(path,
            "OrderNumber", "OrderDate", "ShipDate", "CustomerKey", "ProductKey", "StoreKey", "EmployeeKey",
            "Quantity", "UnitPrice", "Discount", "SalesAmount", "CostAmount", "Comment");

        var days = new List<(DateOnly Day, double Weight)>();
        for (var d = FirstDay; d <= LastDay; d = d.AddDays(1))
        {
            var w = MonthWeights[d.Month - 1] * (1 + (0.18 * (d.Year - FirstDay.Year)));
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                w *= 1.25;
            }

            if (Array.IndexOf(BlackFridays, d) >= 0)
            {
                w *= 6;
            }

            days.Add((d, w));
        }

        var baseOrders = TargetOrders / days.Sum(x => x.Weight);
        var staffByRegion = Regions.ToDictionary(r => r, r => employees.Where(e => e.Region == r && e.Key > 5).ToList());
        var storeWeights = stores.Select(s => 0.5 + _rng.NextDouble()).ToArray();
        var enterprise = customers.Where(c => c.Segment == "Enterprise").ToList();
        var rows = 0;
        var orderSeq = new Dictionary<int, int>();
        var bulkOrdersLeft = 12;

        foreach (var (day, weight) in days)
        {
            var orders = (int)Math.Round(baseOrders * weight * (0.85 + (_rng.NextDouble() * 0.3)));
            for (var o = 0; o < orders; o++)
            {
                var store = stores[_rng.PickWeighted(storeWeights)];
                if (store.Key == OutageStoreKey && day >= OutageStart && day <= OutageEnd)
                {
                    continue;
                }

                var bulk = bulkOrdersLeft > 0 && _rng.Chance(0.00025);
                var customer = bulk ? _rng.Pick(enterprise) : customers[_rng.LongTail(customers.Count, 2.2)];
                if (bulk)
                {
                    bulkOrdersLeft--;
                }

                var employee = _rng.Pick(staffByRegion[store.Region]);
                var seq = orderSeq[day.Year] = orderSeq.GetValueOrDefault(day.Year) + 1;
                var orderNumber = $"SO-{day.Year}-{seq:000000}";
                var shipDate = day.AddDays(_rng.Next(1, 8));
                var lines = 1 + _rng.LongTail(4, 1.6) + (_rng.Chance(0.35) ? 1 : 0);
                var comment = _rng.Chance(0.08) ? Comment(customer, employee) : null;

                for (var l = 0; l < lines; l++)
                {
                    var product = products[_rng.LongTail(products.Count, 3)];
                    if (product.Key == LaunchProductKey && day < LaunchDate)
                    {
                        product = products[0];
                    }

                    var qty = bulk ? _rng.Next(200, 501) : 1 + _rng.LongTail(10, 2.5);
                    var price = Math.Round(product.ListPrice * (1 + (0.03m * (day.Year - FirstDay.Year))), 2);
                    var discount = customer.Segment == "Enterprise" ? 0.10m : _rng.Pick([0m, 0m, 0m, 0.05m, 0.10m, 0.20m]);
                    if (bulk)
                    {
                        discount = 0.25m;
                    }

                    var sales = Math.Round(qty * price * (1 - discount), 2);
                    var cost = Math.Round(qty * product.UnitCost, 2);

                    csv.Row(orderNumber, day, shipDate, customer.Key, product.Key, store.Key, employee.Key,
                        qty, price, discount, sales, cost, l == 0 ? comment : null);
                    rows++;
                }
            }
        }

        return rows;
    }

    private string Comment(Customer c, Employee e) => _rng.Next(0, 7) switch
    {
        0 => $"Customer {c.FullName} asked to deliver after 6pm",
        1 => $"Call {c.FullName} at {c.Phone} before shipping",
        2 => $"Gift wrap requested by {c.FullName}",
        3 => $"Discount approved by {e.FullName}",
        4 => $"Deliver to {c.Address}, {c.City}",
        5 => $"Repeat order, confirm with {c.Email}",
        _ => "Standard delivery",
    };
}
