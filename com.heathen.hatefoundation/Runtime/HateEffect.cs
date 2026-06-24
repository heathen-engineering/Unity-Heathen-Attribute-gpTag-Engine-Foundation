using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>How a modifier combines with an attribute (HATE-Spec §7.2). Divide is Multiply(1/x).</summary>
    public enum HateOp { Add, Multiply, Override }

    /// <summary>Reapply policy for a duration effect (HATE-Spec §7.3); HATE prescribes no stacking model.</summary>
    public enum HateReapply
    {
        /// <summary>Always insert a new row (N rows).</summary>
        Instance,
        /// <summary>Find the (entity, tag) row and reset its timer (one row max).</summary>
        Refresh,
        /// <summary>Find the (entity, tag) row and increment its stacks (one row).</summary>
        Stack,
    }

    /// <summary>
    /// One attribute modifier in an effect's static definition (A): an <see cref="HateOp"/> of
    /// <see cref="Magnitude"/> against a target attribute (HATE-Spec §7.2). An effect is a GameplayTag plus a
    /// bag of these; HATE folds the active ones over the hydrated records, DataLens never interprets them.
    /// </summary>
    public readonly struct HateModifier
    {
        public readonly GameplayTag Attribute;
        public readonly HateOp Op;
        public readonly double Magnitude;

        public HateModifier(GameplayTag attribute, HateOp op, double magnitude)
        {
            Attribute = attribute;
            Op = op;
            Magnitude = magnitude;
        }
    }
}
