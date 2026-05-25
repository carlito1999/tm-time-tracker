using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TrackedRepoRepositoryTests
{
    private static TrackedRepoRepository New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return new TrackedRepoRepository(ds);
    }

    [Fact]
    public void Add_then_GetAll_returns_in_sort_order()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.GetAll().Select(r => r.Path).Should().Equal(@"c:\projects\a", @"c:\projects\b");
    }

    [Fact]
    public void Add_is_case_insensitive_dedupe()
    {
        var repo = New();
        repo.Add(@"C:\Projects\X");
        repo.Add(@"c:\projects\x");
        repo.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void Remove_by_path_deletes_matching_row()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.Remove(@"c:\projects\a");
        repo.GetAll().Should().ContainSingle().Which.Path.Should().Be(@"c:\projects\b");
    }

    [Fact]
    public void Remove_is_case_insensitive()
    {
        var repo = New();
        repo.Add(@"C:\Projects\X");
        repo.Remove(@"c:\projects\x");
        repo.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Sort_order_increments_per_insertion()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.Add(@"c:\projects\c");
        var rows = repo.GetAll();
        rows.Select(r => r.SortOrder).Should().BeInAscendingOrder();
    }

    [Fact]
    public void GetAll_returns_empty_list_initially()
    {
        New().GetAll().Should().BeEmpty();
    }
}
