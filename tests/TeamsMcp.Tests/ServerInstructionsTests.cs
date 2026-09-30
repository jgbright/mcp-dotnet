namespace TeamsMcp.Tests;

/// <summary>
/// Claude Code keeps only the first 2,048 characters of a server's instructions, so the ones a
/// model cannot do without have to sit inside that. 2,000 leaves room for a line-ending change.
/// </summary>
public class ServerInstructionsTests
{
    [Theory]
    [InlineData("will not change on retry")]
    [InlineData("carries a req=N")]
    [InlineData("Absent fields are absent on purpose")]
    [InlineData("the boundary is inclusive")]
    [InlineData("do not conclude \"nothing was said\"")]
    [InlineData("has been rejected before the tool ran")]
    public void What_a_model_cannot_do_without_survives_truncation(string marker)
    {
        var index = global::Program.ServerInstructions.IndexOf(marker, StringComparison.Ordinal);

        Assert.InRange(index, 0, 2000 - marker.Length);
    }
}
