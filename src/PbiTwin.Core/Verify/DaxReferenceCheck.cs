using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Output;
using PbiTwin.Core.Rename;

namespace PbiTwin.Core.Verify;

/// <summary>
/// Checks that every <c>[Column]</c> / <c>[Measure]</c> reference in the twin's DAX points at an object that
/// exists. PbiTwin cannot run DAX, but a broken rename or a dangling reference shows up here.
/// </summary>
public static class DaxReferenceCheck
{
    public static Check Run(Model twin)
    {
        ArgumentNullException.ThrowIfNull(twin);

        // An identity map of the twin's own names: anything it cannot resolve does not exist.
        var identity = new NameMap();
        foreach (var t in twin.Tables)
        {
            identity.Tables[t.Name] = t.Name;
            identity.Columns[t.Name] = t.Columns.Where(c => c.Type != ColumnType.RowNumber)
                .ToDictionary(c => c.Name, c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var m in t.Measures)
            {
                identity.Measures[m.Name] = m.Name;
            }
        }

        var problems = new List<string>();
        var expressions = 0;
        DaxSites.ForEach(twin, IdentityNames.Instance, (dax, site) =>
        {
            expressions++;
            var unresolved = new List<string>();
            DaxRenamer.Rename(dax, identity, site.IsCalculatedTable ? null : site.HomeTable, anyTableFallback: true, unresolved);
            problems.AddRange(unresolved.Select(u => $"{site.Location}: [{u}]"));
            return dax;
        });

        return problems.Count == 0
            ? new Check("DAX references", CheckStatus.Pass, $"all column and measure references in {expressions} DAX expressions exist")
            : new Check("DAX references", CheckStatus.Warn, $"{problems.Count} references could not be resolved: {string.Join("; ", problems.Take(5))}");
    }
}
