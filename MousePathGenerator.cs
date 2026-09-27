using System.Drawing;

namespace AutoClicker;

/// <summary>
/// Generates natural, human-like cursor paths for foreground actions. WindMouse-style:
/// the cursor is pulled toward the target by a "gravity" force while a randomized "wind"
/// force pushes it sideways, with the per-step velocity clamped so it can't rocket across
/// the screen. The result is an arcing path that decelerates as it closes in on the
/// target, instead of the straight-line teleport <c>SetCursorPos</c> would produce.
/// </summary>
/// <remarks>
/// Reimplemented from the published WindMouse concept (BenLand100) rather than copied:
/// only the idea — gravity toward the target plus damped random wind, velocity clamping —
/// is reproduced here. The constants are tuned for typical 20-120 ms glide distances.
/// </remarks>
internal static class MousePathGenerator
{
    // sqrt(3) precomputed: each step's wind is the previous wind damped by 1/sqrt(3) plus
    // a fresh random impulse. Damping keeps the path from becoming pure random walk.
    private const double WindDamping = 1.7320508075688772;
    private const int MaxSteps = 20000; // hard termination guard, far beyond any real path

    /// <summary>
    /// Yields the waypoints of a natural path from <paramref name="from"/> to
    /// <paramref name="to"/>, both inclusive. The first point is always
    /// <paramref name="from"/> and the last is always exactly <paramref name="to"/> —
    /// the caller snaps to the final point, so the path must end there.
    /// </summary>
    public static IEnumerable<Point> Path(Point from, Point to, Random rng)
    {
        yield return from;
        if (from == to) yield break;

        double windX = 0, windY = 0, velX = 0, velY = 0;
        double px = from.X, py = from.Y;

        for (int step = 0; step < MaxSteps; step++)
        {
            double dx = to.X - px;
            double dy = to.Y - py;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= 1.0) break; // close enough: the final snap below lands exactly

            // Damped random wind plus gravity toward the target (stronger when far away).
            windX = windX / WindDamping + (rng.NextDouble() * 2.0 - 1.0) * 2.0;
            windY = windY / WindDamping + (rng.NextDouble() * 2.0 - 1.0) * 2.0;
            velX += windX + dx / dist * 3.0;
            velY += windY + dy / dist * 3.0;

            // Clamp the step: proportional to remaining distance, with sane bounds.
            double maxStep = Math.Clamp(dist / 3.0, 5.0, 60.0);
            double speed = Math.Sqrt(velX * velX + velY * velY);
            if (speed > maxStep)
            {
                velX = velX / speed * maxStep;
                velY = velY / speed * maxStep;
            }

            px += velX;
            py += velY;

            int ix = (int)Math.Round(px);
            int iy = (int)Math.Round(py);
            if (ix == from.X && iy == from.Y) continue; // never yield the start twice
            yield return new Point(ix, iy);
        }

        yield return to; // the runner snaps the final point exactly
    }
}
