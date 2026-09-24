using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TmTimeTracker;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using Xunit;

namespace TmTimeTracker.Tests.Services;

/// <summary>
/// A missing registration cannot fail any unit test - every one of them builds its subject by
/// hand. It would fail at startup instead, on the user's machine. Building the real container
/// with validation is what catches it here.
/// </summary>
public class ClaudeServiceWiringTests
{
    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .AddTmTimeTrackerCore()
            .AddJiraServices()
            .AddClaudeServices()
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateOnBuild = true;
                o.ValidateScopes = true;
            })
            .Build();

    [Fact]
    public void The_estimation_worker_can_be_constructed()
    {
        using var host = BuildHost();

        var workers = host.Services.GetServices<IHostedService>();

        workers.Should().ContainItemsAssignableTo<TicketEstimationWorker>();
    }

    [Theory]
    [InlineData(typeof(IClaudeEstimator))]
    [InlineData(typeof(IGitWorktreeManager))]
    [InlineData(typeof(IJiraSearchSource))]
    [InlineData(typeof(IJiraEstimateWriter))]
    [InlineData(typeof(IJiraProjectSource))]
    [InlineData(typeof(TmTimeTracker.Jev.IJevClient))]
    public void Every_estimation_seam_resolves(Type seam)
    {
        using var host = BuildHost();

        host.Services.GetService(seam).Should().NotBeNull();
    }

    // All three Jira seams are the one client, so a single token refresh serves them all.
    [Fact]
    public void The_jira_seams_share_one_client()
    {
        using var host = BuildHost();

        var search = host.Services.GetRequiredService<IJiraSearchSource>();
        var writer = host.Services.GetRequiredService<IJiraEstimateWriter>();
        var projects = host.Services.GetRequiredService<IJiraProjectSource>();

        search.Should().BeSameAs(writer).And.BeSameAs(projects);
    }
}
