# End-to-end checklist (manual, on Windows)

Needs: Windows, Power BI Desktop (recent), .NET 8 SDK.

## 0. Prepare

- [ ] `git clone` the repository, then in its folder: `dotnet run --project samples/generator`
      (creates `samples/ContosoPII/data/*.csv`, ~15 MB).
- [ ] `dotnet test` — all green.

## 1. Open the sample in Power BI Desktop

- [ ] Open `samples/ContosoPII/ContosoPII.pbip`.
- [ ] *Transform data → Edit parameters*: set `DataFolder` to the absolute path of `samples\ContosoPII\data`.
- [ ] *Refresh*. All 5 tables load; the page shows a card, bar chart, line chart, table and textbox.
- [ ] Note for later: Revenue total, `Margin %`, the shape of the monthly line, `Top Segment Revenue`.
- [ ] *Modeling → View as → Region Manager*: the table shows only West stores.
- [ ] If Desktop reports a problem opening the report (PBIR) — tell us; the page can be rebuilt by hand.

## 2. Create the twin

- [ ] `dotnet run --project src/PbiTwin.App` (or run the built `PbiTwin.exe`).
- [ ] **Source**: the running ContosoPII model is listed; choose `ContosoPII.pbip`; status shows *Matched ✓*.
- [ ] **Scan**: FullName → PersonName, Email → Email, IBAN → Iban, City → City (🔗 with DimStore[City]),
      ManagerKey 🔗 EmployeeKey, Discount → Keep, Comment → FreeText. Examples are masked.
- [ ] **Options**: keep defaults; note the seed.
- [ ] **Preview**: before/after look right for DimCustomer and FactSales.
- [ ] **Run** → **Done**: all checks green, *0 leaks found*.

## 3. Open the twin

- [ ] Close the original report. Click *Open Twin in Power BI Desktop*, then *Refresh*.
- [ ] All visuals render. Revenue differs from the original (scaled), but `Margin %` is the same to ~0.1 pp.
- [ ] The monthly line has the same shape (Nov–Dec peaks, Black Friday spikes, growth).
- [ ] `Top Segment Revenue` and `Capital Stores Revenue` return values (not blank) — literals were rewritten.
- [ ] *View as → Region Manager* shows only one region's stores.
- [ ] `DimEmployee[ManagerPath]` has no errors.
- [ ] The bar chart filter shows an invented country, the textbox an invented name.

## 4. Trust report

- [ ] `TRUST-REPORT.md` exists next to the twin, shows all checks passed and 0 leaks, and contains no real names, emails or cities.

## 5. Search the twin yourself

In the twin folder (PowerShell):

```powershell
Get-ChildItem -Recurse -File | Select-String -Pattern "Kovalenko","ukr.net","Kyiv","Ukraine","sql-prod" -SimpleMatch
```

- [ ] Nothing found. (Pick a few more names/emails from `DimCustomer.csv` of the original.)

## 6. (Stage 5, not built yet) Translate DAX

- [ ] Ask an AI to write a measure using the twin's names → *Translate DAX* → paste into the original → it works.

## 7. Offline proof (optional)

- [ ] Block PbiTwin in Windows Firewall (see [TRUST.md](TRUST.md)) or turn off the network, repeat step 2 — same result.
