namespace Heathen.HATE
{
    /// <summary>
    /// How an effect modifier combines with an attribute value (HATE-Spec §5.3). Maps onto a DataLens
    /// <c>SystemOp</c> when applied as a column System.
    /// </summary>
    public enum ModifierOp
    {
        /// <summary>value += magnitude.</summary>
        Add = 0,
        /// <summary>value *= magnitude.</summary>
        Multiply = 1,
        /// <summary>value = magnitude.</summary>
        Override = 2,
    }
}
