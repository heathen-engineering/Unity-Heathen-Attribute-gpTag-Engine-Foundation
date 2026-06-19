using System;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// The user-declared value kind of a HATE attribute (HATE-Spec §4 type model). Drives the DataLens
    /// storage width, how the value is read/displayed, and validation. ("User" = the designer/dev authoring
    /// the game; the value at runtime belongs to a "player" actor.)
    /// </summary>
    public enum HateValueType
    {
        /// <summary>Whole numbers. HATE narrows storage to the smallest integer column that holds the
        /// declared [Min,Max] (unsigned when Min ≥ 0, signed otherwise).</summary>
        Integral = 0,

        /// <summary>32-bit IEEE float ("Single Precision"). The default for fractional values — compact and
        /// engine-native. Precision degrades as magnitude grows away from zero.</summary>
        SinglePrecision = 1,

        /// <summary>64-bit IEEE float ("Double Precision"). Opt in when range/precision far from zero
        /// demands it; 64-bit, so it does not disturb the data layout.</summary>
        DoublePrecision = 2,
    }

    /// <summary>
    /// A user-declared attribute (HATE-Spec §4): a <see cref="GameplayTag"/> identity + a value
    /// <see cref="HateValueType"/> + an inclusive <c>[Min,Max]</c> range. HATE does the smarts from this:
    /// it derives the DataLens column width (the smallest stride that holds the range — DataLens packs by
    /// byte length, not C# type), the logical value type (how the debugger displays + how hydration reads
    /// it via the matching GameplayTagCollection accessor), and range validation.
    /// </summary>
    public readonly struct HateAttribute
    {
        /// <summary>The attribute's tag identity (e.g. <c>Attributes.Health</c>).</summary>
        public readonly GameplayTag Tag;
        public readonly HateValueType Type;
        /// <summary>Inclusive minimum the value is validated/clamped to.</summary>
        public readonly double Min;
        /// <summary>Inclusive maximum the value is validated/clamped to.</summary>
        public readonly double Max;

        public HateAttribute(GameplayTag tag, HateValueType type, double min, double max)
        {
            if (max < min) (min, max) = (max, min); // tolerate swapped bounds
            Tag = tag; Type = type; Min = min; Max = max;
        }

        /// <summary>The smallest DataLens column type that stores this attribute's declared range. Integral
        /// narrows via <see cref="Column.SmallestUnsigned"/>/<see cref="Column.SmallestSigned"/>; fractional
        /// maps to Float (Single) or Double.</summary>
        public DataLensValueType StorageType
        {
            get
            {
                switch (Type)
                {
                    case HateValueType.SinglePrecision: return DataLensValueType.Float;
                    case HateValueType.DoublePrecision: return DataLensValueType.Double;
                    default:
                        return Min >= 0
                            ? Column.SmallestUnsigned((ulong)Math.Max(0d, Math.Ceiling(Max)))
                            : Column.SmallestSigned((long)Math.Floor(Min), (long)Math.Ceiling(Max));
                }
            }
        }

        /// <summary>The logical value type used to read/display the stored value: integral with Min ≥ 0 →
        /// Unsigned, Min &lt; 0 → Signed; fractional → Decimal. (Hydration picks the concrete accessor —
        /// GetFloat for Single, GetDouble for Double, GetInt/GetLong by integer width — from
        /// <see cref="StorageType"/>.)</summary>
        public GameplayTagValueType LogicalType => Type == HateValueType.Integral
            ? (Min >= 0 ? GameplayTagValueType.Unsigned : GameplayTagValueType.Signed)
            : GameplayTagValueType.Decimal;

        /// <summary>Clamp a value into the declared <c>[Min,Max]</c>.</summary>
        public double Clamp(double value) => value < Min ? Min : (value > Max ? Max : value);

        /// <summary>True when <paramref name="value"/> lies within the declared <c>[Min,Max]</c>.</summary>
        public bool IsValid(double value) => value >= Min && value <= Max;
    }
}
