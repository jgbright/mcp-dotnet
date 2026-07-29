namespace TeamsMcp.Tests;

/// <summary>
/// A broken log must never break the server, so most of these cover the failure paths.
/// </summary>
public class FileLineSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "teams-mcp-tests", Guid.NewGuid().ToString("N"));

    private string LogPath(string name = "teams-mcp.log") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Missing_directories_are_created_on_first_write()
    {
        using var sink = new FileLineSink(Path.Combine(_dir, "nested", "deep", "teams-mcp.log"));

        sink.Write("first");

        Assert.Equal(["first"], ReadAllLines(Path.Combine(_dir, "nested", "deep", "teams-mcp.log")));
    }

    [Fact]
    public void Lines_are_appended_in_order_and_survive_without_disposal()
    {
        var path = LogPath();
        using var sink = new FileLineSink(path);

        sink.Write("one");
        sink.Write("two");

        // Read while the sink still holds the file. Every line is flushed as it is written, which
        // is what makes the log usable after a crash.
        Assert.Equal(["one", "two"], ReadAllLines(path));
    }

    [Fact]
    public void A_second_sink_appends_rather_than_truncating()
    {
        var path = LogPath();
        using (var first = new FileLineSink(path))
        {
            first.Write("from pid A");
        }
        using (var second = new FileLineSink(path))
        {
            second.Write("from pid B");
        }

        Assert.Equal(["from pid A", "from pid B"], ReadAllLines(path));
    }

    [Fact]
    public void The_file_rolls_to_dot_one_past_the_size_cap()
    {
        var path = LogPath();
        using var sink = new FileLineSink(path);

        sink.Write(new string('x', 9 * 1024 * 1024)); // over the 8 MB cap
        sink.Write("after the roll");

        Assert.True(File.Exists(path + ".1"), "the oversized log should have been rolled aside");
        Assert.Equal(["after the roll"], ReadAllLines(path));
    }

    [Fact]
    public void An_unwritable_path_is_swallowed_rather_than_thrown()
    {
        Directory.CreateDirectory(LogPath("occupied.log")); // a directory where the log file should go
        using var sink = new FileLineSink(LogPath("occupied.log"));

        var exception = Record.Exception(() => sink.Write("this cannot be written"));

        Assert.Null(exception);
    }

    [Fact]
    public void Writing_after_disposal_is_a_no_op()
    {
        var path = LogPath();
        var sink = new FileLineSink(path);
        sink.Write("before");
        sink.Dispose();

        sink.Write("after");

        Assert.Equal(["before"], ReadAllLines(path));
    }

    private static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
