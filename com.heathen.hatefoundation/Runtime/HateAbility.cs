using System;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// An ability's static definition (A) (HATE-Spec §8): the effects it applies on activation, its cooldown,
    /// charges, and an optional resource cost. Cost and cooldown are not special primitives in the full model
    /// (Cost = an Effect, Cooldown = a Condition); this slice models a simple resource cost + timer.
    /// <para>
    /// <b>Charges (GAS parity):</b> an ability holds up to <see cref="MaxCharges"/> charges (default 1).
    /// Activation consumes one charge and, if the recharge timer is idle, starts it; a charge is restored each
    /// time <see cref="WithCooldown"/> elapses, up to the maximum. So <see cref="Cooldown"/> is the
    /// <em>per-charge recharge time</em>, and an ability with multiple charges can fire again while others
    /// recharge. With the default single charge this is exactly the classic cooldown-gated ability.
    /// </para>
    /// Targeting input schema and Condition-composed gating come later.
    /// </summary>
    public sealed class HateAbilityDef
    {
        internal double Cooldown;
        internal int MaxCharges = 1;
        internal HateTargetMode TargetMode = HateTargetMode.Supplied;
        internal HateSourceMode SourceMode = HateSourceMode.Caster;   // who pays the cost (§8.1); default = the caster
        internal GameplayTag[] Effects = Array.Empty<GameplayTag>();
        internal GameplayTag CostResource;   // default (invalid) = free
        internal double CostAmount;
        internal HateCondition[] Requirements = Array.Empty<HateCondition>();
        internal GameplayTag[] SourceEffects = Array.Empty<GameplayTag>(); // cost effects applied to each resolved source

        public HateAbilityDef WithCooldown(double cooldown) { Cooldown = cooldown; return this; }
        /// <summary>Sets how the ability selects targets (default <see cref="HateTargetMode.Supplied"/>).</summary>
        public HateAbilityDef WithTargeting(HateTargetMode mode) { TargetMode = mode; return this; }
        /// <summary>Shorthand for a self-targeted ability (<see cref="HateTargetMode.Caster"/>).</summary>
        public HateAbilityDef SelfTargeted() { TargetMode = HateTargetMode.Caster; return this; }
        /// <summary>Sets who pays the cost — the source selector (§8.1; default <see cref="HateSourceMode.Caster"/>).
        /// <see cref="HateSourceMode.Supplied"/> pays from the invocation's source set (sacrifice / drain).</summary>
        public HateAbilityDef WithSource(HateSourceMode mode) { SourceMode = mode; return this; }
        /// <summary>Sets the maximum charges (clamped to at least 1). The cooldown becomes the per-charge recharge time.</summary>
        public HateAbilityDef WithCharges(int maxCharges) { MaxCharges = maxCharges < 1 ? 1 : maxCharges; return this; }
        public HateAbilityDef Applies(params GameplayTag[] effects) { Effects = effects ?? Array.Empty<GameplayTag>(); return this; }
        public HateAbilityDef Costs(GameplayTag resource, double amount) { CostResource = resource; CostAmount = amount; return this; }
        /// <summary>Eligibility conditions (all must hold against the caster) — the Condition half of §8's cost/cooldown model.</summary>
        public HateAbilityDef Requires(params HateCondition[] conditions) { Requirements = conditions ?? Array.Empty<HateCondition>(); return this; }
        /// <summary>Cost effects applied to each resolved <em>source</em> on activation (§8.1) — the Effect half:
        /// consume ammo/resource, stamp a cooldown timer, self-debuff, or drain a sacrificed source. With the
        /// default <see cref="HateSourceMode.Caster"/> the source is the caster.</summary>
        public HateAbilityDef AppliesToSource(params GameplayTag[] effects) { SourceEffects = effects ?? Array.Empty<GameplayTag>(); return this; }
        /// <summary>Alias of <see cref="AppliesToSource"/> kept for the common caster-pays case (default source mode).</summary>
        public HateAbilityDef AppliesToCaster(params GameplayTag[] effects) => AppliesToSource(effects);
    }

    /// <summary>
    /// A polled despawn event (HATE-Spec §14): carries the EntityId plus the engine handle (<see cref="ExternalRef"/>)
    /// captured before the row was freed, so an engine bridge can still destroy the right visual.
    /// </summary>
    public readonly struct HateDespawn
    {
        public readonly EntityId Entity;
        public readonly ulong ExternalRef;
        public HateDespawn(EntityId entity, ulong externalRef) { Entity = entity; ExternalRef = externalRef; }
    }
}
