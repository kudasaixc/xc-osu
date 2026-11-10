namespace osu.Web
{
    /// <summary>Display stalls only: CPU work duration alone does not imply a missed presentation.</summary>
    public static class StutterCapturePolicy
    {
        public static bool ShouldCapture(double interval, double period, int repeated, int endedRun)
            => interval > 0 && interval <= 1000 &&
               (interval > period * 1.6 + 1 || repeated == 2 || endedRun >= 2);

        // A major stall may bypass the ten-second sampling cooldown; an active recording already
        // includes subsequent stalls, so it is never replaced or truncated by another trigger.
        public static bool IsSevere(double interval, double period, int repeated, int endedRun)
            => interval >= 100 || repeated * period >= 100 || endedRun * period >= 100;
    }
}
