using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// An attribute declaration: a <see cref="GameplayTag"/> id, its fixed-width value type, and a default
    /// seeded on each new row. An attribute is just a column; a cap like <c>MaxHealth</c> is an ordinary
    /// attribute, not special machinery (HATE-Spec §4). Attributes are not character-only - a terrain region's
    /// <c>Fertility</c> or a weather front's <c>Intensity</c> are the same thing.
    /// </summary>
    public readonly struct HateAttribute
    {
        public readonly GameplayTag Id;
        public readonly DataLensValueType Type;
        public readonly double Default;

        public HateAttribute(GameplayTag id, DataLensValueType type, double @default = 0)
        {
            Id = id;
            Type = type;
            Default = @default;
        }
    }

    /// <summary>
    /// A trait declaration: a <see cref="GameplayTag"/> id, the attributes it bundles, and the capacity (max
    /// rows = max entities that can carry it). A trait is conceptual - it tells HATE these columns are
    /// index-aligned (one row per entity that has the trait). Attributes partition into exactly one trait.
    /// </summary>
    public sealed class HateTrait
    {
        public GameplayTag Id { get; }
        public int Capacity { get; }
        public IReadOnlyList<HateAttribute> Attributes { get; }

        public HateTrait(GameplayTag id, int capacity, params HateAttribute[] attributes)
        {
            Id = id;
            Capacity = capacity;
            Attributes = attributes ?? Array.Empty<HateAttribute>();
        }
    }

    /// <summary>
    /// A tall store declaration (Effects, Abilities): many rows per entity, each with an implicit
    /// <c>EntityIndex</c> column (the owning entity's catalog row) HATE adds automatically, plus the
    /// user-declared instance-state columns (the typed superset, §7). "All effects on entity N" is a scope
    /// predicate <c>WHERE EntityIndex == N</c>, not a join; a generic Trait System ticks one timer column in a
    /// single pass over the whole store.
    /// </summary>
    public sealed class HateTallStore
    {
        public GameplayTag Id { get; }
        public int Capacity { get; }
        public IReadOnlyList<HateAttribute> Columns { get; }

        public HateTallStore(GameplayTag id, int capacity, params HateAttribute[] columns)
        {
            Id = id;
            Capacity = capacity;
            Columns = columns ?? Array.Empty<HateAttribute>();
        }
    }

    /// <summary>
    /// The whole-world declaration: the traits, optional tall stores (effects/abilities), and the EntityCatalog
    /// capacity (max entities). HATE turns this into a <see cref="DataLensSchema"/> (a catalog store with one
    /// dereference index column per trait, a store per trait, and each tall store with its EntityIndex column)
    /// and rides a <see cref="Lens"/>; HATE owns no storage itself (HATE-Spec §5).
    /// </summary>
    public sealed class HateSchema
    {
        private HateTallStore[] _tallStores = Array.Empty<HateTallStore>();

        public int CatalogCapacity { get; }
        public IReadOnlyList<HateTrait> Traits { get; }
        public IReadOnlyList<HateTallStore> TallStores => _tallStores;

        /// <summary>Whether a dedicated Effects store was declared (§7).</summary>
        public bool HasEffects { get; private set; }
        public GameplayTag EffectsStore { get; private set; }
        public int EffectsCapacity { get; private set; }

        public HateSchema(int catalogCapacity, params HateTrait[] traits)
        {
            CatalogCapacity = catalogCapacity;
            Traits = traits ?? Array.Empty<HateTrait>();
        }

        /// <summary>Declare the tall stores (abilities, custom tall traits). Returns this for chaining.</summary>
        public HateSchema WithTallStores(params HateTallStore[] tallStores)
        {
            _tallStores = tallStores ?? Array.Empty<HateTallStore>();
            return this;
        }

        /// <summary>
        /// Declare the dedicated Effects store (a tall store HATE owns the schema of: EntityIndex + EffectTag +
        /// Duration + Stacks). Enables <see cref="HateWorld.DefineEffect"/>/<c>ApplyInstant</c>/<c>AddEffect</c>/
        /// <c>RecomputeAttribute</c>. Returns this for chaining.
        /// </summary>
        public HateSchema WithEffects(GameplayTag store, int capacity)
        {
            HasEffects = true;
            EffectsStore = store;
            EffectsCapacity = capacity;
            return this;
        }

        /// <summary>Whether a dedicated Abilities store was declared (§8).</summary>
        public bool HasAbilities { get; private set; }
        public GameplayTag AbilitiesStore { get; private set; }
        public int AbilitiesCapacity { get; private set; }

        /// <summary>
        /// Declare the dedicated Abilities store (EntityIndex + AbilityTag + Cooldown + Charges). Enables
        /// <see cref="HateWorld.GrantAbility"/>/<c>Activate</c>/<c>TickCooldowns</c>. Returns this for chaining.
        /// </summary>
        public HateSchema WithAbilities(GameplayTag store, int capacity)
        {
            HasAbilities = true;
            AbilitiesStore = store;
            AbilitiesCapacity = capacity;
            return this;
        }
    }
}
