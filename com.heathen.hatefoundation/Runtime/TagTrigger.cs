namespace Heathen.HATE
{
    /// <summary>How a <see cref="TagTrigger"/> tests an actor's CurrentTags bitmask against its mask.</summary>
    public enum TriggerMode
    {
        /// <summary>Pass if the actor has ALL the mask's bits ((tags &amp; mask) == mask).</summary>
        RequireAll = 0,
        /// <summary>Pass if the actor has ANY of the mask's bits ((tags &amp; mask) != 0).</summary>
        RequireAny = 1,
        /// <summary>Pass if the actor has NONE of the mask's bits ((tags &amp; mask) == 0).</summary>
        Exclude = 2,
    }

    /// <summary>
    /// A Trigger (HATE-Spec §6): a single tag-bitmask condition compiled to one DataLens bitmask
    /// predicate, batch-evaluated across all actors in a branchless System pass. (Multi-condition
    /// AND/OR trees need IR predicate trees — a later slice; for now a Trigger is one condition.)
    /// </summary>
    public readonly struct TagTrigger
    {
        public readonly int Mask;
        public readonly TriggerMode Mode;

        public TagTrigger(int mask, TriggerMode mode)
        {
            Mask = mask;
            Mode = mode;
        }

        public static TagTrigger RequireAll(int mask) => new TagTrigger(mask, TriggerMode.RequireAll);
        public static TagTrigger RequireAny(int mask) => new TagTrigger(mask, TriggerMode.RequireAny);
        public static TagTrigger Exclude(int mask) => new TagTrigger(mask, TriggerMode.Exclude);
    }
}
