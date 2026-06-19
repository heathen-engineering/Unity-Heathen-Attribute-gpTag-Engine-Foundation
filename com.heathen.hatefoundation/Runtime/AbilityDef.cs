using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// An ability definition (HATE-Spec §7), expressed as data — parity with <c>UGameplayAbility</c>'s
    /// shape, minus OOP. Addressed by its own <see cref="Id"/> GameplayTag (e.g. <c>Abilities.Fireball</c>).
    /// The activation spine: a <b>cost</b> (an Instant effect on a resource attribute), a <b>cooldown</b>
    /// (a duration effect that grants a cooldown status tag gating re-use), an optional activation
    /// <b>requirement</b> (a <see cref="GameplayTagCondition"/> set compiled to a DataLens predicate — NOT
    /// per-actor evaluated), an activation <b>cue</b>, and a delayed payload <b>task</b> (a single timed
    /// effect + cue, fired by <c>AdvanceAbilities</c>).
    /// <para>
    /// All identities are <see cref="GameplayTag"/>s; an unset/invalid tag (<c>default</c>, Id 0) means
    /// "none" (no cost / no cooldown tag / no cue / no payload attribute).
    /// </para>
    /// </summary>
    public readonly struct AbilityDef
    {
        /// <summary>This ability's tag identity (e.g. <c>Abilities.Fireball</c>) — how it is addressed.</summary>
        public readonly GameplayTag Id;

        /// <summary>Resource attribute spent on activation, or <c>default</c> (invalid) for no cost.</summary>
        public readonly GameplayTag CostAttr;
        /// <summary>Amount of <see cref="CostAttr"/> spent (subtracted from Base) on Commit.</summary>
        public readonly float CostAmount;
        /// <summary>Cooldown duration in ticks, or 0 for no cooldown.</summary>
        public readonly int CooldownTicks;
        /// <summary>Status tag granted while on cooldown (gates re-use), or <c>default</c> for none.</summary>
        public readonly GameplayTag CooldownTag;
        /// <summary>Optional activation requirement: a GameplayTagCondition set compiled to a DataLens
        /// predicate over the actor's status/attributes; null/empty = no requirement.</summary>
        public readonly GameplayTagCondition[] Requirement;

        /// <summary>Cue emitted immediately on successful activation (e.g. cast start), or <c>default</c> for none.</summary>
        public readonly GameplayTag ActivateCue;

        // ── Payload task (§7 step 3): a single delayed effect + cue, fired by AdvanceAbilities. ──
        /// <summary>Attribute the payload modifies, or <c>default</c> for no payload effect.</summary>
        public readonly GameplayTag EffectAttr;
        /// <summary>How the payload modifies <see cref="EffectAttr"/>'s Base.</summary>
        public readonly ModifierOp EffectOp;
        /// <summary>Payload magnitude.</summary>
        public readonly float EffectMag;
        /// <summary>Ticks after activation at which the payload fires (0 = next AdvanceAbilities).</summary>
        public readonly int EffectDelayTicks;
        /// <summary>Cue emitted when the payload fires (e.g. impact), or <c>default</c> for none.</summary>
        public readonly GameplayTag EffectCue;

        public AbilityDef(GameplayTag id = default,
                          GameplayTag costAttr = default, float costAmount = 0f,
                          int cooldownTicks = 0, GameplayTag cooldownTag = default,
                          GameplayTagCondition[] requirement = null,
                          GameplayTag activateCue = default,
                          GameplayTag effectAttr = default, ModifierOp effectOp = ModifierOp.Add, float effectMag = 0f,
                          int effectDelayTicks = 0, GameplayTag effectCue = default)
        {
            Id = id;
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
