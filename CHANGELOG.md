# Changelog

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
