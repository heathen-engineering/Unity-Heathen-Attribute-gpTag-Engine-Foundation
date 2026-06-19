namespace Heathen.HATE
{
    /// <summary>
    /// What happens to a stacking duration effect's remaining time when a new stack is applied
    /// (HATE-Spec §5.5). The stack count is bumped (capped at the limit) regardless; this controls
    /// the duration.
    /// </summary>
    public enum StackRefresh
    {
        /// <summary>Reset the duration to the new application's full length (the common "refresh on re-apply").</summary>
        RefreshDuration = 0,
        /// <summary>Keep whichever EndTick is later (existing vs the new application).</summary>
        KeepLongest = 1,
        /// <summary>Leave the existing EndTick unchanged (the original stack ticks down on its own clock).</summary>
        KeepExisting = 2,
    }
}
