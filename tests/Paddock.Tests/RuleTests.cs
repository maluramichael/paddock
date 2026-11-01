using Paddock.Core;
using Paddock.Sdk;
using Xunit;

namespace Paddock.Tests;

public class RuleTests
{
    static ItemInfo File(string name, long size = 10, int ageDays = 0) =>
        new($@"C:\Desktop\{name}", name, false, size, DateTime.Now.AddDays(-ageDays));

    static IRuleCondition Cond(string id) => BuiltInConditions.All.Single(c => c.Id == id);

    [Theory]
    [InlineData("foto.PNG", ".png, .jpg", true)]
    [InlineData("foto.png", "png", true)]
    [InlineData("doc.pdf", ".png;.jpg", false)]
    public void Dateiendung(string name, string param, bool expected) =>
        Assert.Equal(expected, Cond("extension").Matches(File(name), param));

    [Theory]
    [InlineData("Rechnung_2026.pdf", "Rechnung*", true)]
    [InlineData("Screenshot 2026-11-01.png", "*screenshot*", true)]
    [InlineData("Brief.docx", "Rechnung*", false)]
    public void Namensmuster(string name, string param, bool expected) =>
        Assert.Equal(expected, Cond("name").Matches(File(name), param));

    [Fact]
    public void Dateiart_erkennt_Verknuepfungen_als_Programm() =>
        Assert.True(Cond("kind").Matches(File("Spiel.lnk"), "Programm"));

    [Fact]
    public void Ungueltiger_Regex_wirft_nicht() =>
        Assert.False(Cond("regex").Matches(File("a.txt"), "(["));

    [Fact]
    public void Aelter_als() =>
        Assert.True(Cond("olderThan").Matches(File("alt.txt", ageDays: 40), "30"));

    [Fact]
    public void Beispiel_Plugin_Groesser_als()
    {
        IRuleCondition? cond = null;
        var host = new FakeHost(c => cond = c);
        new Paddock.Plugins.Examples.ExamplesPlugin().Initialize(host);
        Assert.NotNull(cond);
        Assert.True(cond!.Matches(File("film.mkv", size: 300L * 1024 * 1024), "100"));
        Assert.False(cond.Matches(File("klein.txt", size: 1024), "1,5"));
    }

    sealed class FakeHost(Action<IRuleCondition> onCondition) : IPaddockHost
    {
        public Version HostVersion => new(1, 0);
        public string DataDirectory => Path.GetTempPath();
        public IReadOnlyList<IFenceInfo> Fences => [];
        public void RegisterRuleCondition(IRuleCondition condition) => onCondition(condition);
        public void RegisterFenceCommand(IFenceCommand command) { }
        public void RegisterItemCommand(IItemCommand command) { }
        public void Notify(string title, string message) { }
        public void Log(string message) { }
        public void Refresh(Guid fenceId) { }
    }
}
