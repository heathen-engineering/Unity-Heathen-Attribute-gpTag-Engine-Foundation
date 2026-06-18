using Heathen.DataLens;

namespace Heathen.HATE
{
    /// <summary>
    /// How an ability folds its <see cref="Consideration"/>s into a single Utility score (HATE-Spec §8.2,
    /// design locked 2026-06-18: both modes, chosen per ability). Each consideration is one branchless
    /// DataLens curve pass over a metric column; the aggregate is the column op that accumulates them.
    /// </summary>
    public enum Aggregate
    {
        /// <summary>score = ∏ curve_c(metric_c). Any weak consideration drags the score toward 0 (veto
        /// semantics, IAUS-style). Accumulated with a Mul combine; <see cref="Consideration.Weight"/> is
        /// not used in this mode (flatten a curve to reduce a consideration's influence).</summary>
        Product = 0,

        /// <summary>score = Σ weight_c · curve_c(metric_c). Considerations trade off linearly. Accumulated
        /// with an Add combine; <see cref="Consideration.Weight"/> scales each term.</summary>
        WeightedSum = 1,
    }

    /// <summary>
    /// One consideration in an ability's utility score (HATE-Spec §8.2): a response <see cref="Curve"/>
    /// over a metric column, normalised over an input range, optionally weighted. Pure data — it compiles
    /// to a single uniform DataLens curve pass, so scoring stays contiguous and branchless.
    /// <para>
    /// The metric is a HATE <b>attribute</b> (its Current value): "preference rises as M rises" is a rising
    /// curve over M, "execute favours low health" is a falling (inverted) curve over Health, "only in range"
    /// is a steep threshold over a Distance attribute. External/derived metrics and bias are modelled the
    /// same way — declare an attribute and have an Effect or System write it (§8.3); no special channel.
    /// </para>
    /// </summary>
    public struct Consideration
    {
        /// <summary>The attribute whose Current value is the input metric.</summary>
        public int MetricAttr;
        /// <summary>The response curve (includes its own normalise range [Min,Max] and invert flag).</summary>
        public Curve Curve;
        /// <summary>Weight for <see cref="Aggregate.WeightedSum"/> (ignored under <see cref="Aggregate.Product"/>).</summary>
        public float Weight;

        public Consideration(int metricAttr, Curve curve, float weight = 1f)
        {
            MetricAttr = metricAttr;
            Curve = curve;
            Weight = weight;
        }
    }
}
