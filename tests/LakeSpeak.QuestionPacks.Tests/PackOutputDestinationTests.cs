namespace LakeSpeak.QuestionPacks.Tests;

public sealed class PackOutputDestinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lakespeak-pack-output-{Guid.NewGuid():N}");

    public PackOutputDestinationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Preflight_rejects_an_existing_destination_without_force()
    {
        var target = Path.Combine(_root, "report.md");
        File.WriteAllText(target, "existing");

        var ex = Should.Throw<PackOutputException>(() =>
            PackOutputDestination.Preflight(_root, "report.md", isPackPath: true, overwrite: false));

        ex.Message.ShouldContain("already exists");
        File.ReadAllText(target).ShouldBe("existing");
    }

    [Fact]
    public void Preflight_rejects_a_blank_output_as_an_actionable_pack_error()
    {
        var ex = Should.Throw<PackOutputException>(() =>
            PackOutputDestination.Preflight(_root, " ", isPackPath: false, overwrite: false));

        ex.Message.ShouldContain("output path is missing", Case.Insensitive);
    }

    [Fact]
    public void Filesystem_root_is_a_valid_containment_boundary()
    {
        var root = Path.GetPathRoot(_root)!;
        var child = Path.Combine(root, "report.md");

        QuestionPackLoader.IsInside(child, root).ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_creation_is_never_overwritten_without_force()
    {
        using var destination = PackOutputDestination.Preflight(
            _root, "report.md", isPackPath: true, overwrite: false);
        File.WriteAllText(destination.FullPath, "concurrent");

        await Should.ThrowAsync<IOException>(() => destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
                await writer.WriteAsync("new".AsMemory(), cancellationToken),
            TestContext.Current.CancellationToken));

        File.ReadAllText(destination.FullPath).ShouldBe("concurrent");
    }

    [Fact]
    public async Task Normal_nested_output_is_installed_atomically()
    {
        using var destination = PackOutputDestination.Preflight(
            _root, "reports/daily/report.md", isPackPath: true, overwrite: false);

        await destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
                await writer.WriteAsync("complete report".AsMemory(), cancellationToken),
            TestContext.Current.CancellationToken);

        File.ReadAllText(destination.FullPath).ShouldBe("complete report");
        Directory.GetFiles(Path.GetDirectoryName(destination.FullPath)!, ".lakespeak-*.tmp")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Explicit_output_override_may_target_a_user_selected_path_outside_the_pack_directory()
    {
        var packDirectory = Path.Combine(_root, "pack");
        var selectedDirectory = Path.Combine(_root, "selected");
        Directory.CreateDirectory(packDirectory);
        using var destination = PackOutputDestination.Preflight(
            packDirectory,
            Path.Combine(selectedDirectory, "report.md"),
            isPackPath: false,
            overwrite: false);

        await destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
                await writer.WriteAsync("selected".AsMemory(), cancellationToken),
            TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(selectedDirectory, "report.md")).ShouldBe("selected");
    }

    [Fact]
    public async Task Failed_overwrite_preserves_the_previous_complete_report()
    {
        var target = Path.Combine(_root, "report.md");
        File.WriteAllText(target, "previous complete report");
        using var destination = PackOutputDestination.Preflight(
            _root, "report.md", isPackPath: true, overwrite: true);

        await Should.ThrowAsync<IOException>(() => destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
            {
                await writer.WriteAsync("partial".AsMemory(), cancellationToken);
                throw new IOException("synthetic disk failure");
            },
            TestContext.Current.CancellationToken));

        File.ReadAllText(target).ShouldBe("previous complete report");
        Directory.GetFiles(_root, ".lakespeak-*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Successful_force_replaces_only_the_verified_destination()
    {
        var target = Path.Combine(_root, "report.md");
        var sibling = Path.Combine(_root, "sibling.md");
        File.WriteAllText(target, "previous");
        File.WriteAllText(sibling, "keep");
        using var destination = PackOutputDestination.Preflight(
            _root, "report.md", isPackPath: true, overwrite: true);

        await destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
                await writer.WriteAsync("replacement".AsMemory(), cancellationToken),
            TestContext.Current.CancellationToken);

        File.ReadAllText(target).ShouldBe("replacement");
        File.ReadAllText(sibling).ShouldBe("keep");
    }

    [Fact]
    public async Task Cancelled_overwrite_preserves_the_previous_complete_report()
    {
        var target = Path.Combine(_root, "report.md");
        File.WriteAllText(target, "previous complete report");
        using var destination = PackOutputDestination.Preflight(
            _root, "report.md", isPackPath: true, overwrite: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(() => destination.WriteAtomicAsync(
            async (writer, token) =>
            {
                await writer.WriteAsync("partial".AsMemory(), token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
            cancellation.Token));

        File.ReadAllText(target).ShouldBe("previous complete report");
        Directory.GetFiles(_root, ".lakespeak-*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_parent_swapped_for_a_link_after_preflight_is_rejected()
    {
        var reports = Path.Combine(_root, "reports");
        var displaced = Path.Combine(_root, "reports-original");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(reports);
        Directory.CreateDirectory(outside);

        using var destination = PackOutputDestination.Preflight(
            _root, "reports/report.md", isPackPath: true, overwrite: true);
        try
        {
            Directory.Move(reports, displaced);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // The held directory handle denies delete sharing, so Windows prevents the swap.
            File.Exists(Path.Combine(outside, "report.md")).ShouldBeFalse();
            return;
        }
        if (!TryCreateDirectoryLink(reports, outside))
        {
            Directory.Move(displaced, reports);
            Assert.Skip("This runner cannot create a directory symbolic link or junction.");
        }

        await Should.ThrowAsync<PackOutputException>(() => destination.WriteAtomicAsync(
            async (writer, cancellationToken) =>
                await writer.WriteAsync("escaped".AsMemory(), cancellationToken),
            TestContext.Current.CancellationToken));

        File.Exists(Path.Combine(outside, "report.md")).ShouldBeFalse();
    }

    [Fact]
    public void Loader_rejects_a_final_file_link_even_when_its_target_is_inside_the_pack_directory()
    {
        var actual = Path.Combine(_root, "actual.md");
        var link = Path.Combine(_root, "report.md");
        File.WriteAllText(actual, "safe");
        if (!TryCreateFileLink(link, actual))
        {
            Assert.Skip("This runner cannot create a file symbolic link.");
        }

        var ex = Should.Throw<PackValidationException>(() =>
            QuestionPackLoader.Parse(YamlWithOutput("report.md"), _root));

        ex.Errors.ShouldContain(e => e.Contains("symbolic link or junction", StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_rejects_a_dangling_final_file_link()
    {
        var link = Path.Combine(_root, "report.md");
        if (!TryCreateFileLink(link, Path.Combine(_root, "missing.md")))
        {
            Assert.Skip("This runner cannot create a dangling file symbolic link.");
        }

        var ex = Should.Throw<PackValidationException>(() =>
            QuestionPackLoader.Parse(YamlWithOutput("report.md"), _root));

        ex.Errors.ShouldContain(e => e.Contains("symbolic link or junction", StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_rejects_a_parent_link_even_when_its_target_is_inside_the_pack_directory()
    {
        var actual = Path.Combine(_root, "actual");
        var link = Path.Combine(_root, "reports");
        Directory.CreateDirectory(actual);
        if (!TryCreateDirectoryLink(link, actual))
        {
            Assert.Skip("This runner cannot create a directory symbolic link or junction.");
        }

        var ex = Should.Throw<PackValidationException>(() =>
            QuestionPackLoader.Parse(YamlWithOutput("reports/report.md"), _root));

        ex.Errors.ShouldContain(e => e.Contains("symbolic link or junction", StringComparison.Ordinal));
    }

    private static string YamlWithOutput(string path) =>
        $$"""
        apiVersion: lakespeak.net/v1alpha1
        kind: QuestionPack
        metadata:
          name: output-test
        spec:
          agent: sales
          questions:
            - id: q1
              ask: test
          output:
            path: {{path}}
        """;

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
