# Changelog

## 0.1.10

- Fixed: when a CSV cell did not parse as its column's type, the error message quoted the cell (the .NET message contains the value), so a data value could end up on screen, in a screenshot or in a pasted error. The error now names the file, line and column and says the value is not shown.
- Column names in Ukrainian, Russian, Polish, German, French and Spanish (surname, phone, address, city, country, …) are recognised, so those columns get realistic fake names, cities and phones. Whole-number columns that identify someone (phone, SSN, passport) are mapped one-to-one instead of scaled; look-alikes such as `iPhonesSold` are not.
- The "output must not be inside the source project" check now follows symbolic links and junctions, so a link into the project cannot hide it.
- A run that fails or is cancelled halfway now removes the half-written twin (and the codebook), so it cannot be mistaken for a finished one.
- A test proves that the trust report itself holds no original value (it is excluded from the leak scan, so it needed its own check).
- Random-model tests now cover booleans, doubles, whole numbers with blanks, dates with and without a time, text keys, report visuals (a filter and a textbox that mention values), and names with emoji, Hebrew, combining accents and non-breaking spaces. Blanks and true/false values are checked to stay as they were.

## 0.1.9

- The command line rejects unknown options (a typo such as `--rennme` used to be ignored and silently changed what the run did), bad numbers (`--rows 0`, `--noise -5`, `--port abc`), `--rename` values that do not exist, `--seed` without a value, and `--port` together with `--csv`. The output folder and twin name are checked before any data is read.
- CI checks these refusals on every push.
- Dependabot proposes monthly updates for actions and packages (nothing merges by itself); `CONTRIBUTING.md` states the non-negotiable rules.

## 0.1.8

- Fixed: a column name or output path containing `#(` (or a line break or tab in a name) was written to the Power Query text unescaped, where `#(` starts an escape sequence such as `#(lf)`. These are now escaped.
- Fixed a leak found by the random-model test: a DAX filter such as `'Dim'[Name] = "Kyiv"` kept the original word when `Kyiv` was a value of a different column. If the filtered column is rewritten, the literal is now replaced too (the filter stays as empty as before).
- Links in the model or report (button actions, hyperlinks, `mailto:`) are reported by host name, because an internal address can name the company even when no data value does. Standard Microsoft schema hosts are ignored.
- Text columns kept as they are, but whose sampled values are all different, are reported as possible identifiers.
- Random models with awkward names and values (quotes, commas, line breaks, `#(`, Cyrillic) go through the whole pipeline in both rename modes (with measures, a calculated column and a row-level-security filter that mention original values); every check must pass.

## 0.1.7

- Every picture in the report (logos, backgrounds) is named in the trust report, because its content cannot be scanned and a logo can identify a company.
- New check "Twin opens": the finished twin is loaded again from disk and its tables and relationships are counted.
- Random-input (fuzz) tests for the DAX tokenizer, CSV reading/writing and the text matcher; they found nothing, and stay as a guard.
- Measured: 1,000,000 rows in about 9 s and 150 MB.

## 0.1.6

- A broken or hand-edited rules file (invalid JSON, empty entries, a column mapped to itself) gives a clear message instead of a crash.
- Saving or loading rules in a folder you cannot write to reports the problem instead of closing the app.

## 0.1.5

Security
- The output folder may not be, contain or sit inside the source project; checked on the Options step.
- Caches (`.abf`, `.pbix`, `.bak`) and symbolic links are not copied; files the leak scan cannot read are reported.
- A short user-chosen seed is reported as a weakness (only its length is mentioned).
- Columns that look personal but were kept as they are are listed in the trust report.
- GitHub Actions are pinned to commit SHAs and run with minimal permissions.

Quality
- Cyrillic models, empty tables, blank-only columns, quotes and line breaks in CSV, and broken input files are covered by end-to-end tests.
- The command line has `scan` (show the rules without creating anything) and `--version`, and reports every failure as `error:`.
- Warnings on screen are capped at 12 (all stay in the trust report); running out of memory explains what to do.

## 0.1.4

- Inline images (button backgrounds, logos stored as `data:` URIs) are no longer touched; text inside them was being rewritten and the pictures broke.
- A filter on a column that PbiTwin does not change keeps its literal, even if another column holds the same word.
- Ambiguity warnings name the filter's column.

## 0.1.3

- No warning for ambiguous words in display text (titles, textboxes); only for filters and DAX.

## 0.1.2

- The DAX reference check no longer reports a column that exists in several tables as missing.

## 0.1.1

- Fixed creating a twin from a live Power BI Desktop model: `AdomdCommand.ExecuteScalar` is not supported, so row counts are read with a reader.
- Errors are shown with their cause instead of being cleared when the wizard steps back.
- Leak scan: no false positives from `true`/`false`/`null`, JSON property names, pieces of table and column names, GUIDs, base64 images, numbers or dates that contain a numeric original, and pseudonyms that contain an original value.
- The trust report summarizes leaks by column and by file and shows the surrounding text with the value cut out.

## 0.1.0

First release: Windows wizard and command line; classification, anonymization with keys, names, amounts, dates, DAX and report literals, renaming with a codebook and DAX translator, saved rules, trust report.
