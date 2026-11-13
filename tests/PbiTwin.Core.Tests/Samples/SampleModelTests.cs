using Microsoft.AnalysisServices.Tabular;
using PbiTwin.Core.Tests.Guards;

namespace PbiTwin.Core.Tests.Samples;

/// <summary>The hand-written ContosoPII TMDL must be loadable by the official TOM serializer.</summary>
public class SampleModelTests
{
    private static readonly string ModelFolder =
        Path.Combine(RepoPaths.Root, "samples", "ContosoPII", "ContosoPII.SemanticModel", "definition");

    private static Model Load() => TmdlSerializer.DeserializeDatabaseFromFolder(ModelFolder).Model;

    [Fact]
    public void Sample_model_deserializes()
    {
        var model = Load();

        Assert.Equal(
            ["DimCustomer", "DimDate", "DimEmployee", "DimProduct", "DimStore", "FactSales", "Time Intelligence"],
            model.Tables.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(6, model.Relationships.Count);
        Assert.Single(model.Roles);
        Assert.Equal(2, model.Expressions.Count);
    }

    [Fact]
    public void Sample_model_has_the_features_the_anonymizer_must_handle()
    {
        var model = Load();
        var fact = model.Tables["FactSales"];

        Assert.Contains(fact.Measures, m => m.Expression.Contains("\"Enterprise\"", StringComparison.Ordinal));
        Assert.Contains(fact.Columns, c => c is CalculatedColumn);
        Assert.Contains(model.Tables["DimEmployee"].Columns, c => c is CalculatedColumn);
        Assert.NotNull(model.Tables["Time Intelligence"].CalculationGroup);
        Assert.Equal(3, model.Tables["Time Intelligence"].CalculationGroup.CalculationItems.Count);
        Assert.IsType<CalculatedPartitionSource>(model.Tables["DimDate"].Partitions[0].Source);
        Assert.All(model.Tables["DimDate"].Columns.Where(c => c.Type != ColumnType.RowNumber), c => Assert.IsType<CalculatedTableColumn>(c));
        Assert.Contains("\"West\"", model.Roles["Region Manager"].TablePermissions["DimStore"].FilterExpression, StringComparison.Ordinal);
        Assert.Contains(model.Relationships.OfType<SingleColumnRelationship>(), r => !r.IsActive);
    }
}
