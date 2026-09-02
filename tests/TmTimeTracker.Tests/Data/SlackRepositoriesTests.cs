using FluentAssertions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class SlackRepositoriesTests
{
    private static ISqliteConnectionFactory NewDb()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return factory;
    }

    private static SlackChannelMapping Mapping(string project = "SN") =>
        new(project, "C0809CK6C14", "sheeponline", "Done {TICKET}");

    [Fact]
    public void Channel_upsert_then_find_round_trips()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());

        var found = repo.Find("SN");
        found.Should().NotBeNull();
        found!.ChannelId.Should().Be("C0809CK6C14");
        found.ChannelName.Should().Be("sheeponline");
        found.MessageTemplate.Should().Be("Done {TICKET}");
    }

    [Fact]
    public void Channel_upsert_replaces_an_existing_project_row()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());
        repo.Upsert(new SlackChannelMapping("SN", "C999", "elsewhere", "New {TICKET}"));

        repo.GetAll().Should().HaveCount(1);
        repo.Find("SN")!.ChannelName.Should().Be("elsewhere");
    }

    [Fact]
    public void Channel_find_returns_null_for_unknown_project()
    {
        new SlackChannelRepository(NewDb()).Find("NOPE").Should().BeNull();
    }

    [Fact]
    public void Channel_remove_deletes_the_row()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping());
        repo.Remove("SN");
        repo.Find("SN").Should().BeNull();
    }

    [Fact]
    public void Channel_get_all_is_ordered_by_project_key()
    {
        var repo = new SlackChannelRepository(NewDb());
        repo.Upsert(Mapping("TM"));
        repo.Upsert(Mapping("SN"));

        repo.GetAll().Select(m => m.ProjectKey).Should().ContainInOrder("SN", "TM");
    }

    [Fact]
    public void Credential_save_get_clear_round_trips_through_the_protector()
    {
        var protector = new Mock<ITokenProtector>();
        protector.Setup(p => p.Protect("xoxp-secret")).Returns(new byte[] { 1, 2, 3 });
        protector.Setup(p => p.Unprotect(It.Is<byte[]>(b => b.Length == 3))).Returns("xoxp-secret");

        var repo = new SlackCredentialRepository(NewDb(), protector.Object);
        repo.Get().Should().BeNull();

        repo.Save("xoxp-secret");
        repo.Get().Should().Be("xoxp-secret");

        repo.Clear();
        repo.Get().Should().BeNull();
    }

    [Fact]
    public void Credential_save_twice_keeps_a_single_row()
    {
        var protector = new Mock<ITokenProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>())).Returns(new byte[] { 9 });
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns("second");

        var repo = new SlackCredentialRepository(NewDb(), protector.Object);
        repo.Save("first");
        repo.Save("second");

        repo.Get().Should().Be("second");
    }
}
