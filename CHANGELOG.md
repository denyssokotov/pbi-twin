# Changelog

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
