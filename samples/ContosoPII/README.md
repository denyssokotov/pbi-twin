# ContosoPII sample

A small Power BI project full of deliberately "sensitive" looking — but entirely fictional — data.
It is the test bed for PbiTwin: every tricky case the anonymizer must handle is in here.

## Get the data

The CSV files are not committed. Generate them (deterministic, ~15 MB, takes a couple of seconds):

```
dotnet run --project samples/generator
```

Files land in `samples/ContosoPII/data/`. Their SHA-256 hashes are in `data.sha256`, and CI checks that
every platform produces identical bytes.

## Open in Power BI Desktop

1. Open `ContosoPII.pbip`.
2. *Transform data → Edit parameters*: set `DataFolder` to the absolute path of `samples\ContosoPII\data`.
3. *Refresh*.

## What is inside

| Table | Rows | Sensitive-looking content |
|---|---|---|
| DimCustomer | 2,000 | Full names, emails, phones, addresses, IBANs (valid checksums), cities, countries |
| DimProduct | 300 | Product names, brands, unit cost |
| DimEmployee | 80 | Names, emails, salaries, `ManagerKey` self-reference (no relationship) |
| DimStore | 40 | Store names, cities, regions |
| FactSales | ~196,000 | Order numbers `SO-2024-000123`, amounts, free-text comments with names, phones, emails and addresses |
| DimDate | calculated | DAX calendar spanning the fact dates |

Patterns a twin must preserve: Nov–Dec seasonality, ~+20% year-over-year growth, Black Friday spikes,
a three-week outage of store 17 in March 2023, a dozen bulk enterprise orders, a long tail of products,
and a product launched in June 2024.

Model features the anonymizer must handle:

- Measures incl. `Revenue YoY %` and hard-coded literals (`DimCustomer[Segment] = "Enterprise"`, `DimStore[City] = "Kyiv"`)
- Calculated columns (`FactSales[Line Margin]`, `DimEmployee[ManagerPath]` using `PATH`)
- Calculated table (`DimDate`) and a calculation group (`Time Intelligence`)
- RLS role `Region Manager` filtering `DimStore[Region] = "West"`
- An unused `LegacyServer` parameter pointing at a "production" SQL server (must not leak)
- Report (PBIR): card, bar chart with a visual filter `Country = "Ukraine"`, line chart, table, and a textbox naming a person
