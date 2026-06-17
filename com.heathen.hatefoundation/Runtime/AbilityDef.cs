namespace Heathen.HATE
{
    /// <summary>
    /// An ability definition (HATE-Spec §7), expressed as data — parity with <c>UGameplayAbility</c>'s
    /// shape, minus OOP. The first P2 slice covers the activation spine: a <b>cost</b> (an Instant
    /// effect on a resource attribute), a <b>cooldown</b> (a duration effect that grants a cooldown tag
    /// gating re-use), an optional activation <b>requirement</b> (a Trigger over the actor's tags), an
    /// activation <b>cue</b>, and a delayed payload <b>task</b> (a single timed effect + cue, fired by
    /// <c>AdvanceAbilities</c>). Per-actor granting (AbilityInstances) and richer task types
    /// (wait-event / wait-attribute-change) come in later P2 slices.
    /// </summary>
    public readonly struct AbilityDef
    {
        /// <summary>Resource attribute spent on activation, or -1 for no cost.</summary>
        public readonly int CostAttr;
        /// <summary>Amount of <see cref="CostAttr"/> spent (subtracted from Base) on Commit.</summary>
        public readonly float CostAmount;
        /// <summary>Cooldown duration in ticks, or 0 for no cooldown.</summary>
        public readonly int CooldownTicks;
        /// <summary>Tag mask granted while on cooldown (gates re-use), or 0 for none.</summary>
        public readonly int CooldownTag;
        /// <summary>Optional activation requirement evaluated against the actor's CurrentTags; null = none.</summary>
        public readonly TagTrigger? Requirement;

        /// <summary>Cue emitted immediately on successful activation (e.g. cast start), or -1 for none.</summary>
        public readonly int ActivateCue;

        // ── Payload task (§7 step 3): a single delayed effect + cue, fired by AdvanceAbilities. ──
        /// <summary>Attribute the payload modifies, or -1 for no payload effect.</summary>
        public readonly int EffectAttr;
        /// <summary>How the payload modifies <see cref="EffectAttr"/>'s Base.</summary>
        public readonly ModifierOp EffectOp;
        /// <summary>Payload magnitude.</summary>
        public readonly float EffectMag;
        /// <summary>Ticks after activation at which the payload fires (0 = next AdvanceAbilities).</summary>
        public readonly int EffectDelayTicks;
        /// <summary>Cue emitted when the payload fires (e.g. impact), or -1 for none.</summary>
        public readonly int EffectCue;

        public AbilityDef(int costAttr = -1, float costAmount = 0f,
                          int cooldownTicks = 0, int cooldownTag = 0,
                          TagTrigger? requirement = null,
                          int activateCue = -1,
                          int effectAttr = -1, ModifierOp effectOp = ModifierOp.Add, float effectMag = 0f,
                          int effectDelayTicks = 0, int effectCue = -1)
        {
            CostAttr = costAttr;
            CostAmount = costAmount;
            CooldownTicks = cooldownTicks;
            CooldownTag = cooldownTag;
            Requirement = requirement;
            ActivateCue = activateCue;
            EffectAttr = effectAttr;
            EffectOp = effectOp;
            EffectMag = effectMag;
            EffectDelayTicks = effectDelayTicks;
            EffectCue = effectCue;
        }
    }
}
