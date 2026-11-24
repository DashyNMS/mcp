using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Mcp.Tests;

/// <summary>Where the last good answers are kept (#39), and what can't be got out of it.</summary>
public sealed class LastKnownGoodTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashynms-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LastKnownGood Answers(string identity = "account", bool onDisk = false)
        => new(identity, Lifetime, onDisk ? _directory : null, _clock, NullLogger<LastKnownGood>.Instance);

    private string[] Files() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public void The_last_answer_to_a_question_is_given_with_when_it_was_true()
    {
        var answers = Answers();
        answers.Keep("a", "first");
        _clock.Advance(TimeSpan.FromHours(1));
        answers.Keep("a", "second");
        var asOf = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(new KnownAnswer("second", asOf), answers.Find("a"));
        Assert.Null(answers.Find("b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_answer_too_old_or_from_the_future_is_not_given(bool onDisk)
    {
        var answers = Answers(onDisk: onDisk);
        answers.Keep("a", "old");

        _clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(answers.Find("a"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(answers.Find("a"));

        _clock.Advance(-Lifetime - TimeSpan.FromHours(1));
        Assert.Null(answers.Find("a"));
    }

    [Fact]
    public void Only_so_many_are_kept_and_the_one_used_longest_ago_goes()
    {
        var answers = Answers();
        for (var i = 0; i < LastKnownGood.MaxAnswers; i++)
        {
            answers.Keep($"{i}", "x");
        }

        Assert.NotNull(answers.Find("0"));
        answers.Keep("one more", "x");

        Assert.NotNull(answers.Find("0"));
        Assert.Null(answers.Find("1"));
        Assert.NotNull(answers.Find("one more"));

        answers.Keep("huge", new string('x', LastKnownGood.MaxCharacters));
        Assert.Null(answers.Find("0"));
        Assert.NotNull(answers.Find("huge"));
    }

    [Fact]
    public void Without_a_folder_nothing_is_written()
    {
        Answers().Keep("a", "answer");

        Assert.Empty(Files());
        Assert.False(Answers().HasStoredAnswers);
    }

    [Fact]
    public void With_one_they_outlast_the_server()
    {
        Assert.False(Answers(onDisk: true).HasStoredAnswers);
        Answers(onDisk: true).Keep("find_device\nquery=\"core\"", "answer");

        var next = Answers(onDisk: true);

        Assert.True(next.HasStoredAnswers);
        Assert.Equal("answer", next.Find("find_device\nquery=\"core\"")!.Json);
        Assert.Null(next.Find("find_device"));
    }

    [Fact]
    public void Neither_the_question_nor_the_answer_can_be_read_from_the_folder()
    {
        Answers("token-of-the-account", onDisk: true).Keep("find_device\nquery=\"core-sw1.example.net\"", """{"hostname":"core-sw1.example.net"}""");

        var file = Assert.Single(Files());
        foreach (var secret in new[] { "core-sw1", "find_device", "token-of-the-account", "hostname" })
        {
            Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public void Another_account_or_configuration_gets_nothing_even_from_a_copied_file()
    {
        Answers("one", onDisk: true).Keep("a", "answer");
        var other = Answers("two", onDisk: true);
        Assert.False(other.HasStoredAnswers);
        Assert.Null(other.Find("a"));

        // Put where the other would look for its own answer to the same question.
        var taken = Assert.Single(Files());
        other.Keep("a", "theirs");
        var theirs = Assert.Single(Files(), f => f != taken);
        File.Copy(taken, theirs, overwrite: true);

        Assert.Null(Answers("two", onDisk: true).Find("a"));
        Assert.Equal("answer", Answers("one", onDisk: true).Find("a")!.Json);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("cut")]
    [InlineData("empty")]
    [InlineData("swapped")]
    public void A_file_that_is_not_as_it_was_written_is_ignored(string how)
    {
        var answers = Answers(onDisk: true);
        answers.Keep("a", "answer");
        var file = Assert.Single(Files());
        var bytes = File.ReadAllBytes(file);
        switch (how)
        {
            case "changed":
                bytes[20] ^= 1;
                break;
            case "cut":
                bytes = bytes[..^1];
                break;
            case "empty":
                bytes = [];
                break;
            default:
                // Another question's answer, whole and from the same account.
                answers.Keep("b", "another");
                bytes = File.ReadAllBytes(Assert.Single(Files(), f => f != file));
                break;
        }

        File.WriteAllBytes(file, bytes);

        Assert.Null(Answers(onDisk: true).Find("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forgetting_one_leaves_the_rest_and_clearing_leaves_none(bool onDisk)
    {
        var answers = Answers(onDisk: onDisk);
        answers.Keep("a", "1");
        answers.Keep("b", "2");

        answers.Forget("a");
        Assert.Null(answers.Find("a"));
        Assert.NotNull(answers.Find("b"));

        answers.Clear();
        Assert.Null(answers.Find("b"));
        Assert.Empty(Files());
        Assert.False(answers.HasStoredAnswers);
    }

    [Fact]
    public void Files_too_old_to_give_are_deleted_at_startup()
    {
        Answers(onDisk: true).Keep("a", "1");
        File.SetLastWriteTimeUtc(Assert.Single(Files()), _clock.GetUtcNow().UtcDateTime - Lifetime);

        Assert.False(Answers(onDisk: true).HasStoredAnswers);
        Assert.Empty(Files());
    }

    [Fact]
    public void A_folder_that_cannot_be_used_leaves_memory()
    {
        // A file where the folder should be.
        File.WriteAllText(_directory, "in the way");
        try
        {
            var answers = Answers(onDisk: true);
            answers.Keep("a", "answer");

            Assert.Equal("answer", answers.Find("a")!.Json);
            Assert.False(answers.HasStoredAnswers);
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Theory]
    [InlineData("{}", "{}", true)]
    [InlineData("""{"limit":10,"query":"core"}""", """{"query":"core"}""", true)]
    [InlineData("""{"query":null}""", "{}", true)]
    [InlineData("""{"query":"core","offset":5}""", """{"offset":5,"query":"core"}""", true)]
    [InlineData("""{"query":"core","unknown":1}""", """{"query":"core"}""", true)]
    [InlineData("""{"query":"core"}""", """{"query":"edge"}""", false)]
    [InlineData("""{"limit":11}""", "{}", false)]
    [InlineData("""{"query":"a\nlimit=11"}""", """{"query":"a","limit":11}""", false)]
    public void The_same_question_is_recognised_however_it_is_put(string one, string other, bool same)
    {
        var tool = new Tool
        {
            Name = "find_device",
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":["string","null"],"default":null},"limit":{"type":"integer","default":10},"offset":{"type":"integer","default":0}}}""").RootElement,
        };
        string Question(string arguments) => LastKnownGoodFilter.Question(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments));

        Assert.Equal(same, Question(one) == Question(other));
    }

    [Fact]
    public void Config_logs_and_images_are_never_kept()
        => Assert.Equal(
            ["config_backup", "config_diff", "graph", "recent_config_changes", "search_logs", "what_changed"],
            LastKnownGoodFilter.NotKept.Order(StringComparer.Ordinal));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
