using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// An eligibility check evaluated against a single entity (HATE-Spec §8): either an attribute comparison
    /// (<c>attribute op value</c>) or a granted-tag presence test. Abilities gate activation on a set of these
    /// (all must hold, see <see cref="HateAbilityDef.Requires"/>). Combined with cost <em>effects</em> applied
    /// to the caster (<see cref="HateAbilityDef.AppliesToCaster"/>), this is how cost, cooldown, ammo and their
    /// mixes compose from the same Condition + Effect pieces rather than being special primitives — e.g. ammo =
    /// <c>Requires(Attr(Ammo, GreaterEqual, 1))</c> + a cost effect that subtracts one; a global cooldown =
    /// <c>Requires(Attr(GcdTimer, LessEqual, 0))</c> + a cost effect that sets the timer.
    /// </summary>
    public struct HateCondition
    {
        internal enum Kind : byte { Attribute, HasTag, LacksTag }

        internal Kind      kind;
        internal GameplayTag tag;   // the attribute tag (Attribute) or the required/forbidden tag (HasTag/LacksTag)
        internal CompareOp op;      // Attribute kind only
        internal double    value;   // Attribute kind only

        /// <summary>Requires <c>GetAttribute(entity, attribute) op value</c> to hold.</summary>
        public static HateCondition Attr(GameplayTag attribute, CompareOp op, double value)
            => new HateCondition { kind = Kind.Attribute, tag = attribute, op = op, value = value };

        /// <summary>Requires the entity to have the given granted tag.</summary>
        public static HateCondition Has(GameplayTag tag) => new HateCondition { kind = Kind.HasTag, tag = tag };

        /// <summary>Requires the entity to NOT have the given granted tag (e.g. not Silenced/Stunned).</summary>
        public static HateCondition Lacks(GameplayTag tag) => new HateCondition { kind = Kind.LacksTag, tag = tag };
    }
}
