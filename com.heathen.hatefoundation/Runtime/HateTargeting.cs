using System;

namespace Heathen.HATE
{
    /// <summary>
    /// How an ability selects the entities its effects apply to (HATE-Spec §8, "targeting is HATE mechanism").
    /// HATE stores simulation state, not transforms, so spatial selection (nearest-N, point-radius) is performed
    /// by the game's own spatial system and handed to HATE as a supplied list — that is the <see cref="Supplied"/>
    /// path. HATE owns the deterministic mechanism of applying to the resolved set.
    /// </summary>
    public enum HateTargetMode
    {
        /// <summary>The ability targets its caster; any supplied targets are ignored (self-buffs, self-heals).</summary>
        Caster,
        /// <summary>The ability targets the entities supplied on the invocation (one, or a game-computed list).</summary>
        Supplied,
    }

    /// <summary>
    /// The invocation input an ability's user-defined schema carries (HATE-Spec §8): the supplied target set plus
    /// an optional point and opaque scalar params (swing velocity, button pressure, an injected roll). HATE does
    /// not interpret the point or params — they are carried to the resolver and the game's Entity Systems, which
    /// do. The target set drives <see cref="HateTargetMode.Supplied"/> targeting.
    /// </summary>
    public struct HateTargetInput
    {
        /// <summary>The supplied targets (ignored in <see cref="HateTargetMode.Caster"/> mode).</summary>
        public EntityId[] Targets;

        /// <summary>Whether <see cref="PointX"/>/<see cref="PointY"/>/<see cref="PointZ"/> carry a meaningful point.</summary>
        public bool HasPoint;
        /// <summary>A point in the game's space, carried uninterpreted by HATE.</summary>
        public double PointX, PointY, PointZ;

        /// <summary>Opaque invocation scalars the game fills and HATE carries (magnitude injection, input strength, …).</summary>
        public double Param0, Param1;

        /// <summary>An input targeting an explicit set of entities.</summary>
        public static HateTargetInput Of(params EntityId[] targets) => new HateTargetInput { Targets = targets };

        /// <summary>An input carrying a point (plus any explicit targets the game already resolved).</summary>
        public static HateTargetInput At(double x, double y, double z, params EntityId[] targets)
            => new HateTargetInput { HasPoint = true, PointX = x, PointY = y, PointZ = z, Targets = targets };
    }
}
