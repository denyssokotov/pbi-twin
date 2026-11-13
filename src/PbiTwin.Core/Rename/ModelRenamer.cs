using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Output;

namespace PbiTwin.Core.Rename;

/// <summary>
/// Renames tables, columns, measures, hierarchies, levels and roles in a TOM model and rewrites every DAX
/// expression to match. Translations and display folders, which repeat the original names, are removed.
/// </summary>
public static class ModelRenamer
{
    public static void Apply(Model model, NameMap map)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(map);

        // 1. DAX first, while the model still has its original names.
        DaxSites.ForEach(model, map, (dax, site) =>
            DaxRenamer.Rename(dax, map,
                // A calculated table's own columns do not exist inside its expression.
                site.IsCalculatedTable ? null : site.HomeTable,
                anyTableFallback: !site.IsCalculatedTable));

        // 2. Then the objects themselves (TOM keeps relationships, sort-by columns, levels and permissions linked).
        foreach (var table in model.Tables.ToList())
        {
            var original = table.Name;
            foreach (var c in table.Columns.Where(c => c.Type != ColumnType.RowNumber).ToList())
            {
                c.Name = map.Column(original, c.Name);
                c.DisplayFolder = null;
                if (c is CalculatedTableColumn ctc)
                {
                    // Otherwise Power BI would name the column after the expression output again.
                    ctc.IsNameInferred = false;
                }
            }

            foreach (var h in table.Hierarchies.ToList())
            {
                var levels = map.Levels.GetValueOrDefault(original)?.GetValueOrDefault(h.Name);
                foreach (var l in h.Levels.ToList())
                {
                    l.Name = levels?.GetValueOrDefault(l.Name) ?? l.Name;
                }

                h.Name = map.Hierarchies.GetValueOrDefault(original)?.GetValueOrDefault(h.Name) ?? h.Name;
                h.DisplayFolder = null;
            }

            foreach (var m in table.Measures.ToList())
            {
                m.Name = map.Measure(m.Name);
                m.DisplayFolder = null;
            }

            table.Name = map.Table(original);
            foreach (var p in table.Partitions)
            {
                p.Name = table.Partitions.Count == 1 ? table.Name : p.Name;
            }
        }

        foreach (var role in model.Roles)
        {
            role.Name = map.Roles.GetValueOrDefault(role.Name) ?? role.Name;
        }

        // Translations, perspectives and linguistic schemas repeat the original names.
        model.Cultures.Clear();
        model.Perspectives.Clear();
    }
}
