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
        /// <summary>The magnitude (the fixed value, or the low end of a rolled range).</summary>
        public readonly double Magnitude;
        /// <summary>The high end of a rolled range; equal to <see cref="Magnitude"/> for a fixed modifier.</summary>
        public readonly double MagnitudeMax;
        /// <summary>When valid, the magnitude is injected by the caller under this tag (SetByCaller, §7.2/§10),
        /// falling back to <see cref="Magnitude"/>/range when the caller supplies no value. Invalid = not injected.</summary>
        public readonly GameplayTag SetByCaller;

        /// <summary>When valid, the magnitude is a Modifier Magnitude Calculation (MMC / attribute capture, §7.2):
        /// <c>Coefficient · captured + CaptureOffset</c>, where <c>captured</c> is this attribute read from the
        /// source (the caller's flashed record) or the target (<see cref="CaptureFromTarget"/>). Invalid = not MMC.
        /// Resolved at instant/periodic apply; the continuous fold uses <see cref="Magnitude"/> (the offset).</summary>
        public readonly GameplayTag CaptureAttribute;
        /// <summary>The MMC coefficient (multiplied by the captured attribute).</summary>
        public readonly double Coefficient;
        /// <summary>The MMC constant offset (added after the coefficient·captured term).</summary>
        public readonly double CaptureOffset;
        /// <summary>MMC capture source: the target entity's live attribute (true) or the caller's payload (false).</summary>
        public readonly bool CaptureFromTarget;

        // The full constructor is PRIVATE: GameplayTag has an implicit numeric conversion, so a public ctor with a
        // GameplayTag magnitude parameter would silently capture `new HateModifier(attr, op, 20)` (treating 20 as a
        // tag). The public forms below are unambiguous; SetByCaller / Captured are named factories.
        private HateModifier(GameplayTag attribute, HateOp op, double magnitude, double magnitudeMax,
            GameplayTag setByCaller, GameplayTag captureAttribute, double coefficient, double captureOffset, bool captureFromTarget)
        {
            Attribute = attribute;
            Op = op;
            Magnitude = magnitude;
            MagnitudeMax = magnitudeMax;
            SetByCaller = setByCaller;
            CaptureAttribute = captureAttribute;
            Coefficient = coefficient;
            CaptureOffset = captureOffset;
            CaptureFromTarget = captureFromTarget;
        }

        /// <summary>A fixed-magnitude modifier.</summary>
        public HateModifier(GameplayTag attribute, HateOp op, double magnitude)
            : this(attribute, op, magnitude, magnitude, default, default, 0, 0, false) { }

        /// <summary>A ranged modifier: the magnitude is rolled in <c>[min, max]</c> from the deterministic stream
        /// on each instant/periodic application (HATE-Spec §7.2) — e.g. a DOT that ticks 10-15 damage. The
        /// continuous fold (<see cref="HateWorld.RecomputeAttribute"/>) uses the low end.</summary>
        public HateModifier(GameplayTag attribute, HateOp op, double magnitudeMin, double magnitudeMax)
            : this(attribute, op, magnitudeMin, magnitudeMax, default, default, 0, 0, false) { }

        /// <summary>A caller-injected (SetByCaller) modifier: the magnitude is read from the invocation payload
        /// under <paramref name="setByCaller"/>, falling back to <paramref name="fallback"/> when absent (§7.2/§10).</summary>
        public static HateModifier Injected(GameplayTag attribute, HateOp op, GameplayTag setByCaller, double fallback = 0)
            => new HateModifier(attribute, op, fallback, fallback, setByCaller, default, 0, 0, false);

        /// <summary>A captured-magnitude (MMC) modifier: the magnitude is <c>coefficient · captured + offset</c>,
        /// where <paramref name="captureAttribute"/> is read from the caller's payload (default) or the target
        /// entity (<paramref name="fromTarget"/>) at apply time — e.g. "heal 50% of the caster's SpellPower".</summary>
        public static HateModifier Captured(GameplayTag attribute, HateOp op, GameplayTag captureAttribute,
            double coefficient = 1, double offset = 0, bool fromTarget = false)
            => new HateModifier(attribute, op, offset, offset, default, captureAttribute, coefficient, offset, fromTarget);
    }
}
