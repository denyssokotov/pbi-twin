using PbiTwin.Core.Anonymize;
using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;
using PbiTwin.Core.Verify;

namespace PbiTwin.Core.Tests.Output;

public class RewriterTests
{
    private static readonly ColumnInfo StoreRegion = new("DimStore", "Region", ColumnType.Text, "Region", false, false, null, false);
    private static readonly ColumnInfo Segment = new("DimCustomer", "Segment", ColumnType.Text, "Segment", false, false, null, false);
    private static readonly ColumnInfo City = new("DimStore", "City", ColumnType.Text, "City", false, false, null, false);

    private static Dictionary<string, Dictionary<Domain, string>> Known()
    {
        var region = new Domain("label:REGION", Rule.Label) { Columns = { StoreRegion } };
        var segment = new Domain("label:SEGMENT", Rule.Label) { Columns = { Segment } };
        var city = new Domain("class:City", Rule.City) { Columns = { City } };
        return new()
        {
            ["West"] = new() { [region] = "Region 04" },
            ["Enterprise"] = new() { [segment] = "Segment 01" },
            ["Kyiv"] = new() { [city] = "Kelhaven" },
            ["Other"] = new() { [region] = "Region 09", [segment] = "Segment 07" },
        };
    }

    [Fact]
    public void Rewrites_string_literals_in_dax_but_not_names_or_comments()
    {
        var lit = new LiteralRewriter(Known());
        var dax = "CALCULATE([Revenue], 'DimStore'[Region] = \"West\") // \"West\" in a comment\n + [West]";
        var result = lit.RewriteDax(dax, "m");

        Assert.Equal("CALCULATE([Revenue], 'DimStore'[Region] = \"Region 04\") // \"West\" in a comment\n + [West]", result);
        Assert.Equal(1, lit.Replacements["m"]);
    }

    [Fact]
    public void Uses_the_column_before_the_literal_to_resolve_ambiguity()
    {
        var lit = new LiteralRewriter(Known());

        Assert.Contains("\"Segment 07\"", lit.RewriteDax("FILTER(T, DimCustomer[Segment] = \"Other\")", "a"), StringComparison.Ordinal);
        Assert.Contains("\"Region 09\"", lit.RewriteDax("FILTER(T, DimStore[Region] IN { \"West\", \"Other\" })", "b"), StringComparison.Ordinal);
        Assert.Empty(lit.Warnings);
    }

    [Fact]
    public void Replaces_values_inside_free_text()
    {
        var lit = new LiteralRewriter(Known());
        Assert.Equal("Stores in Kelhaven and Region 04", lit.RewriteText("Stores in Kyiv and West", "t"));
        Assert.Equal("Westfield stays", lit.RewriteText("Westfield stays", "t"));
    }

    [Fact]
    public void Warns_about_hard_coded_dates_only()
    {
        var lit = new LiteralRewriter(Known());
        lit.RewriteDax("CALENDAR(DATE(YEAR(MIN(F[D])), 1, 1), TODAY())", "a");
        Assert.Empty(lit.Warnings);
        lit.RewriteDax("CALCULATE([R], F[D] >= DATE(2024, 1, 1))", "b");
        Assert.Single(lit.Warnings);
    }

    [Fact]
    public void Rewrites_pbir_filters_titles_textboxes_and_dates()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var visual = Path.Combine(dir, "definition", "pages", "p", "visuals", "v");
            Directory.CreateDirectory(visual);
            File.WriteAllText(Path.Combine(visual, "visual.json"), """
                {
                  "visual": {
                    "objects": { "general": [ { "properties": { "paragraphs": [ { "textRuns": [ { "value": "Report for Kyiv" } ] } ] } } ] },
                    "visualContainerObjects": { "title": [ { "properties": { "text": { "expr": { "Literal": { "Value": "'West stores'" } } } } } ] }
                  },
                  "filterConfig": { "filters": [
                    { "filter": { "Where": [ { "Condition": { "In": {
                        "Expressions": [ { "Column": { "Expression": { "SourceRef": { "Source": "d" } }, "Property": "Region" } } ],
                        "Values": [ [ { "Literal": { "Value": "'West'" } } ], [ { "Literal": { "Value": "'Unknown'" } } ] ] } } } ] } },
                    { "filter": { "Where": [ { "Condition": { "Comparison": { "ComparisonKind": 2,
                        "Left": { "Column": { "Property": "OrderDate" } },
                        "Right": { "Literal": { "Value": "datetime'2024-01-01T00:00:00'" } } } } } ] } }
                  ] }
                }
                """);

            var lit = new LiteralRewriter(Known());
            Assert.True(ReportRewriter.Rewrite(dir, lit, 364));
            var json = File.ReadAllText(Path.Combine(visual, "visual.json"));

            Assert.Contains("Report for Kelhaven", json, StringComparison.Ordinal);
            Assert.Contains("'Region 04 stores'", json, StringComparison.Ordinal);
            Assert.Contains("'Region 04'", json, StringComparison.Ordinal);
            Assert.Contains("'Unknown'", json, StringComparison.Ordinal);
            Assert.Contains("datetime'2024-12-30T00:00:00'", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Kyiv", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Leak_scan_reports_location_but_not_value()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.csv"), "id,city\n1,Kelhaven\n2,Kyiv\n");
            File.WriteAllText(Path.Combine(dir, "b.tmdl"), "measure x = \"Region 04\"\n");

            var (findings, values, files) = LeakScanner.Scan(dir, Known());

            var f = Assert.Single(findings);
            Assert.Equal("a.csv", f.File);
            Assert.Equal(3, f.Line);
            Assert.Equal("DimStore[City]", f.Columns);
            Assert.DoesNotContain("Kyiv", f.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, files);
            Assert.Equal(4, values);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Leak_context_shows_the_structure_with_the_value_cut_out()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "visual.json"), "{ \"Value\": \"'Kyiv'\", \"Count\": 1 }\n");
            File.WriteAllText(Path.Combine(dir, "data.csv"), "id,city\n1,Kyiv\n");

            var findings = LeakScanner.Scan(dir, Known()).Findings;

            var json = Assert.Single(findings, f => f.File == "visual.json");
            Assert.Equal("{ \"Value\": \"'‹value›'\", \"Count\": 1 }", json.Context);
            Assert.Null(Assert.Single(findings, f => f.File == "data.csv").Context);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Leak_scan_ignores_json_keys_and_pieces_of_table_and_column_names()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "visual.json"), "{\n  \"Kyiv\": true\n}\n");
            File.WriteAllText(Path.Combine(dir, "t.tmdl"), "column 'Kyiv ID'\n    sourceColumn: Kyiv ID\n");
            File.WriteAllText(Path.Combine(dir, "m.tmdl"), "measure x = \"Kyiv\"\n");

            var (findings, _, _) = LeakScanner.Scan(dir, Known(), ownNames: ["Kyiv ID"]);

            // Only the DAX literal is a value.
            Assert.Equal("m.tmdl", Assert.Single(findings, f => f.Columns == "DimStore[City]").File);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ambiguous_values_warn_in_filters_and_dax_but_not_in_display_text()
    {
        var lit = new LiteralRewriter(Known());

        lit.RewriteText("Other regions overview", "title");
        Assert.Empty(lit.Warnings);

        lit.MapLiteral("Other", null, null, "filter");
        Assert.Single(lit.Warnings);
    }

    [Fact]
    public void Inline_images_and_filters_on_unchanged_columns_are_left_alone()
    {
        var lit = new LiteralRewriter(Known());
        var image = "data:image/png;base64,iVBORw0KGgo+Kyiv/Other+Kyiv=";

        Assert.Equal(image, lit.MapLiteral(image, null, null, "visual.json"));
        Assert.Equal(image, lit.RewriteDax($"\"{image}\"", "m").Trim('"'));

        // "Kyiv" is a value of DimStore[City]; a filter on an unrelated column keeps it as is.
        Assert.Equal("Kyiv", lit.MapLiteral("Kyiv", null, "Notes", "visual.json"));
        Assert.NotEqual("Kyiv", lit.MapLiteral("Kyiv", null, "City", "visual.json"));
        Assert.Empty(lit.Warnings);
    }

    [Fact]
    public void Leak_scan_ignores_base64_images()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "visual.json"), "{ \"url\": \"'data:image/png;base64,AAAA+Kyiv/BBBB'\" }\n");

            Assert.Empty(LeakScanner.Scan(dir, Known()).Findings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Warnings_shown_on_screen_are_capped_and_counted()
    {
        var warnings = Enumerable.Range(1, 30).Select(i => $"w{i}").ToList();

        var shown = PbiTwin.Core.Verify.TrustReport.Condense(warnings, 12);

        Assert.Equal(13, shown.Count);
        Assert.Contains("18 more", shown[^1], StringComparison.Ordinal);
        Assert.Equal(5, PbiTwin.Core.Verify.TrustReport.Condense(warnings.Take(5).ToList(), 12).Count);
    }

    [Fact]
    public void Leak_scan_ignores_syntax_literals_such_as_true_and_false()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "theme.json"), "{ \"bold\": true, \"italic\": false, \"x\": null }\n");
            var domain = new Domain("flag", Rule.Label);
            domain.Columns.Add(new ColumnInfo("Goals", "IsMD", ColumnType.Text, "IsMD", false, false, null, false));
            var known = new Dictionary<string, Dictionary<Domain, string>>
            {
                ["True"] = new() { [domain] = "Flag 01" },
                ["False"] = new() { [domain] = "Flag 02" },
            };

            var (findings, _, _) = LeakScanner.Scan(dir, known);

            Assert.Empty(findings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Leak_scan_ignores_pseudonyms_and_guids_that_merely_contain_an_original_value()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // 48213 was an employee ID; ID-48213 is somebody else's pseudonym. 4234 is a segment of a GUID.
            File.WriteAllText(Path.Combine(dir, "a.csv"), "ID-48213,12\nID-99001,7.4234\n2024-04-23,3.48213\n");
            File.WriteAllText(Path.Combine(dir, "b.tmdl"), "table LocalDateTable_3f66fe14-95ad-4234-a00a-67aad7749326\n");
            var domain = new Domain("emp", Rule.Key);
            domain.Columns.Add(new ColumnInfo("Emp", "EmployeeId", ColumnType.Text, "EmployeeId", true, false, null, false));
            var known = new Dictionary<string, Dictionary<Domain, string>>
            {
                ["48213"] = new() { [domain] = "ID-77777" },
                ["4234"] = new() { [domain] = "ID-48213" },
                ["99001"] = new() { [domain] = "ID-11111" },
                ["55555"] = new() { [domain] = "ID-99001" },
            };

            var (findings, _, _) = LeakScanner.Scan(dir, known);

            Assert.Empty(findings);

            // A real leak in a cell of its own is still reported.
            File.AppendAllText(Path.Combine(dir, "a.csv"), "99001,5\n");
            Assert.Single(LeakScanner.Scan(dir, known).Findings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Csv_partition_query_reads_from_data_folder()
    {
        var table = new TableInfo("Dim Store", [StoreRegion with { Table = "Dim Store" }, new("Dim Store", "Opened", ColumnType.DateTime, "Opened", false, false, null, true)], false);
        var m = ModelRewriter.CsvQuery(table);

        Assert.Contains("File.Contents(DataFolder & \"\\Dim Store.csv\")", m, StringComparison.Ordinal);
        Assert.Contains("{\"Region\", type text}", m, StringComparison.Ordinal);
        Assert.Contains("{\"Opened\", type date}", m, StringComparison.Ordinal);
        Assert.Contains("\"en-US\"", m, StringComparison.Ordinal);
    }
}

public class MetadataTests
{
    [Fact]
    public void Union_find_groups_related_columns()
    {
        var uf = new UnionFind();
        uf.Union("Fact[CustomerKey]", "DimCustomer[CustomerKey]");
        uf.Union("Returns[CustomerKey]", "DimCustomer[CustomerKey]");
        uf.Union("Fact[ProductKey]", "DimProduct[ProductKey]");
        uf.Find("Lonely[Key]");

        var groups = uf.Groups();
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups.Single(g => g.Contains("Returns[CustomerKey]")).Count);
        Assert.Equal(uf.Find("Fact[CustomerKey]"), uf.Find("Returns[CustomerKey]"));
        Assert.NotEqual(uf.Find("Fact[CustomerKey]"), uf.Find("Fact[ProductKey]"));
    }

    [Theory]
    [InlineData(new byte[] { 0x35, 0x00, 0x32, 0x00, 0x31, 0x00, 0x32, 0x00, 0x33, 0x00 }, 52123)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x35, 0x00, 0x32, 0x00, 0x31, 0x00 }, 521)]
    [InlineData(new byte[] { 0x36, 0x34, 0x30, 0x30, 0x30, 0x0D, 0x0A }, 64000)]
    public void Reads_port_files(byte[] bytes, int expected)
    {
        Assert.True(PbiTwin.Core.Connection.DesktopInstances.TryReadPort(bytes, out var port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void Finds_instances_in_workspace_folders()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var data = Path.Combine(root, "AnalysisServicesWorkspace_abc", "Data");
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, "msmdsrv.port.txt"), System.Text.Encoding.Unicode.GetBytes("54321"));
            Directory.CreateDirectory(Path.Combine(root, "stale_workspace", "Data"));

            var found = Assert.Single(PbiTwin.Core.Connection.DesktopInstances.Find([root]));
            Assert.Equal(54321, found.Port);
            Assert.Equal("Data Source=localhost:54321", found.ConnectionString);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
