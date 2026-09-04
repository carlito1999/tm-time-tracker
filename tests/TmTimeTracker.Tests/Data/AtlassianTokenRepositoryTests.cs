using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class AtlassianTokenRepositoryTests
{
    private sealed class PassthroughProtector : TmTimeTracker.Platform.ITokenProtector
    {
        public byte[] Protect(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    }

    private static (JiraApiTokenRepository Jira,
                    BitbucketApiTokenRepository Bitbucket,
                    GitLabApiTokenRepository GitLab) Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        var protector = new PassthroughProtector();
        return (new JiraApiTokenRepository(factory, protector),
                new BitbucketApiTokenRepository(factory, protector),
                new GitLabApiTokenRepository(factory, protector));
    }

    [Fact]
    public void Round_trips_a_credential()
    {
        var (jira, _, _) = Build();

        jira.Save("lefteris@thecube.dev", "token-abc");

        jira.Get().Should().Be(new AtlassianCredential("lefteris@thecube.dev", "token-abc"));
    }

    // The two tokens are not interchangeable - Bitbucket rejects the scopeless Jira one - so
    // storing them in the same slot would silently break whichever was written second.
    [Fact]
    public void Keeps_the_jira_and_bitbucket_tokens_apart()
    {
        var (jira, bitbucket, _) = Build();

        jira.Save("lefteris@thecube.dev", "jira-token");
        bitbucket.Save("lefteris@thecube.dev", "bitbucket-token");

        jira.Get()!.Token.Should().Be("jira-token");
        bitbucket.Get()!.Token.Should().Be("bitbucket-token");
    }

    [Fact]
    public void Replaces_rather_than_accumulating_on_a_second_save()
    {
        var (jira, _, _) = Build();

        jira.Save("old@example.com", "old-token");
        jira.Save("new@example.com", "new-token");

        jira.Get().Should().Be(new AtlassianCredential("new@example.com", "new-token"));
    }

    [Fact]
    public void Reports_nothing_saved_before_a_first_save_and_after_clearing()
    {
        var (jira, _, _) = Build();
        jira.Get().Should().BeNull();

        jira.Save("lefteris@thecube.dev", "token");
        jira.Clear();

        jira.Get().Should().BeNull();
    }

    // A third provider, a third token. The GitLab REST API needs a Personal Access Token with
    // read_api; neither Atlassian token reaches it, and the gitlab.com credential Git Credential
    // Manager holds is git-transport only and answers 403 insufficient_scope to every API call.
    [Fact]
    public void Keeps_the_gitlab_token_in_its_own_row()
    {
        var (jira, bitbucket, gitlab) = Build();

        jira.Save("lefteris@thecube.dev", "jira-token");
        bitbucket.Save("lefteris@thecube.dev", "bitbucket-token");
        gitlab.Save("lefteris31", "glpat-token");

        jira.Get()!.Token.Should().Be("jira-token");
        bitbucket.Get()!.Token.Should().Be("bitbucket-token");
        gitlab.Get()!.Token.Should().Be("glpat-token");
    }

    // GitLab authenticates with the token alone, so the email column carries the username the
    // /user probe reported - that is what the settings page shows back to the user.
    [Fact]
    public void Stores_the_gitlab_username_in_place_of_an_email()
    {
        var (_, _, gitlab) = Build();

        gitlab.Save("lefteris31", "glpat-token");

        gitlab.Get().Should().Be(new AtlassianCredential("lefteris31", "glpat-token"));
    }

    [Fact]
    public void Reports_no_gitlab_token_before_a_first_save()
    {
        var (_, _, gitlab) = Build();

        gitlab.Get().Should().BeNull();
    }
}
