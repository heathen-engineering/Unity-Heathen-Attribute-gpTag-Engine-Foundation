using System;

namespace Heathen.HATE
{
    /// <summary>
    /// An entity handle: simply the entity's row index in the EntityCatalog (HATE-Spec §5). Nothing more.
    /// HATE deliberately bakes in no generation/slot packing: stale-handle safety, when a game needs it, is
    /// opt-in and authored as an ordinary trait (e.g. an <c>AddressableActor</c> trait with a generation
    /// column validated by the game's own systems). An Entity is not necessarily an actor; it may be a crate,
    /// a weather front, a terrain region, a map - anything with attributes and tags.
    /// <para>
    /// The index is a <see cref="ulong"/> (registry-natural, <c>size_t</c>-aligned, collision headroom) rather
    /// than a signed int; the null handle is <see cref="ulong.MaxValue"/> (there is no valid "-1" row). Note this
    /// is HATE's own concept - DataLens has no notion of an entity - so the width is HATE's choice; the tall
    /// stores' <c>EntityIndex</c> linking columns are the unsigned 64-bit companion.
    /// </para>
    /// </summary>
    public readonly struct EntityId : IEquatable<EntityId>
    {
        /// <summary>The EntityCatalog row index.</summary>
        public readonly ulong Index;

        public EntityId(ulong index) { Index = index; }

        /// <summary>The null handle (no entity).</summary>
        public static readonly EntityId None = new EntityId(ulong.MaxValue);

        public bool IsValid => Index != ulong.MaxValue;

        public bool Equals(EntityId other) => Index == other.Index;
        public override bool Equals(object obj) => obj is EntityId e && Equals(e);
        public override int GetHashCode() => Index.GetHashCode();
        public override string ToString() => IsValid ? $"Entity({Index})" : "Entity(None)";
        public static bool operator ==(EntityId a, EntityId b) => a.Index == b.Index;
        public static bool operator !=(EntityId a, EntityId b) => a.Index != b.Index;
    }
}
