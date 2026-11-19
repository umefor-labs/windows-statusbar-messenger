using MessengerBubbles.Core;

namespace MessengerBubbles.Tests;

public class GeometryTests
{
    static readonly Box Screen = new(0, 0, 1920, 1040);
    static readonly Box SecondScreen = new(1920, 0, 1280, 720);

    [Theory]
    [InlineData(100, 0)]
    [InlineData(1500, 1864)]
    public void SnapX_GoesToNearestEdge(double x, double expected) =>
        Assert.Equal(expected, Geometry.SnapX(new Box(x, 300, 56, 56), Screen));

    [Fact]
    public void SnapX_UsesTheBubblesOwnScreen() =>
        Assert.Equal(1920, Geometry.SnapX(new Box(2000, 300, 56, 56), SecondScreen));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1864, 0)]
    [InlineData(0, 984)]
    [InlineData(1864, 984)]
    public void PlacePanel_StaysInsideWorkArea(double x, double y)
    {
        var p = Geometry.PlacePanel(new Box(x, y, 56, 56), 380, 560, Screen);
        Assert.InRange(p.X, 0, 1920 - 380);
        Assert.InRange(p.Y, 0, 1040 - 560);
    }

    [Fact]
    public void PlacePanel_ShrinksToFitSmallScreen()
    {
        var small = new Box(0, 0, 1024, 600);
        var p = Geometry.PlacePanel(new Box(968, 100, 56, 56), 460, 720, small);
        Assert.Equal(600, p.H);
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void DismissTarget_CentersOnMark()
    {
        var (x, y) = Geometry.DismissTarget(new Box(100, 100, 56, 56), new Box(0, 0, 1000, 1000));
        Assert.Equal(472, x);
        Assert.Equal(902, y);
    }

    [Fact]
    public void PlacePanel_FromBottomBar_OpensAboveAvatar()
    {
        // Bar reserved below the work area (1040..1066).
        var p = Geometry.PlacePanel(new Box(300, 1043, 20, 20), 380, 560, Screen);
        Assert.Equal(300, p.X);
        Assert.Equal(1040 - 560 - 8, p.Y);
    }

    [Fact]
    public void PlacePanel_FromTopBar_OpensBelowAvatar()
    {
        var area = new Box(0, 26, 1920, 1014);
        var p = Geometry.PlacePanel(new Box(300, 3, 20, 20), 380, 560, area);
        Assert.Equal(26 + 8, p.Y);
    }

    [Fact]
    public void PlacePanel_FromBar_StaysInsideWorkAreaHorizontally()
    {
        var area = new Box(48, 0, 1872, 1040);
        var p = Geometry.PlacePanel(new Box(1900, 1043, 20, 20), 380, 560, area);
        Assert.Equal(48 + 1872 - 380, p.X);
    }

    [Theory]
    [InlineData(1043, true)]
    [InlineData(3, false)]
    [InlineData(1000, false)]
    public void IsOnBar_OnlyOutsideTheWorkArea(double y, bool expected)
        => Assert.Equal(expected, Geometry.IsOnBar(new Box(300, y, 20, 20), Screen));

    [Fact]
    public void IsOnBar_TopBar()
    {
        var area = new Box(0, 26, 1920, 1014);
        Assert.True(Geometry.IsOnBar(new Box(300, 3, 20, 20), area));
    }

    [Fact]
    public void PlaceDrawer_HugsLeftEdgeAtFullHeight()
    {
        var area = new Box(48, 0, 1872, 1014);
        Assert.Equal(new Box(48, 0, 380, 1014), Geometry.PlaceDrawer(380, area));
    }

    [Fact]
    public void PlacePreview_FromBottomBar_SitsInBottomLeftCorner()
    {
        var area = new Box(48, 0, 1872, 1014);
        Assert.Equal(new Box(48, 1014 - 56, 280, 56), Geometry.PlacePreview(new Box(60, 1017, 20, 20), 280, 56, area, null));
    }

    [Fact]
    public void PlacePreview_FromTopBar_SitsInTopLeftCorner()
    {
        var area = new Box(48, 26, 1872, 1014);
        Assert.Equal(new Box(48, 26, 280, 56), Geometry.PlacePreview(new Box(60, 3, 20, 20), 280, 56, area, null));
    }

    [Fact]
    public void PlacePreview_FlushBesideFullHeightDrawer()
    {
        var drawer = new Box(0, 0, 380, 1040);
        var p = Geometry.PlacePreview(new Box(10, 1043, 20, 20), 280, 56, Screen, drawer);
        Assert.Equal(new Box(380, 1040 - 56, 280, 56), p);
    }

    [Fact]
    public void PlacePanel_DoesNotCoverBubble()
    {
        var p = Geometry.PlacePanel(new Box(1864, 300, 56, 56), 380, 560, Screen);
        Assert.True(p.X + p.W <= 1864);
    }

    [Fact]
    public void RestoreOnScreen_OnScreenSaved_Unchanged()
    {
        var saved = new Box(2000, 100, 56, 56);
        Assert.Equal(saved, Geometry.RestoreOnScreen(saved, [Screen, SecondScreen]));
    }

    [Fact]
    public void RestoreOnScreen_OffScreenSaved_MovesToFirstWorkArea()
    {
        var r = Geometry.RestoreOnScreen(new Box(2000, 100, 56, 56), [Screen]);
        Assert.InRange(r.X, 0, 1920 - 56);
        Assert.InRange(r.Y, 0, 1040 - 56);
    }

    [Fact]
    public void FindFreeSlot_StartFree_ReturnsStart()
    {
        var start = new Box(1864, 300, 56, 56);
        Assert.Equal(start, Geometry.FindFreeSlot(start, [], Screen, 8));
    }

    [Fact]
    public void FindFreeSlot_Taken_GoesAbove()
    {
        var start = new Box(1864, 300, 56, 56);
        Assert.Equal(start with { Y = 236 }, Geometry.FindFreeSlot(start, [start], Screen, 8));
    }

    [Fact]
    public void FindFreeSlot_HoleInStack_StillGoesAboveTop()
    {
        var start = new Box(1864, 300, 56, 56);
        Assert.Equal(start with { Y = 172 }, Geometry.FindFreeSlot(start, [start with { Y = 236 }], Screen, 8));
    }

    [Fact]
    public void FindFreeSlot_NoRoomAbove_GoesBelow()
    {
        var start = new Box(1864, 10, 56, 56);
        Assert.Equal(start with { Y = 74 }, Geometry.FindFreeSlot(start, [start], Screen, 8));
    }

    [Fact]
    public void SpreadSlots_StacksUpwardWithoutOverlap()
    {
        var start = new Box(1864, 300, 56, 56);
        var slots = Geometry.SpreadSlots(start, 3, [], Screen, 8);
        Assert.Equal([300, 236, 172], slots.Select(s => s.Y));
    }

    [Fact]
    public void PlacePreview_NoPanel_SitsBesideBubble()
    {
        var bubble = new Box(1864, 300, 56, 56);
        Assert.Equal(Geometry.PlacePanel(bubble, 280, 56, Screen), Geometry.PlacePreview(bubble, 280, 56, Screen, null));
    }

    [Fact]
    public void PlacePreview_OverPanel_MovesAbovePanel()
    {
        var panel = new Box(1476, 200, 380, 560);
        var p = Geometry.PlacePreview(new Box(1864, 300, 56, 56), 280, 56, Screen, panel);
        Assert.Equal(136, p.Y);
    }

    [Fact]
    public void PlacePreview_PanelAtTop_MovesBelowPanel()
    {
        var panel = new Box(1476, 0, 380, 560);
        var p = Geometry.PlacePreview(new Box(1864, 300, 56, 56), 280, 56, Screen, panel);
        Assert.Equal(568, p.Y);
    }

    [Fact]
    public void FindFreeSlot_ColumnFull_MovesInward()
    {
        var start = new Box(1864, 950, 56, 56);
        var r = Geometry.FindFreeSlot(start, [new Box(1864, 0, 56, 1040)], Screen, 8);
        Assert.Equal(1800, r.X);
    }

    [Fact]
    public void FindFreeSlot_ScreenFull_FallsBackToStart()
    {
        var start = new Box(1864, 950, 56, 56);
        Assert.Equal(start, Geometry.FindFreeSlot(start, [Screen], Screen, 8));
    }

    [Theory]
    [InlineData(932, 950, true)]
    [InlineData(932, 300, false)]
    [InlineData(100, 950, false)]
    public void InDismissZone_OnlyNearBottomCentre(double x, double y, bool expected) =>
        Assert.Equal(expected, Geometry.InDismissZone(new Box(x, y, 56, 56), Screen));

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void HopOffset_IsAtRestAtStartMiddleAndEnd(double t) =>
        Assert.Equal(0, Geometry.HopOffset(t, 14), 6);

    [Fact]
    public void HopOffset_SecondHopIsLowerAndNothingGoesBelowRest()
    {
        var first = Enumerable.Range(1, 49).Max(i => Geometry.HopOffset(i / 100.0, 14));
        var second = Enumerable.Range(51, 49).Max(i => Geometry.HopOffset(i / 100.0, 14));
        Assert.InRange(first, 7, 14);
        Assert.True(second < first / 2);
        Assert.All(Enumerable.Range(0, 101), i => Assert.InRange(Geometry.HopOffset(i / 100.0, 14), 0, 14));
    }
}
