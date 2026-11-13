# How PbiTwin works

In plain words, step by step.

## 1. Read the model

PbiTwin loads the model definition from the `.pbip` project (TMDL or `model.bim`) with Microsoft's own
Tabular Object Model. It reads the data from the running Power BI Desktop with DAX queries
(`EVALUATE SELECTCOLUMNS('Table', ...)`), one table at a time, row by row, so large tables never need to fit in memory.

Calculated tables, calculation groups and auto date tables are not extracted: Power BI recalculates them
from the twin's data. Calculated columns are recalculated too.

## 2. Domains: one mapping per family of columns

`FactSales[CustomerKey]` and `DimCustomer[CustomerKey]` must get the *same* pseudonym, or the relationship
breaks. PbiTwin groups columns that are joined by relationships with a union-find structure; every group
(a *domain*) shares one mapping. Columns of the same kind share a domain too: all city columns, all
`Region` columns, all person names. A key-like column without a relationship whose values are a subset of
the table's own key (e.g. `ManagerKey` → `EmployeeKey`) joins that key's domain, so `PATH()` keeps working.

## 3. Classify columns

Each column gets a class from simple, readable rules: data type, the column name, the data category set in
Power BI (City, Country, Address…) and patterns in a sample of values (emails, IBANs, phones, person-like
names, ID-like codes, text length, number of distinct values). Each class has a default rule; you can change any of them.

## 4. Keyed hashing

Every pseudonym and every random number comes from **HMAC-SHA256(seed, domain + value)**. The same seed
gives the same twin; without the seed, a pseudonym cannot be traced back to its original. The seed is
random for every run, shown in the UI, and never saved.

## 5. Transforms

- **One-to-one replacements** (keys, categories, cities, countries): all distinct values of the domain are
  collected first and sorted by their keyed hash, then numbered. The order of the original values (e.g.
  customer 1 was the first customer) is not revealed, and no two originals ever share a pseudonym.
- **Names, emails, phones, IBANs, addresses, free text**: generated from the keyed hash, so the same input
  always gets the same fake value. Phones and IBANs keep their shape (digits stay digits).
- **Money**: every amount column is multiplied by one secret factor (between 0.37 and 2.7 by default) and
  by `1 ± noise` per row (3%). Because the factor is shared, every ratio between amounts — margin %, shares,
  growth — stays the same; the noise averages out in totals. Zero stays zero, signs never change.
- **Quantities**: their own factor and noise, rounded to whole numbers, never turning into zero.
- **Dates**: all dates move by the same offset: a multiple of 364 days (exactly 52 weeks, so weekdays are
  kept) plus or minus a few whole weeks. Seasonality and day-of-week patterns survive, and the date table
  still lines up with the facts.
- Blanks stay blank.

## 6. Write the twin

Data goes to UTF-8 CSV files (invariant culture, ISO dates). The project is copied, and the model is
rewritten through TOM: every table reads its CSV through a `DataFolder` parameter; every original query,
parameter, data source and query group is deleted. String literals in DAX and in the report that equal
an original value are replaced by that value's pseudonym — using the column next to the literal to pick
the right mapping — and original values inside free text (titles, textboxes) are replaced word by word.

## 7. Rename (optional)

With renaming on, every table, column, measure, hierarchy, level and role gets a neutral name, decided once
and stored in the codebook. DAX is rewritten with a small lexer that knows strings, comments, `'quoted tables'`
and `[brackets]` (with `]]` and `''` escapes): `Table[Column]` and `'Table'[Measure]` are mapped by their table;
an unqualified `[Name]` is a measure, a column of the table the expression lives in, or the only column with
that name — except names the expression creates itself (a `"Year"` in `ADDCOLUMNS`, CALENDAR's `[Date]`).
Report references (`Entity`, `Property`, `queryRef`, filter aliases) follow. Translations, perspectives,
display folders and the diagram layout, which repeat original names, are removed. The same lexer and the
reversed codebook power *Translate DAX*.

## 8. Verify

The twin is read back from disk and checked:

- row counts match the original (or the row limit);
- every foreign key still finds its key, and one-side keys are still unique;
- **leak scan**: every distinct original text value with 4+ characters is searched for, case-insensitively
  and as a whole word, in every file of the twin; the report lists file and line, never the value;
- every column and measure reference in every DAX expression of the twin points at an object that exists;
- monthly totals of the largest amount column correlate with the original (≈ 1.0), and the ratio of the two
  largest amount columns (what drives margin %) is unchanged within the noise.

The result is written to `TRUST-REPORT.md` next to the twin, without original values, the seed or the factors.
