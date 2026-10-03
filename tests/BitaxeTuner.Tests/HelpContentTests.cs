using BitaxeTuner.Core.Help;
using BitaxeTuner.Core.Monitoring;
using Xunit;

namespace BitaxeTuner.Tests;

public class HelpContentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_section_has_content(bool server)
    {
        var sections = HelpContent.Sections(server);
        Assert.Equal(4, sections.Count);
        Assert.All(sections, s =>
        {
            Assert.NotEmpty(s.Items);
            Assert.All(s.Items, i => { Assert.False(string.IsNullOrWhiteSpace(i.Title)); Assert.False(string.IsNullOrWhiteSpace(i.Text)); });
        });
        Assert.Equal(sections.SelectMany(s => s.Items.Select(i => s.Id + i.Title)).Count(),
            sections.SelectMany(s => s.Items.Select(i => s.Id + i.Title)).Distinct().Count());
    }

    [Fact]
    public void Journal_help_explains_every_category()
    {
        var all = EventExplanations.All().ToList();
        Assert.All(EventCategories.All, c => Assert.Contains(all, x => x.Category == c));
        var journal = HelpContent.Sections(server: true).Single(s => s.Id == "journal");
        Assert.Equal(all.Count, journal.Items.Count);
    }

    [Fact]
    public void Miner_log_help_lists_the_default_alert_patterns()
    {
        var logs = HelpContent.Sections(server: false).Single(s => s.Id == "minerlog");
        var text = string.Join("\n", logs.Items.Select(i => i.Text));
        Assert.All(new BitaxeTuner.Core.Config.LogAlertSettings().Patterns, p => Assert.Contains(p, text));
    }
}
