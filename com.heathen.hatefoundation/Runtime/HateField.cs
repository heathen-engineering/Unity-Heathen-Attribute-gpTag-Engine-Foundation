namespace Heathen.HATE
{
    /// <summary>
    /// Which field of an attribute a duration effect targets (HATE-Spec §5.3 cap buffs). Most effects
    /// modify <see cref="Current"/> (the derived working value); cap buffs target <see cref="Max"/> or
    /// <see cref="Min"/> (e.g. "+10% Max Health for 30 ticks"). All three are recomputed from a persistent
    /// base each <c>RecomputeCurrent</c>, so duration cap buffs auto-expire normally — no reversal logic.
    /// (Permanent Base changes are <c>ApplyInstant</c>, not a duration field.)
    /// </summary>
    public enum HateField
    {
        /// <summary>The derived working value: <c>clamp(Base + ΣAdd)·ΠMul, Min, Max)</c>.</summary>
        Current = 0,
        /// <summary>The dynamic minimum cap (folded from MinBase + active Min-field effects).</summary>
        Min = 1,
        /// <summary>The dynamic maximum cap (folded from MaxBase + active Max-field effects).</summary>
        Max = 2,
    }
}
