using System.Xml.Linq;
using FluentAssertions;
using TmTimeTracker.UI;
using Xunit;

namespace TmTimeTracker.Tests.UI;

public class WindowsToastNotifierTests
{
    // scenario="urgent" is the whole reason this class builds XML by hand instead of using
    // ToastContentBuilder, so it is the thing most worth pinning down.
    [Fact]
    public void Marks_urgent_notifications_with_the_urgent_scenario()
    {
        var xml = XDocument.Parse(WindowsToastNotifier.BuildXml("t", "b", urgent: true));

        xml.Root!.Attribute("scenario")!.Value.Should().Be("urgent");
    }

    [Fact]
    public void Leaves_the_scenario_off_ordinary_notifications()
    {
        var xml = XDocument.Parse(WindowsToastNotifier.BuildXml("t", "b", urgent: false));

        xml.Root!.Attribute("scenario").Should().BeNull();
    }

    [Fact]
    public void Carries_the_title_and_body_as_the_two_text_elements()
    {
        var xml = XDocument.Parse(WindowsToastNotifier.BuildXml("Title", "Body", urgent: true));

        xml.Descendants("text").Select(t => t.Value)
            .Should().Equal("Title", "Body");
    }

    // A ticket summary is free text from Jira and lands straight in the title. Unescaped, an
    // ampersand alone would make the document unparseable and the notification would never show.
    [Fact]
    public void Escapes_markup_in_the_text_it_is_given()
    {
        var raw = WindowsToastNotifier.BuildXml("A & B", "<script>x</script>", urgent: false);

        var xml = XDocument.Parse(raw);
        xml.Descendants("text").Select(t => t.Value)
            .Should().Equal("A & B", "<script>x</script>");
    }
}
