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
    /// A type-split effect store (HATE-Spec §7.4): a tag subtree (e.g. <c>HATE.Effect.DOT</c>) whose effects live
    /// in their own narrow-dense store rather than the monolithic Effects store. Its columns are the reserved
    /// machinery (EntityIndex + EffectTag + Duration + Stacks) plus only the attributes this type uses.
    /// </summary>
    public sealed class HateEffectType
    {
        public GameplayTag Subtree { get; }
        public GameplayTag Store { get; }
        public int Capacity { get; }
        public IReadOnlyList<HateAttribute> Attributes { get; }

        public HateEffectType(GameplayTag subtree, GameplayTag store, int capacity, params HateAttribute[] attributes)
        {
            Subtree = subtree;
            Store = store;
            Capacity = capacity;
            Attributes = attributes ?? Array.Empty<HateAttribute>();
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

        private HateAttribute[] _effectAttributes = Array.Empty<HateAttribute>();
        private HateAttribute[] _abilityAttributes = Array.Empty<HateAttribute>();

        /// <summary>Whether a dedicated Effects store was declared (§7).</summary>
        public bool HasEffects { get; private set; }
        public GameplayTag EffectsStore { get; private set; }
        public int EffectsCapacity { get; private set; }

        /// <summary>The authored effect-attribute superset: the columns of the single Effects store, on top of
        /// the reserved machinery columns (<see cref="HateReserved.EffectDuration"/> etc.). Every effect row
        /// carries them all; an effect simply leaves the ones it does not use at default (HATE-Spec §7).</summary>
        public IReadOnlyList<HateAttribute> EffectAttributes => _effectAttributes;

        /// <summary>The authored ability-attribute superset: the columns of the single Abilities store.</summary>
        public IReadOnlyList<HateAttribute> AbilityAttributes => _abilityAttributes;

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
        public HateSchema WithEffects(GameplayTag store, int capacity, params HateAttribute[] attributes)
        {
            HasEffects = true;
            EffectsStore = store;
            EffectsCapacity = capacity;
            _effectAttributes = attributes ?? Array.Empty<HateAttribute>();
            return this;
        }

        private HateEffectType[] _effectTypes = Array.Empty<HateEffectType>();

        /// <summary>The declared type-split effect stores (§7.4); empty = the single monolithic Effects store.</summary>
        public IReadOnlyList<HateEffectType> EffectTypes => _effectTypes;

        /// <summary>
        /// Split a class of effects (a tag subtree, e.g. <c>HATE.Effect.DOT</c>) into its own narrow-dense store
        /// instead of the monolithic <see cref="WithEffects"/> store (HATE-Spec §7.4). Effects whose tag is that
        /// subtree or a descendant route to this store; all others stay in the monolithic store. Its columns are
        /// the reserved machinery (EntityIndex + EffectTag + Duration + Stacks) plus the <paramref name="attributes"/>
        /// this type uses, so the store is dense. Type-ops, ticking and recompute span every effect store
        /// automatically; the only cost is that "all effects on this entity" becomes a gather across stores.
        /// Declare more specific subtrees first when they nest (first at-or-under match wins). Requires
        /// <see cref="WithEffects"/>. Returns this for chaining.
        /// </summary>
        public HateSchema WithEffectType(GameplayTag subtree, GameplayTag store, int capacity, params HateAttribute[] attributes)
        {
            var list = new List<HateEffectType>(_effectTypes) { new HateEffectType(subtree, store, capacity, attributes) };
            _effectTypes = list.ToArray();
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
        public HateSchema WithAbilities(GameplayTag store, int capacity, params HateAttribute[] attributes)
        {
            HasAbilities = true;
            AbilitiesStore = store;
            AbilitiesCapacity = capacity;
            _abilityAttributes = attributes ?? Array.Empty<HateAttribute>();
            return this;
        }

        /// <summary>Whether a dedicated GrantedTags store was declared (§7.3).</summary>
        public bool HasGrantedTags { get; private set; }
        public GameplayTag GrantedTagsStore { get; private set; }
        public int GrantedTagsCapacity { get; private set; }

        /// <summary>
        /// Declare the dedicated GrantedTags store (EntityIndex + Tag + RefCount): the backbone for granted
        /// tags and immunity (HATE-Spec §7.3). An active effect grants a tag on an entity for its lifetime
        /// (<c>State.Stunned</c>); immunity is the same primitive read as a gate (an incoming effect whose
        /// classification tags overlap the target's granted tags is blocked). Ref-counted so overlapping
        /// grants compose. Enables <see cref="HateWorld.GrantTag"/>/<c>RevokeTag</c>/<c>HasTag</c>/
        /// <c>HasAnyTag</c>/<c>TryApplyEffectInstant</c>. Returns this for chaining.
        /// </summary>
        public HateSchema WithGrantedTags(GameplayTag store, int capacity)
        {
            HasGrantedTags = true;
            GrantedTagsStore = store;
            GrantedTagsCapacity = capacity;
            return this;
        }
    }
}
