using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// A game-facing read/write view over the entities that carry a set of traits — the runtime realisation of a
    /// <b>Result Set</b> (the EntityCatalog joined to those trait stores by the catalog's per-trait index columns).
    /// A trait attribute reads and writes straight through to its store via the same dereference the spawn/master
    /// views use, so <see cref="DataLensView.Commit"/> propagates a change back to the source (Coding Law 4: the
    /// store is reached only through the view). Ask for one entity's flat record with <see cref="Record"/>; the
    /// <see cref="DataViewRecord"/> reads/writes its fields by tag. This is what a generated per-archetype view and
    /// its typed interface ride.
    /// </summary>
    public sealed class HateEntityView : IDisposable
    {
        private readonly Lens _lens;
        private readonly DataLensView _view;

        internal HateEntityView(Lens lens, DataLensView view) { _lens = lens; _view = view; }

        /// <summary>The underlying DataLens view (advanced use).</summary>
        public DataLensView View => _view;

        /// <summary>Number of in-scope entity rows currently hydrated.</summary>
        public int Count => _view.RowCount;

        /// <summary>
        /// Set the refresh <b>frequency</b>: the Lens re-hydrates this view every <paramref name="period"/> ticks
        /// (with an optional <paramref name="phase"/> offset) as part of its own orchestration — the intended,
        /// hands-off path, so game code just reads the current record and never pulls the data itself.
        /// </summary>
        public void Schedule(ulong period, ulong phase = 0) => _lens.Schedule(_view, period, phase);

        /// <summary>
        /// Re-hydrate the snapshot now — the explicit path, for when you are not driving it off a scheduled
        /// frequency (fine for a handful of referential entities).
        /// </summary>
        public void Refresh() => _view.Refresh();

        /// <summary>
        /// The flat record for one entity — its row in this view — or an invalid <see cref="DataViewRecord"/> when
        /// the entity is not in scope. O(rows) today (the master-view scan); trivial for the few referential
        /// entities this is meant for.
        /// </summary>
        public DataViewRecord Record(EntityId entity)
        {
            int n = _view.RowCount;
            for (int i = 0; i < n; i++)
                if (_view.SourceRow(i) == entity.Index)
                    return new DataViewRecord(_view, i);
            return default;
        }

        /// <summary>The flat record at a hydrated row index — for iterating the whole in-scope set.</summary>
        public DataViewRecord RecordAt(int row) => new DataViewRecord(_view, row);

        public void Dispose() => _view?.Dispose();
    }

    public sealed partial class HateWorld
    {
        /// <summary>
        /// Create a game-facing read/write view over the entities carrying these traits — a <b>Result Set</b>
        /// (catalog + those trait stores). Trait attributes commit straight back to their store. This is the same
        /// composed catalog→trait dereference used for spawning and the master view, exposed as a durable,
        /// per-entity-addressable surface for game code and generated accessors.
        /// </summary>
        /// <remarks>
        /// <paramref name="scope"/> is the Scope predicate (the <c>Where</c> filter that rules an entity record
        /// in/out of the primary view); null = all entities carrying the traits. Set a refresh frequency with
        /// <see cref="HateEntityView.Schedule"/>. Child Ability/Effect Result Sets are the separate
        /// <see cref="CreateAbilityView"/> scoped to the entities of interest.
        /// </remarks>
        public HateEntityView CreateEntityView(IReadOnlyList<GameplayTag> traits,
            Func<DataLensFilter, DataLensPredicate> scope = null)
        {
            if (traits == null || traits.Count == 0)
                throw new ArgumentException("CreateEntityView requires at least one trait.");

            var from = new DataLensFrom(_catalogTag);
            var select = new List<GameplayTag>();
            var readOnly = new List<bool>();
            foreach (GameplayTag trait in traits)
            {
                if (!_catIndexTag.TryGetValue((ulong)trait, out GameplayTag ci))
                    throw new ArgumentException($"CreateEntityView references unknown trait {(ulong)trait}.");
                from.Dereference(into: trait, via: ci);
                select.Add(ci); readOnly.Add(true);   // project the index column read-only (don't rewrite links on an attribute update)
                foreach (HateAttribute a in _traitOf[(ulong)trait].Attributes) { select.Add(a.Id); readOnly.Add(false); }
            }
            if (scope != null) from.Where(scope);

            return new HateEntityView(_lens, _lens.View(from, select.ToArray(), readOnly.ToArray()));
        }

        /// <summary>
        /// Create a child <b>Ability</b> Result Set — a view over the tall abilities store (each row a granted
        /// ability: its owning entity, tag, cooldown and charges). This is the "carry child rows" half of an entity
        /// view: get one entity's ability record with <see cref="HateChildView.Record"/> and read
        /// <see cref="HateReserved.AbilityCooldown"/> / <see cref="HateReserved.AbilityCharges"/> by tag. Scope it
        /// (e.g. to the entities in a parent view) with <paramref name="scope"/>; null = all.
        /// </summary>
        public HateChildView CreateAbilityView(Func<DataLensFilter, DataLensPredicate> scope = null)
        {
            if (!_hasAbilities) throw new InvalidOperationException("No Abilities store declared (HateSchema.WithAbilities).");
            var from = new DataLensFrom(_abilitiesStore);
            if (scope != null) from.Where(scope);
            var select = new[] { _abEntityIndexCol, _abTagCol, _abCooldownCol, _abChargesCol };
            return new HateChildView(_lens, _lens.View(from, select), _abEntityIndexCol, _abTagCol);
        }
    }

    /// <summary>
    /// A view over a <b>tall child store</b> (abilities or effects) — one row per (entity, child), addressed by an
    /// entity-index column and a tag column. It is the carried-child-rows half of a game-facing entity view: the
    /// parent view gives you the entity's attributes, this gives you its abilities/effects. Get a specific child's
    /// record with <see cref="Record"/> and read its columns by tag (e.g. cooldown/charges). Lens-maintained on the
    /// frequency you <see cref="Schedule"/>.
    /// </summary>
    public sealed class HateChildView : IDisposable
    {
        private readonly Lens _lens;
        private readonly DataLensView _view;
        private readonly GameplayTag _entityIndexCol, _tagCol;

        internal HateChildView(Lens lens, DataLensView view, GameplayTag entityIndexCol, GameplayTag tagCol)
        {
            _lens = lens; _view = view; _entityIndexCol = entityIndexCol; _tagCol = tagCol;
        }

        /// <summary>The underlying DataLens view (advanced use).</summary>
        public DataLensView View => _view;

        /// <summary>Number of child rows currently hydrated (across all in-scope entities).</summary>
        public int Count => _view.RowCount;

        /// <summary>Set the refresh frequency (Lens re-hydrates every <paramref name="period"/> ticks).</summary>
        public void Schedule(ulong period, ulong phase = 0) => _lens.Schedule(_view, period, phase);

        /// <summary>Re-hydrate now (the explicit path).</summary>
        public void Refresh() => _view.Refresh();

        /// <summary>
        /// The record for one entity's child identified by <paramref name="childTag"/> (e.g. a specific ability
        /// tag), or an invalid record if the entity does not have it. O(rows); trivial at the counts this serves.
        /// </summary>
        public DataViewRecord Record(EntityId entity, GameplayTag childTag)
        {
            int n = _view.RowCount;
            ulong tag = (ulong)childTag;
            for (int i = 0; i < n; i++)
                if (_view.Get<int>(i, _entityIndexCol) == entity.Index && _view.Get<ulong>(i, _tagCol) == tag)
                    return new DataViewRecord(_view, i);
            return default;
        }

        /// <summary>The child record at a hydrated row index (for iterating the whole in-scope set).</summary>
        public DataViewRecord RecordAt(int row) => new DataViewRecord(_view, row);

        public void Dispose() => _view?.Dispose();
    }
}
