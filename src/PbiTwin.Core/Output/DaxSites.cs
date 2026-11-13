using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Rename;

namespace PbiTwin.Core.Output;

/// <summary>Where a DAX expression lives. <see cref="HomeTable"/> uses the original name.</summary>
public sealed record DaxSite(string Location, string HomeTable, bool IsCalculatedTable);

/// <summary>Visits every DAX expression of a model: measures (incl. format strings, detail rows, KPIs),
/// calculated columns, calculated tables, calculation items and RLS filters.</summary>
public static class DaxSites
{
    public static void ForEach(Model model, INames names, Func<string, DaxSite, string> rewrite)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(rewrite);

        string Apply(string? dax, DaxSite site) => string.IsNullOrEmpty(dax) ? dax ?? string.Empty : rewrite(dax, site);

        foreach (var table in model.Tables)
        {
            var home = table.Name;
            var tn = $"'{names.Table(home)}'";
            DaxSite Site(string location, bool calcTable = false) => new(location, home, calcTable);

            foreach (var m in table.Measures)
            {
                var where = $"Measure {tn}[{names.Measure(m.Name)}]";
                m.Expression = Apply(m.Expression, Site(where));
                if (m.FormatStringDefinition is not null)
                {
                    m.FormatStringDefinition.Expression = Apply(m.FormatStringDefinition.Expression, Site(where + " format string"));
                }

                if (m.DetailRowsDefinition is not null)
                {
                    m.DetailRowsDefinition.Expression = Apply(m.DetailRowsDefinition.Expression, Site(where + " detail rows"));
                }

                if (m.KPI is not null)
                {
                    m.KPI.TargetExpression = Apply(m.KPI.TargetExpression, Site(where + " KPI target"));
                    m.KPI.StatusExpression = Apply(m.KPI.StatusExpression, Site(where + " KPI status"));
                    m.KPI.TrendExpression = Apply(m.KPI.TrendExpression, Site(where + " KPI trend"));
                }
            }

            foreach (var c in table.Columns.OfType<CalculatedColumn>())
            {
                c.Expression = Apply(c.Expression, Site($"Calculated column {tn}[{names.Column(home, c.Name)}]"));
            }

            foreach (var p in table.Partitions)
            {
                if (p.Source is CalculatedPartitionSource cps)
                {
                    cps.Expression = Apply(cps.Expression, Site($"Calculated table {tn}", calcTable: true));
                }
            }

            if (table.CalculationGroup is not null)
            {
                foreach (var item in table.CalculationGroup.CalculationItems)
                {
                    var where = $"Calculation item {tn}[{item.Name}]";
                    item.Expression = Apply(item.Expression, Site(where));
                    if (item.FormatStringDefinition is not null)
                    {
                        item.FormatStringDefinition.Expression = Apply(item.FormatStringDefinition.Expression, Site(where + " format string"));
                    }
                }
            }

            if (table.DefaultDetailRowsDefinition is not null)
            {
                table.DefaultDetailRowsDefinition.Expression = Apply(table.DefaultDetailRowsDefinition.Expression, Site($"Detail rows {tn}"));
            }
        }

        foreach (var role in model.Roles)
        {
            foreach (var tp in role.TablePermissions)
            {
                tp.FilterExpression = Apply(tp.FilterExpression, new DaxSite($"Role filter on '{names.Table(tp.Name)}'", tp.Name, false));
            }
        }
    }
}
