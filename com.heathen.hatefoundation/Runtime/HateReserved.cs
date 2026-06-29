using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// The well-known attribute columns HATE's own machinery depends on (HATE-Spec §7). Effects and Abilities
    /// are each a single tall store whose width is the <b>superset</b> of the attributes you author; these
    /// reserved columns are always present and are how the built-in systems address the row. Everything is an
    /// attribute - the timer/cooldown the tick loop drives is just a reserved one looked up by tag, not a
    /// special slot. They are seeded into the superset automatically and surface as locked rows in the Forge.
    /// </summary>
    public static class HateReserved
    {
        /// <summary>The effect identity column (u64): which effect this row is (a GameplayTag hash).</summary>
        public static readonly GameplayTag EffectTag = GameplayTag.FromName("HATE.Effect.Tag");
        /// <summary>The effect's remaining duration (the tick loop subtracts dt; Expire frees rows at &lt;= 0).</summary>
        public static readonly GameplayTag EffectDuration = GameplayTag.FromName("HATE.Effect.Duration");
        /// <summary>The effect's stack count (scales aggregation contribution).</summary>
        public static readonly GameplayTag EffectStacks = GameplayTag.FromName("HATE.Effect.Stacks");

        /// <summary>The ability identity column (u64): which ability this row is (a GameplayTag hash).</summary>
        public static readonly GameplayTag AbilityTag = GameplayTag.FromName("HATE.Ability.Tag");
        /// <summary>The ability's remaining cooldown (the tick loop subtracts dt and clamps at 0).</summary>
        public static readonly GameplayTag AbilityCooldown = GameplayTag.FromName("HATE.Ability.Cooldown");
        /// <summary>The ability's remaining charges.</summary>
        public static readonly GameplayTag AbilityCharges = GameplayTag.FromName("HATE.Ability.Charges");
    }
}
