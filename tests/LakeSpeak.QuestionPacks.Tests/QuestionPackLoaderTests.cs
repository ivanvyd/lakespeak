namespace LakeSpeak.QuestionPacks.Tests;

public class QuestionPackLoaderTests
{
    private const string Valid =
        """
        apiVersion: lakespeak.net/v1alpha1
        kind: QuestionPack
        metadata:
          name: daily-brief
          description: A daily summary
        spec:
          agent: platform-operations
          questions:
            - id: failed-jobs
              title: Failed jobs
              ask: Which production jobs failed in the last 24 hours?
              timeout: 90s
          output:
            format: markdown
            path: reports/daily.md
          behavior:
            continueOnQuestionFailure: false
            includeGeneratedSql: true
            includeIdentifiers: true
            timeout: 5m
        """;

    [Fact]
    public void Parses_a_valid_pack()
    {
        // Arrange — the canonical pack above.

        // Act
        var pack = QuestionPackLoader.Parse(Valid, "/packs");

        // Assert
        pack.Name.ShouldBe("daily-brief");
        pack.Agent.ShouldBe("platform-operations");
        pack.Questions.Single().Id.ShouldBe("failed-jobs");
        pack.Questions.Single().Timeout.ShouldBe(TimeSpan.FromSeconds(90));
        pack.Behavior.ContinueOnQuestionFailure.ShouldBeFalse();
        pack.Behavior.IncludeGeneratedSql.ShouldBeTrue();
        pack.Behavior.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Still_parses_a_pack_written_before_the_domain_moved()
    {
        // Arrange — the apiVersion packs declared while the project used lakespeak.dev.
        var yaml = Valid.Replace(
            QuestionPackLoader.CurrentApiVersion,
            QuestionPackLoader.LegacyApiVersion,
            StringComparison.Ordinal);

        // Act
        var pack = QuestionPackLoader.Parse(yaml, "/packs");

        // Assert — the same pack, not a degraded one.
        pack.Name.ShouldBe("daily-brief");
        pack.Questions.Single().Id.ShouldBe("failed-jobs");
    }

    [Fact]
    public void Defaults_are_the_safe_ones()
    {
        // Arrange — a pack with no behavior block at all.
        const string minimal =
            """
            apiVersion: lakespeak.net/v1alpha1
            kind: QuestionPack
            metadata:
              name: minimal
            spec:
              agent: sales
              questions:
                - id: q1
                  ask: How did revenue change?
            """;

        // Act
        var pack = QuestionPackLoader.Parse(minimal, "/packs");

        // Assert — identifiers off by default because a conversation id points at governed data
        // and reports get committed; continue-on-failure on, because partial results beat none
        // for a scheduled report.
        pack.Behavior.IncludeIdentifiers.ShouldBeFalse();
        pack.Behavior.IncludeGeneratedSql.ShouldBeFalse();
        pack.Behavior.ContinueOnQuestionFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("../escaped.md")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escaped.md")]
    public void Rejects_an_output_path_that_escapes_the_pack_directory(string path)
    {
        // Arrange — a pack can arrive from a pull request, so its output path is
        // attacker-influenced.
        var yaml = Valid.Replace("path: reports/daily.md", $"path: {path}", StringComparison.Ordinal);

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Assert
        ex.Errors.ShouldContain(e => e.Contains("outside the pack directory", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_absolute_output_path()
    {
        // Arrange
        var absolute = OperatingSystem.IsWindows() ? @"C:\temp\out.md" : "/tmp/out.md";
        var yaml = Valid.Replace("path: reports/daily.md", $"path: '{absolute}'", StringComparison.Ordinal);

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Assert
        ex.Errors.ShouldContain(e => e.Contains("must be relative", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        // Arrange — a pack broken three different ways.
        const string broken =
            """
            apiVersion: lakespeak.net/v1alpha1
            kind: QuestionPack
            metadata:
              name: Bad_Name
            spec:
              agent: sales
              questions:
                - id: Upper-Case
                  ask: one
                - id: dup
                  ask: two
                - id: dup
                  ask: three
            """;

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(broken, "/packs"));

        // Assert — reporting only the first turns fixing a pack into repeated guessing.
        ex.Errors.Count.ShouldBeGreaterThanOrEqualTo(3);
        ex.Errors.ShouldContain(e => e.Contains("kebab-case", StringComparison.Ordinal));
        ex.Errors.ShouldContain(e => e.Contains("duplicate question id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("lakespeak.net/v1", "apiVersion")]
    [InlineData("lakespeak.net/v1alpha1", "kind")]
    public void Rejects_a_wrong_apiVersion_or_kind(string apiVersion, string broken)
    {
        // Arrange
        var yaml = Valid.Replace("apiVersion: lakespeak.net/v1alpha1", $"apiVersion: {apiVersion}", StringComparison.Ordinal);
        if (broken == "kind")
        {
            yaml = yaml.Replace("kind: QuestionPack", "kind: Something", StringComparison.Ordinal);
        }

        // Act
        var act = () => QuestionPackLoader.Parse(yaml, "/packs");

        // Assert
        Should.Throw<PackValidationException>(act);
    }

    [Fact]
    public void Requires_an_agent()
    {
        // Arrange — a report that silently ran against a different Agent is worse than one that
        // failed, so the Agent is never inferred.
        var yaml = Valid.Replace("  agent: platform-operations\n", string.Empty, StringComparison.Ordinal);

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Assert
        ex.Errors.ShouldContain(e => e.Contains("spec.agent is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Caps_the_number_of_questions()
    {
        // Arrange — each question occupies a SQL warehouse; beyond the cap it is a scheduled
        // job, not a report.
        var questions = string.Join('\n',
            Enumerable.Range(1, 51).Select(i => $"    - id: q{i}\n      ask: question {i}"));

        var yaml =
            $"""
            apiVersion: lakespeak.net/v1alpha1
            kind: QuestionPack
            metadata:
              name: huge
            spec:
              agent: sales
              questions:
            {questions}
            """;

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Assert
        ex.Errors.ShouldContain(e => e.Contains("maximum is 50", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unparseable_duration()
    {
        // Arrange
        var yaml = Valid.Replace("timeout: 90s", "timeout: soon", StringComparison.Ordinal);

        // Act
        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Assert
        ex.Errors.ShouldContain(e => e.Contains("is not a duration", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_blank_profile()
    {
        var yaml = Valid.Replace(
            "  agent: platform-operations",
            "  agent: platform-operations\n  profile: '   '",
            StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("spec.profile", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unparseable_pack_timeout_and_reports_other_errors()
    {
        var yaml = Valid
            .Replace("name: daily-brief", "name: Bad_Name", StringComparison.Ordinal)
            .Replace("timeout: 5m", "timeout: soon", StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("metadata.name", StringComparison.Ordinal));
        ex.Errors.ShouldContain(e => e.Contains("behavior.timeout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("01s")]
    [InlineData("''")]
    [InlineData("597h")]
    [InlineData("99999999999999999999h")]
    public void Rejects_a_non_positive_or_unrepresentable_duration(string timeout)
    {
        var yaml = Valid.Replace("timeout: 90s", $"timeout: {timeout}", StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unsupported_pack_timeout_before_execution()
    {
        var yaml = Valid.Replace("timeout: 5m", "timeout: 597h", StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("behavior.timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_blank_output_path()
    {
        var yaml = Valid.Replace(
            "path: reports/daily.md",
            "path: '   '",
            StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("spec.output.path", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_null_question_node()
    {
        var yaml = Valid.Replace(
            "    - id: failed-jobs\n      title: Failed jobs\n      ask: Which production jobs failed in the last 24 hours?\n      timeout: 90s",
            "    - null",
            StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("question 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Enforces_published_text_limits()
    {
        var yaml = Valid
            .Replace("A daily summary", new string('d', 501), StringComparison.Ordinal)
            .Replace("Failed jobs", new string('t', 201), StringComparison.Ordinal)
            .Replace("Which production jobs failed in the last 24 hours?", new string('a', 4001), StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("description", StringComparison.Ordinal));
        ex.Errors.ShouldContain(e => e.Contains("title", StringComparison.Ordinal));
        ex.Errors.ShouldContain(e => e.Contains("ask", StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_published_text_limit_boundaries()
    {
        var yaml = Valid
            .Replace("A daily summary", new string('d', 500), StringComparison.Ordinal)
            .Replace("Failed jobs", new string('t', 200), StringComparison.Ordinal)
            .Replace("Which production jobs failed in the last 24 hours?", new string('a', 4000), StringComparison.Ordinal);

        var pack = QuestionPackLoader.Parse(yaml, "/packs");

        pack.Description!.Length.ShouldBe(500);
        pack.Questions.Single().Title!.Length.ShouldBe(200);
        pack.Questions.Single().Ask.Length.ShouldBe(4000);
    }

    [Fact]
    public void Text_limits_count_unicode_code_points_like_the_published_schema()
    {
        var fourThousandEmoji = string.Concat(Enumerable.Repeat("🙂", 4000));
        var yaml = Valid.Replace(
            "Which production jobs failed in the last 24 hours?",
            fourThousandEmoji,
            StringComparison.Ordinal);

        var pack = QuestionPackLoader.Parse(yaml, "/packs");

        pack.Questions.Single().Ask.ShouldBe(fourThousandEmoji);

        var tooLong = yaml.Replace(fourThousandEmoji, fourThousandEmoji + "🙂", StringComparison.Ordinal);
        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(tooLong, "/packs"));
    }

    [Fact]
    public void Rejects_an_unknown_key()
    {
        // Arrange — an unknown key is far more likely a typo in a real key than a deliberate
        // extension, and ignoring it means the setting the author intended never takes effect.
        var yaml = Valid.Replace("  agent: platform-operations",
            "  agent: platform-operations\n  contineuOnFailure: true", StringComparison.Ordinal);

        // Act
        var act = () => QuestionPackLoader.Parse(yaml, "/packs");

        // Assert
        Should.Throw<PackValidationException>(act);
    }
}
