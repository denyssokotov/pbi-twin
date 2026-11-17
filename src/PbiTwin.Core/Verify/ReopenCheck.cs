using PbiTwin.Core.Metadata;
using PbiTwin.Core.Output;

namespace PbiTwin.Core.Verify;

/// <summary>Opens the finished twin from disk the way Power BI Desktop would find it, and compares it with what was written.</summary>
public static class ReopenCheck
{
    public static Check Run(TwinFolder twin, int expectedTables, int expectedRelationships)
    {
        ArgumentNullException.ThrowIfNull(twin);

        try
        {
            var project = PbipProject.Open(twin.PbipFile);
            var model = project.LoadDatabase().Model;
            if (model.Tables.Count != expectedTables || model.Relationships.Count != expectedRelationships)
            {
                return new("Twin opens", CheckStatus.Fail,
                    $"the twin on disk has {model.Tables.Count} tables and {model.Relationships.Count} relationships, expected {expectedTables} and {expectedRelationships}");
            }

            var report = Path.Combine(project.ReportFolder, "definition.pbir");
            return File.Exists(report)
                ? new("Twin opens", CheckStatus.Pass, $"the .pbip, report and model load again from disk ({expectedTables} tables, {expectedRelationships} relationships)")
                : new("Twin opens", CheckStatus.Fail, "the report definition is missing");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new("Twin opens", CheckStatus.Fail, "the twin could not be loaded again: " + ex.Message);
        }
    }
}
