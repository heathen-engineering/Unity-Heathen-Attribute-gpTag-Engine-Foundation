using System;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// An ability's static definition (A) (HATE-Spec §8): the effects it applies on activation, its cooldown,
    /// and an optional resource cost. Cost and cooldown are not special primitives in the full model (Cost = an
    /// Effect, Cooldown = a Condition); this slice models a simple resource cost + timer to prove the
    /// grant/cooldown/activate loop. Targeting input schema, charges and Condition-composed gating come later.
    /// </summary>
    public sealed class HateAbilityDef
    {
        internal double Cooldown;
        internal GameplayTag[] Effects = Array.Empty<GameplayTag>();
        internal GameplayTag CostResource;   // default (invalid) = free
        internal double CostAmount;

        public HateAbilityDef WithCooldown(double cooldown) { Cooldown = cooldown; return this; }
        public HateAbilityDef Applies(params GameplayTag[] effects) { Effects = effects ?? Array.Empty<GameplayTag>(); return this; }
        public HateAbilityDef Costs(GameplayTag resource, double amount) { CostResource = resource; CostAmount = amount; return this; }
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
