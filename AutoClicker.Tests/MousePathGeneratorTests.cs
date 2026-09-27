using System.Drawing;
using Xunit;

namespace AutoClicker.Tests;

public class MousePathGeneratorTests
{
    [Fact]
    public void Path_starts_at_from_and_ends_exactly_at_to()
    {
        var pts = MousePathGenerator.Path(new Point(10, 20), new Point(400, 300), new Random(42)).ToList();

        Assert.NotEmpty(pts);
        Assert.Equal(new Point(10, 20), pts[0]);
        Assert.Equal(new Point(400, 300), pts[^1]); // runner snaps to the final point
    }

    [Fact]
    public void Path_terminates_and_contains_only_finite_points()
    {
        var pts = MousePathGenerator.Path(new Point(-500, -300), new Point(2500, 1800), new Random(7)).ToList();

        Assert.InRange(pts.Count, 2, 20000);
        foreach (Point p in pts)
        {
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
            Assert.False(double.IsInfinity(p.X) || double.IsInfinity(p.Y));
        }
    }

    [Fact]
    public void Path_is_deterministic_for_a_seeded_random()
    {
        var a = MousePathGenerator.Path(new Point(5, 5), new Point(500, 400), new Random(123)).ToList();
        var b = MousePathGenerator.Path(new Point(5, 5), new Point(500, 400), new Random(123)).ToList();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Path_for_identical_points_yields_only_that_point()
    {
        var pts = MousePathGenerator.Path(new Point(30, 40), new Point(30, 40), new Random(1)).ToList();

        Assert.Equal(new Point(30, 40), Assert.Single(pts));
    }
}
