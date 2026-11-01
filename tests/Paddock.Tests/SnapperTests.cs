using Paddock.Core;
using Paddock.Interop;
using Xunit;

namespace Paddock.Tests;

public class SnapperTests
{
    static readonly RECT Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Rastet_am_linken_Bildschirmrand_ein()
    {
        var r = Snapper.SnapMove(new RECT(16, 200, 416, 500), [], [Work], threshold: 14, gap: 10);
        Assert.Equal(10, r.Left);
        Assert.Equal(400, r.Width);
    }

    [Fact]
    public void Rastet_neben_anderem_Bereich_mit_Abstand_ein()
    {
        var other = new RECT(100, 100, 500, 400);
        var r = Snapper.SnapMove(new RECT(515, 300, 815, 600), [other], [Work], 14, 10);
        Assert.Equal(510, r.Left);
    }

    [Fact]
    public void Ausserhalb_der_Schwelle_bleibt_alles_wie_es_ist()
    {
        var input = new RECT(300, 300, 600, 600);
        var r = Snapper.SnapMove(input, [], [Work], 14, 10);
        Assert.Equal(input, r);
    }

    [Fact]
    public void Groesse_aendern_rastet_nur_die_gezogene_Kante_ein()
    {
        // WMSZ_RIGHT = 2
        var r = Snapper.SnapSize(new RECT(16, 16, 1905, 400), 2, [], [Work], 14, 10);
        Assert.Equal(16, r.Left);
        Assert.Equal(1910, r.Right);
    }

    [Fact]
    public void Unsichtbarer_Bereich_wird_zurueckgeholt()
    {
        var r = Snapper.EnsureVisible(new RECT(5000, 5000, 5400, 5300), [Work]);
        Assert.True(r.Right <= Work.Right && r.Bottom <= Work.Bottom);
        Assert.Equal(400, r.Width);
    }
}
