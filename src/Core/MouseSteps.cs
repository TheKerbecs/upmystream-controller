using System;
using System.Collections.Generic;

namespace GameController;

/// <summary>A smooth relative mouse move: small steps on an ease-in-out curve that add up to exactly (dx, dy).</summary>
public static class MouseSteps
{
    public const int StepMs = 2;

    public struct Step
    {
        public int AtMs, Dx, Dy;
    }

    public static List<Step> Plan(int dx, int dy, int durationMs)
    {
        var steps = new List<Step>();
        var n = Math.Max(1, durationMs / StepMs);
        int sentX = 0, sentY = 0;
        for (var i = 1; i <= n; i++)
        {
            var t = (double)i / n;
            var eased = t * t * (3 - 2 * t); // smoothstep: slow start, slow end
            var x = (int)Math.Round(dx * eased);
            var y = (int)Math.Round(dy * eased);
            if (x == sentX && y == sentY && i < n) continue; // nothing to move yet
            steps.Add(new Step { AtMs = n == 1 ? 0 : (int)Math.Round((double)durationMs * i / n), Dx = x - sentX, Dy = y - sentY });
            sentX = x;
            sentY = y;
        }
        return steps;
    }
}
