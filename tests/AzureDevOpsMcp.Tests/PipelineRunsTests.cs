using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// list_pipeline_runs across several pipelines or by run id: which request each argument shape
/// builds, and which rows say what pipeline they belong to.
/// </summary>
public class PipelineRunsTests
{
    [Fact]
    public void Several_pipelines_go_out_as_one_definitions_list_with_a_per_pipeline_cap()
    {
        var path = AdoTools.RunsPath("p1", ["16", "28"], null, null, perPipeline: 2, null, null, null, limit: 20);

        Assert.Contains("&definitions=16,28", path, StringComparison.Ordinal);
        Assert.Contains("&maxBuildsPerDefinition=2", path, StringComparison.Ordinal);
        Assert.Contains("&$top=21", path, StringComparison.Ordinal);
        Assert.DoesNotContain("buildIds", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ids_and_a_build_number_are_sent_as_the_service_spells_them()
    {
        var byId = AdoTools.RunsPath("p1", [], AdoTools.RunIds(null, "101, 102", null), null, null, null, null, null, 20);
        var byNumber = AdoTools.RunsPath("p1", [], null, "20260915.*", null, null, null, null, 20);

        Assert.Contains("&buildIds=101,102", byId, StringComparison.Ordinal);
        Assert.DoesNotContain("definitions=", byId, StringComparison.Ordinal);
        Assert.Contains("&buildNumber=20260915.%2A", byNumber, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ids_with_a_pipeline_or_nothing_at_all_is_refused()
    {
        Assert.Contains("not both",
            Assert.Throws<McpException>(() => AdoTools.RunIds("16", "101", null)).Message);
        Assert.Contains("run_ids",
            Assert.Throws<McpException>(() => AdoTools.RunIds(null, null, null)).Message);
        Assert.Contains("`run_ids` takes run numbers",
            Assert.Throws<McpException>(() => AdoTools.RunIds(null, "101,abc", null)).Message);
        Assert.Null(AdoTools.RunIds("16,28", null, null));
    }

    [Fact]
    public void A_row_names_its_pipeline_only_when_asked_and_its_reason_only_when_not_ci()
    {
        // The tool asks for the name unless the caller named exactly one pipeline: run ids and a
        // build number say nothing about which pipeline a run belongs to, which is the question.
        var manual = new WireBuild(7, "1", "completed", "succeeded", null, null, null, null,
            new WireBuildDefinition(16, "Web CI"), null, null, Reason: "manual");
        var ci = manual with { Reason = "individualCI" };

        Assert.Equal("Web CI", Mapping.Run(manual, namePipeline: true).Pipeline);
        Assert.Null(Mapping.Run(manual, namePipeline: false).Pipeline);
        Assert.Equal("manual", Mapping.Run(manual).Reason);
        Assert.Null(Mapping.Run(ci).Reason);
        Assert.Null(Mapping.Run(ci with { Reason = "batchedCI" }).Reason);
    }
}
