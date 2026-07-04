using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>One attribute's live value on an entity (HATE-Spec §17 per-entity inspector).</summary>
    public readonly struct HateAttributeReading
    {
        public readonly GameplayTag Attribute;
        public readonly double Value;
        public HateAttributeReading(GameplayTag attribute, double value) { Attribute = attribute; Value = value; }
    }

    /// <summary>A trait an entity carries, with its attribute readings.</summary>
    public readonly struct HateTraitReading
    {
        public readonly GameplayTag Trait;
        public readonly HateAttributeReading[] Attributes;
        public HateTraitReading(GameplayTag trait, HateAttributeReading[] attributes) { Trait = trait; Attributes = attributes; }
    }

    /// <summary>An active effect row on an entity: its tag, remaining time and stack count.</summary>
    public readonly struct HateEffectReading
    {
        public readonly GameplayTag Effect;
        public readonly double Remaining;
        public readonly int Stacks;
        public HateEffectReading(GameplayTag effect, double remaining, int stacks) { Effect = effect; Remaining = remaining; Stacks = stacks; }
    }

    /// <summary>A granted ability row on an entity: its tag, available charges and recharge/cooldown timer.</summary>
    public readonly struct HateAbilityReading
    {
        public readonly GameplayTag Ability;
        public readonly int Charges;
        public readonly double Cooldown;
        public HateAbilityReading(GameplayTag ability, int charges, double cooldown) { Ability = ability; Charges = charges; Cooldown = cooldown; }
    }

    /// <summary>A granted tag on an entity (status / immunity), with its ref-count.</summary>
    public readonly struct HateTagReading
    {
        public readonly GameplayTag Tag;
        public readonly int Count;
        public HateTagReading(GameplayTag tag, int count) { Tag = tag; Count = count; }
    }

    /// <summary>
    /// A read-only snapshot of everything HATE knows about one entity (HATE-Spec §17 per-entity inspector):
    /// its traits + attribute values, active effects, granted abilities and granted tags. Purely diagnostic.
    /// </summary>
    public sealed class HateEntitySnapshot
    {
        public EntityId Entity;
        public bool Alive;
        public ulong ExternalRef;
        public HateTraitReading[] Traits = Array.Empty<HateTraitReading>();
        public HateEffectReading[] Effects = Array.Empty<HateEffectReading>();
        public HateAbilityReading[] Abilities = Array.Empty<HateAbilityReading>();
        public HateTagReading[] Tags = Array.Empty<HateTagReading>();
    }

    // Diagnostic read surface (HATE-Spec §17): a per-entity inspector for the Toolkit debugger. Reads through
    // Views, mutates nothing. Lives on HateWorld so it can address the effect/ability/tag columns HATE owns.
    public sealed partial class HateWorld
    {
        /// <summary>The schema this world was built from (traits, attributes, tall stores) - for tooling.</summary>
        public HateSchema Schema => _schema;

        /// <summary>
        /// Non-destructive enumeration of the world's live entities (for tooling / debuggers), resolved in a
        /// single catalog refresh (O(live), unlike scanning every catalog slot with <see cref="IsAlive"/>).
        /// Unlike <see cref="DrainSpawned()"/> this consumes nothing.
        /// </summary>
        public IReadOnlyList<EntityId> AliveEntities()
        {
            var list = new List<EntityId>();
            DataLensView cv = CatalogView();
            cv.Refresh();
            int n = cv.RowCount;
            for (int i = 0; i < n; i++)
            {
                long row = cv.SourceRow(i);
                if (row >= 0) list.Add(new EntityId((int)row));
            }
            return list;
        }

        /// <summary>
        /// Snapshot everything HATE knows about one entity (traits + attributes, effects, abilities, granted
        /// tags) for inspection. Diagnostic only - opens read Views and mutates nothing.
        /// </summary>
        public HateEntitySnapshot Inspect(EntityId entity)
        {
            var snap = new HateEntitySnapshot { Entity = entity, Alive = IsAlive(entity) };
            if (!snap.Alive) return snap;
            snap.ExternalRef = GetExternalRef(entity);

            var traits = new List<HateTraitReading>();
            foreach (HateTrait trait in _schema.Traits)
            {
                if (!HasTrait(entity, trait.Id)) continue;
                var attrs = new HateAttributeReading[trait.Attributes.Count];
                for (int a = 0; a < attrs.Length; a++)
                    attrs[a] = new HateAttributeReading(trait.Attributes[a].Id, GetAttribute(entity, trait.Attributes[a].Id));
                traits.Add(new HateTraitReading(trait.Id, attrs));
            }
            snap.Traits = traits.ToArray();

            if (_hasEffects) snap.Effects = ReadEffects(entity);
            if (_hasAbilities) snap.Abilities = ReadAbilities(entity);
            if (_hasGrantedTags) snap.Tags = ReadTags(entity);
            return snap;
        }

        private HateEffectReading[] ReadEffects(EntityId entity)
        {
            var res = new List<HateEffectReading>();
            foreach (GameplayTag store in _effectStores) // monolithic + type-split stores (§7.4)
            {
                EffCols c = _effCols[(ulong)store];
                using (DataLensView v = OpenStoreView(store,
                           new[] { c.Ei, c.Tag, c.Duration, c.Stacks },
                           new DataLensFilter().Eq(c.Ei, entity.Index)))
                {
                    v.Refresh();
                    for (int i = 0; i < v.RowCount; i++)
                        res.Add(new HateEffectReading(
                            new GameplayTag(v.Get<ulong>(i, c.Tag)),
                            v.Get<float>(i, c.Duration),
                            v.Get<int>(i, c.Stacks)));
                }
            }
            return res.ToArray();
        }

        private HateAbilityReading[] ReadAbilities(EntityId entity)
        {
            using (DataLensView v = OpenStoreView(_abilitiesStore,
                       new[] { _abEntityIndexCol, _abTagCol, _abCooldownCol, _abChargesCol },
                       new DataLensFilter().Eq(_abEntityIndexCol, entity.Index)))
            {
                v.Refresh();
                var res = new HateAbilityReading[v.RowCount];
                for (int i = 0; i < res.Length; i++)
                    res[i] = new HateAbilityReading(
                        new GameplayTag(v.Get<ulong>(i, _abTagCol)),
                        v.Get<int>(i, _abChargesCol),
                        v.Get<float>(i, _abCooldownCol));
                return res;
            }
        }

        private HateTagReading[] ReadTags(EntityId entity)
        {
            using (DataLensView v = OpenStoreView(_grantedStore,
                       new[] { _grEntityIndexCol, _grTagCol, _grRefCol },
                       new DataLensFilter().Eq(_grEntityIndexCol, entity.Index)))
            {
                v.Refresh();
                var res = new HateTagReading[v.RowCount];
                for (int i = 0; i < res.Length; i++)
                    res[i] = new HateTagReading(
                        new GameplayTag(v.Get<ulong>(i, _grTagCol)),
                        v.Get<int>(i, _grRefCol));
                return res;
            }
        }
    }
}
