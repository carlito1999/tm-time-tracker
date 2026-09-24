using FluentAssertions;
using TmTimeTracker.Jev;
using Xunit;

namespace TmTimeTracker.Tests.Jev;

public class JevProviderTests
{
    // OpenRouter forwards TypeSafe's request body unchanged, so the two providers differ only in
    // where the request goes and what the model is called there.
    [Fact]
    public void OpenRouter_serves_System_One_under_its_api_prefix()
    {
        JevProvider.OpenRouter.Endpoint.Should().Be("https://openrouter.ai/api/v1/systemone");
        JevProvider.OpenRouter.Model.Should().Be("typesafe/jev-1.13");
    }

    [Fact]
    public void TypeSafe_is_called_directly_on_its_own_host()
    {
        JevProvider.TypeSafe.Endpoint.Should().Be("https://api.typesafe.ai/v1/systemone");
        JevProvider.TypeSafe.Model.Should().Be("jev-1.13.0");
    }

    // A fitted estimator is only valid for the model version that produced its features, so a
    // floating alias would let the weights go stale silently the day the alias moves.
    [Fact]
    public void Neither_provider_uses_a_floating_model_alias()
    {
        JevProvider.All.Should().OnlyContain(p => !p.Model.Contains("latest"));
    }

    [Theory]
    [InlineData("openrouter")]
    [InlineData("typesafe")]
    public void Resolves_a_stored_provider_id(string id)
    {
        JevProvider.FromId(id)!.Id.Should().Be(id);
    }

    [Fact]
    public void An_unknown_provider_id_resolves_to_nothing()
    {
        JevProvider.FromId("anthropic").Should().BeNull();
    }

    [Fact]
    public void OpenRouter_is_listed_first_so_it_is_the_default()
    {
        JevProvider.All[0].Should().Be(JevProvider.OpenRouter);
    }
}
