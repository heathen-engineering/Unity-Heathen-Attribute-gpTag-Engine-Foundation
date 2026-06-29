using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// The HATE runtime: a pure DataLens consumer (HATE-Spec §5, Coding Law 4). It declares a
    /// <see cref="DataLensSchema"/> from a <see cref="HateSchema"/> - an EntityCatalog store with one
    /// dereference index column per trait, plus a store per trait - and rides a <see cref="Lens"/>. HATE owns
    /// no storage; spawn/despawn/attribute access are consumer-defined operations that drive DataLens views,
    /// addressing cells by <c>(columnId, rowIndex, value)</c>. An Entity is just a catalog row; it may be an
    /// actor, a crate, a weather front, a terrain region - anything with attributes and tags.
    /// <para>This is slice 1 (schema + catalog + trait stores + spawn/despawn/attributes). Tall stores
    /// (effects/abilities), Trait/Entity Systems and resolution arrive in later slices.</para>
    /// </summary>
    public sealed class HateWorld : IDisposable
    {
        private readonly HateSchema _schema;
        private readonly DataLensSchema _lensSchema;
        private readonly Lens _lens;
        private readonly GameplayTag _catalogTag;

        private readonly Dictionary<ulong, HateTrait> _traitOf = new Dictionary<ulong, HateTrait>();      // trait id -> trait
        private readonly Dictionary<ulong, GameplayTag> _catIndexTag = new Dictionary<ulong, GameplayTag>(); // trait id -> catalog index col tag
        private readonly Dictionary<ulong, ulong> _attrTrait = new Dictionary<ulong, ulong>();             // attr id -> owning trait id
        private readonly Dictionary<ulong, DataLensValueType> _attrType = new Dictionary<ulong, DataLensValueType>();

        private readonly Dictionary<string, DataLensView> _spawnViews = new Dictionary<string, DataLensView>();
        private DataLensView _master;                                   // catalog + all traits, all attrs (read/write)
        private Dictionary<ulong, int> _masterTraitJoin;               // trait id -> master view join index

        // Polled lifecycle queues (§14): data-driven, no callbacks. The engine bridge drains these each frame.
        private readonly List<EntityId> _spawned = new List<EntityId>();
        private readonly List<HateDespawn> _despawned = new List<HateDespawn>();

        // Cosmetic cue stream (§15): a presentation system drains it each frame. Never authoritative state.
        private readonly List<HateCueEvent> _cues = new List<HateCueEvent>();

        private readonly Dictionary<ulong, HateTallStore> _tallOf = new Dictionary<ulong, HateTallStore>();        // store id -> tall store
        private readonly Dictionary<ulong, GameplayTag> _entityIndexTag = new Dictionary<ulong, GameplayTag>();    // store id -> EntityIndex col tag
        private readonly Dictionary<ulong, DataLensView> _tallInsertViews = new Dictionary<ulong, DataLensView>(); // store id -> cached insert view

        // Effects store (HATE-Spec §7): the dedicated tall store HATE owns the schema of, + the effect-def table.
        private bool _hasEffects;
        private GameplayTag _effectsStore, _effEntityIndexCol, _effTagCol, _effDurationCol, _effStacksCol;
        private readonly Dictionary<ulong, HateModifier[]> _effectDefs = new Dictionary<ulong, HateModifier[]>();

        // Abilities store (HATE-Spec §8) + the ability-def table + the activation request queue.
        private bool _hasAbilities;
        private GameplayTag _abilitiesStore, _abEntityIndexCol, _abTagCol, _abCooldownCol, _abChargesCol;
        private readonly Dictionary<ulong, HateAbilityDef> _abilityDefs = new Dictionary<ulong, HateAbilityDef>();
        private readonly List<(EntityId caster, GameplayTag ability, EntityId target)> _activations
            = new List<(EntityId, GameplayTag, EntityId)>();

        // GrantedTags store (HATE-Spec §7.3): granted tags + immunity, ref-counted (EntityIndex + Tag + RefCount).
        private bool _hasGrantedTags;
        private GameplayTag _grantedStore, _grEntityIndexCol, _grTagCol, _grRefCol;

        // Engine bridge rendezvous (§14): an opaque ExternalRef u64 column on the catalog.
        private GameplayTag _externalRefCol;
        private DataLensView _catalogView; // lazy: base = catalog, projects ExternalRef

        public HateWorld(HateSchema schema)
        {
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _catalogTag = GameplayTag.FromName("HATE.EntityCatalog");
            _lensSchema = new DataLensSchema();

            var catalogColumns = new List<DataColumn>(schema.Traits.Count);
            foreach (HateTrait trait in schema.Traits)
            {
                GameplayTag ci = CatalogIndexTag(trait.Id);
                _traitOf[(ulong)trait.Id] = trait;
                _catIndexTag[(ulong)trait.Id] = ci;
                // catalog index column: int32, default int32.Max = "lacks this trait" (the absent sentinel).
                catalogColumns.Add(DataColumn.OfType(ci, DataLensValueType.Int32, BitConverter.GetBytes(int.MaxValue)));

                var traitColumns = new DataColumn[trait.Attributes.Count];
                for (int i = 0; i < trait.Attributes.Count; i++)
                {
                    HateAttribute a = trait.Attributes[i];
                    traitColumns[i] = DataColumn.OfType(a.Id, a.Type, DefaultBytes(a));
                    _attrTrait[(ulong)a.Id] = (ulong)trait.Id;
                    _attrType[(ulong)a.Id] = a.Type;
                }
                _lensSchema.Add(new DataStoreSchema(trait.Id, trait.Capacity, traitColumns));
            }
            // Engine-bridge rendezvous: an opaque u64 ExternalRef on the catalog (§14). HATE never interprets it.
            _externalRefCol = GameplayTag.FromName("HATE.Catalog.ExternalRef");
            catalogColumns.Add(DataColumn.OfType(_externalRefCol, DataLensValueType.UInt64));

            _lensSchema.Add(new DataStoreSchema(_catalogTag, schema.CatalogCapacity, catalogColumns.ToArray()));

            // Tall stores (effects/abilities): an implicit int32 EntityIndex column (the owning entity's
            // catalog row) plus the declared instance-state columns. "Effects on entity N" is a scope on
            // EntityIndex, not a join.
            void BuildTall(HateTallStore tall)
            {
                GameplayTag ei = EntityIndexTagFor(tall.Id);
                _tallOf[(ulong)tall.Id] = tall;
                _entityIndexTag[(ulong)tall.Id] = ei;

                var cols = new DataColumn[tall.Columns.Count + 1];
                cols[0] = DataColumn.OfType(ei, DataLensValueType.Int32); // default 0; always set on insert
                for (int i = 0; i < tall.Columns.Count; i++)
                {
                    HateAttribute a = tall.Columns[i];
                    cols[i + 1] = DataColumn.OfType(a.Id, a.Type, DefaultBytes(a));
                    _attrType[(ulong)a.Id] = a.Type;
                }
                _lensSchema.Add(new DataStoreSchema(tall.Id, tall.Capacity, cols));
            }

            foreach (HateTallStore tall in schema.TallStores) BuildTall(tall);

            // The dedicated Effects store: one tall store whose width is the reserved machinery columns
            // (identity + the timer/stacks the tick loop drives, addressed by well-known tag) plus the authored
            // effect-attribute superset. Every effect row carries every column; an effect leaves the ones it
            // does not use at default (HATE-Spec §7 - the wide-sparse "all active things share one structure").
            if (schema.HasEffects)
            {
                _hasEffects = true;
                _effectsStore = schema.EffectsStore;
                _effTagCol = HateReserved.EffectTag;
                _effDurationCol = HateReserved.EffectDuration;
                _effStacksCol = HateReserved.EffectStacks;
                var cols = new List<HateAttribute>
                {
                    new HateAttribute(_effTagCol, DataLensValueType.UInt64),
                    new HateAttribute(_effDurationCol, DataLensValueType.Float),
                    new HateAttribute(_effStacksCol, DataLensValueType.Int32),
                };
                AppendUnique(cols, schema.EffectAttributes);
                BuildTall(new HateTallStore(_effectsStore, schema.EffectsCapacity, cols.ToArray()));
                _effEntityIndexCol = _entityIndexTag[(ulong)_effectsStore];
            }

            // The dedicated Abilities store: reserved machinery columns (identity + cooldown + charges) plus the
            // authored ability-attribute superset (DPS, Power, ...), the columns buffs/debuffs operate on.
            if (schema.HasAbilities)
            {
                _hasAbilities = true;
                _abilitiesStore = schema.AbilitiesStore;
                _abTagCol = HateReserved.AbilityTag;
                _abCooldownCol = HateReserved.AbilityCooldown;
                _abChargesCol = HateReserved.AbilityCharges;
                var cols = new List<HateAttribute>
                {
                    new HateAttribute(_abTagCol, DataLensValueType.UInt64),
                    new HateAttribute(_abCooldownCol, DataLensValueType.Float),
                    new HateAttribute(_abChargesCol, DataLensValueType.Int32),
                };
                AppendUnique(cols, schema.AbilityAttributes);
                BuildTall(new HateTallStore(_abilitiesStore, schema.AbilitiesCapacity, cols.ToArray()));
                _abEntityIndexCol = _entityIndexTag[(ulong)_abilitiesStore];
            }

            // The dedicated GrantedTags store (granted tags + immunity, §7.3): EntityIndex + Tag + RefCount.
            if (schema.HasGrantedTags)
            {
                _hasGrantedTags = true;
                _grantedStore = schema.GrantedTagsStore;
                _grTagCol = EffectColTag(_grantedStore, 1);
                _grRefCol = EffectColTag(_grantedStore, 2);
                BuildTall(new HateTallStore(_grantedStore, schema.GrantedTagsCapacity,
                    new HateAttribute(_grTagCol, DataLensValueType.UInt64),
                    new HateAttribute(_grRefCol, DataLensValueType.Int32)));
                _grEntityIndexCol = _entityIndexTag[(ulong)_grantedStore];
            }

            _lens = new Lens(_lensSchema);
        }

        /// <summary>The Lens this world rides (for advanced consumers building their own Systems/Views).</summary>
        public Lens Lens => _lens;

        // ── Spawn ────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn an entity from an archetype: allocate a catalog row plus a row in each of the archetype's
        /// trait stores (the catalog index columns are linked-insert wired to the new trait rows), and seed
        /// the attribute values. Returns the new <see cref="EntityId"/> (the catalog row), or
        /// <see cref="EntityId.None"/> if a store was full (spawn is fallible, §5).
        /// </summary>
        public EntityId Spawn(HateArchetype archetype)
        {
            if (archetype == null) throw new ArgumentNullException(nameof(archetype));
            DataLensView view = GetSpawnView(archetype.Traits);

            view.Refresh();                       // reset stale New rows (re-hydrates; a batch-spawn path is a perf TODO)
            int r = view.AddRow();
            foreach (KeyValuePair<ulong, double> v in archetype.Values)
                if (_attrType.TryGetValue(v.Key, out DataLensValueType t))
                    SetTyped(view, r, new GameplayTag(v.Key), t, v.Value);
            view.SetState(r, ViewRowState.New);
            view.Commit();

            long catalogRow = view.SourceRow(r);  // Core records the allocated catalog row in the source map
            if (catalogRow < 0) return EntityId.None;
            var id = new EntityId((int)catalogRow);
            _spawned.Add(id);                     // polled "needs-visual" queue (§14)
            return id;
        }

        private DataLensView GetSpawnView(IReadOnlyList<GameplayTag> traits)
        {
            string key = TraitSetKey(traits);
            if (_spawnViews.TryGetValue(key, out DataLensView cached)) return cached;

            var from = new DataLensFrom(_catalogTag);
            var select = new List<GameplayTag>();
            foreach (GameplayTag trait in traits)
            {
                if (!_catIndexTag.TryGetValue((ulong)trait, out GameplayTag ci))
                    throw new ArgumentException($"Archetype references unknown trait {(ulong)trait}.");
                from.Dereference(into: trait, via: ci);
                select.Add(ci);                                  // project the catalog index col (puts catalog in the insert map)
                foreach (HateAttribute a in _traitOf[(ulong)trait].Attributes)
                    select.Add(a.Id);
            }
            DataLensView view = _lens.View(from, select.ToArray());
            _spawnViews[key] = view;
            return view;
        }

        // ── Attributes ───────────────────────────────────────────────────────

        /// <summary>Read an attribute of an entity (as a double; 0 if the entity lacks the attribute's trait).</summary>
        public double GetAttribute(EntityId entity, GameplayTag attribute)
        {
            if (!_attrType.TryGetValue((ulong)attribute, out DataLensValueType t)) return 0;
            DataLensView m = Master();
            m.Refresh();
            int vr = FindRow(m, entity.Index);
            return vr < 0 ? 0 : GetTyped(m, vr, attribute, t);
        }

        /// <summary>Write an attribute of an entity (no-op if the entity lacks the attribute's trait).</summary>
        public void SetAttribute(EntityId entity, GameplayTag attribute, double value)
        {
            if (!_attrType.TryGetValue((ulong)attribute, out DataLensValueType t)) return;
            DataLensView m = Master();
            m.Refresh();
            int vr = FindRow(m, entity.Index);
            if (vr < 0) return;
            SetTyped(m, vr, attribute, t, value);
            m.SetState(vr, ViewRowState.Modified);
            m.Commit();
        }

        /// <summary>True if the entity carries the trait.</summary>
        public bool HasTrait(EntityId entity, GameplayTag trait)
        {
            DataLensView m = Master();   // also builds _masterTraitJoin
            m.Refresh();
            int vr = FindRow(m, entity.Index);
            if (vr < 0) return false;
            return _masterTraitJoin.TryGetValue((ulong)trait, out int j) && m.SourceJoinRow(vr, j) >= 0;
        }

        // ── Despawn ──────────────────────────────────────────────────────────

        /// <summary>
        /// Despawn an entity: free its catalog row and every trait row it owns (a View Delete). The entity is
        /// also enqueued for <see cref="DrainDespawned"/> so the game can do its own teardown (destroy an
        /// actor, fire a loot spawner, etc.) - HATE makes no assumption that an entity has a visual at all.
        /// Returns false if the entity was not live.
        /// </summary>
        public bool Despawn(EntityId entity)
        {
            ulong externalRef = GetExternalRef(entity);   // capture the engine handle before the row is freed
            DataLensView m = Master();
            m.Refresh();
            int vr = FindRow(m, entity.Index);
            if (vr < 0) return false;
            m.SetState(vr, ViewRowState.Removed);
            m.Commit();
            ClearTallRowsFor(entity);   // free this entity's effect/ability/granted-tag/tall rows: a reused
                                        // catalog slot must not inherit stale instance state or immunity
            _despawned.Add(new HateDespawn(entity, externalRef));
            return true;
        }

        /// <summary>Take and clear the entities spawned since the last drain (polled "needs-visual", §14).</summary>
        public IReadOnlyList<EntityId> DrainSpawned()
        {
            var copy = _spawned.ToArray();
            _spawned.Clear();
            return copy;
        }

        /// <summary>Take and clear the despawn events since the last drain (polled teardown, carries ExternalRef, §14).</summary>
        public IReadOnlyList<HateDespawn> DrainDespawned()
        {
            var copy = _despawned.ToArray();
            _despawned.Clear();
            return copy;
        }

        /// <summary>Drain the spawned queue into a reusable list (no per-frame allocation — the bridge path).</summary>
        public void DrainSpawned(List<EntityId> into)
        {
            if (into == null) return;
            into.Clear();
            into.AddRange(_spawned);
            _spawned.Clear();
        }

        /// <summary>Drain the despawn events into a reusable list (no per-frame allocation — the bridge path).</summary>
        public void DrainDespawned(List<HateDespawn> into)
        {
            if (into == null) return;
            into.Clear();
            into.AddRange(_despawned);
            _despawned.Clear();
        }

        // ── Gameplay cues (§15) ──────────────────────────────────────────────

        /// <summary>
        /// Append a cosmetic cue to the stream (HATE-Spec §15). Cues never alter authoritative state; a
        /// presentation system drains them each frame and maps the tag to VFX/SFX/anim. The Toolkit bridge
        /// resolves a world location from <paramref name="source"/> (the Foundation stays engine-agnostic).
        /// </summary>
        public void RaiseCue(GameplayTag cue, HateCueType type, double magnitude, EntityId source)
            => _cues.Add(new HateCueEvent(cue, type, magnitude, source));

        /// <summary>Append a sourceless cosmetic cue (source = <see cref="EntityId.None"/>); see the overload
        /// above. <c>default(EntityId)</c> is catalog row 0, not None, so the default is set explicitly here.</summary>
        public void RaiseCue(GameplayTag cue, HateCueType type, double magnitude = 0)
            => _cues.Add(new HateCueEvent(cue, type, magnitude, EntityId.None));

        /// <summary>The cues raised since the last drain (read-only; does not clear the stream).</summary>
        public IReadOnlyList<HateCueEvent> Cues => _cues;

        /// <summary>Take and clear the cues raised since the last drain (the presentation-system path).</summary>
        public IReadOnlyList<HateCueEvent> DrainCues()
        {
            var copy = _cues.ToArray();
            _cues.Clear();
            return copy;
        }

        /// <summary>Drain the cues into a reusable list (no per-frame allocation - the presentation path).</summary>
        public void DrainCues(List<HateCueEvent> into)
        {
            if (into == null) return;
            into.Clear();
            into.AddRange(_cues);
            _cues.Clear();
        }

        /// <summary>Discard all queued cues without draining (e.g. a dedicated server that skips presentation).</summary>
        public void ClearCues() => _cues.Clear();

        /// <summary>True if the entity is a live catalog row (a valid, non-despawned handle).</summary>
        public bool IsAlive(EntityId entity)
        {
            if (!entity.IsValid) return false;
            DataLensView m = Master();
            m.Refresh();
            return FindRow(m, entity.Index) >= 0;
        }

        // ── Engine bridge: ExternalRef (§14) ─────────────────────────────────

        /// <summary>Read the entity's opaque engine handle (0 = unbound / unknown entity).</summary>
        public ulong GetExternalRef(EntityId entity)
        {
            DataLensView cv = CatalogView();
            cv.Refresh();
            int vr = FindRow(cv, entity.Index);
            return vr < 0 ? 0UL : cv.Get<ulong>(vr, _externalRefCol);
        }

        /// <summary>Stash the engine's own handle on the entity (HATE never interprets these bits).</summary>
        public void SetExternalRef(EntityId entity, ulong externalRef)
        {
            DataLensView cv = CatalogView();
            cv.Refresh();
            int vr = FindRow(cv, entity.Index);
            if (vr < 0) return;
            cv.Set<ulong>(vr, _externalRefCol, externalRef);
            cv.SetState(vr, ViewRowState.Modified);
            cv.Commit();
        }

        private DataLensView CatalogView()
            => _catalogView ?? (_catalogView = _lens.View(new DataLensFrom(_catalogTag), new[] { _externalRefCol }));

        // ── Entity System surface ────────────────────────────────────────────

        /// <summary>
        /// Open a cross-trait view (the Entity System surface): base = EntityCatalog, a dereference per trait,
        /// projecting the requested attributes (plus each trait's catalog index column so the view can delete).
        /// The optional <paramref name="filter"/> scopes which entities hydrate. The consumer drives it.
        /// </summary>
        public DataLensView OpenView(GameplayTag[] traits, GameplayTag[] attributes, DataLensPredicate filter = null)
        {
            if (traits == null || traits.Length == 0) throw new ArgumentException("A view needs at least one trait.", nameof(traits));
            var from = new DataLensFrom(_catalogTag);
            var select = new List<GameplayTag>();
            foreach (GameplayTag trait in traits)
            {
                if (!_catIndexTag.TryGetValue((ulong)trait, out GameplayTag ci))
                    throw new ArgumentException($"Unknown trait {(ulong)trait}.");
                from.Dereference(into: trait, via: ci);
                select.Add(ci);
            }
            if (attributes != null)
                foreach (GameplayTag a in attributes) select.Add(a);
            if (filter != null) from.Where(_ => filter);
            return _lens.View(from, select.ToArray());
        }

        // ── Detector → reactor (§9) ──────────────────────────────────────────

        /// <summary>A reaction over one hydrated store row: read/write cells via the view, raise cues, etc.
        /// If it writes a cell it must mark the row <see cref="ViewRowState.Modified"/> (the reactor commits).</summary>
        public delegate void ReactRow(DataLensView view, int row);

        /// <summary>A reaction over one hydrated entity (cross-trait) row; the entity is the dereferenced
        /// catalog row. Write via the view + mark Modified to persist; call <see cref="Despawn"/> to remove.</summary>
        public delegate void ReactEntityRow(EntityId entity, DataLensView view, int row);

        /// <summary>
        /// The general detector → reactor over a store (HATE-Spec §9), the assumption-free reactive primitive:
        /// hydrate just the rows matching <paramref name="filter"/> (the detector - a column predicate, the
        /// scope keeps the cross-cutting work sparse) and run <paramref name="react"/> on each. The reaction is
        /// the consumer's - cues, effects, AI, raw game logic, a Wyrd rule - HATE assumes nothing about it.
        /// With <paramref name="removeMatched"/> the matched rows are freed after reacting (the expire / "on
        /// exit" pass). Returns the number of rows reacted. Detection is a column predicate here; persist it as
        /// a flag column via <see cref="Lens"/><c>.RunSystem</c> when other Systems need to read it too.
        /// </summary>
        public int React(GameplayTag store, GameplayTag[] columns, DataLensPredicate filter,
            ReactRow react, bool removeMatched = false)
        {
            using (DataLensView v = OpenStoreView(store, columns, filter))
            {
                v.Refresh();
                int n = v.RowCount;
                for (int i = 0; i < n; i++)
                {
                    react?.Invoke(v, i);
                    if (removeMatched) v.SetState(i, ViewRowState.Removed);
                }
                if (n > 0) v.Commit();
                return n;
            }
        }

        /// <summary>
        /// The general detector → reactor over entities (cross-trait, catalog-based): hydrate just the entities
        /// matching <paramref name="filter"/> and run <paramref name="react"/> on each, with the resolved
        /// <see cref="EntityId"/>. The §9 reactor for entity-level edges ("every entity with Health &lt;= 0").
        /// To remove an entity the reaction calls <see cref="Despawn"/> (so the lifecycle queue + tall-row
        /// cleanup run); this is why there is no removeMatched here. Returns the number of entities reacted.
        /// </summary>
        public int ReactEntities(GameplayTag[] traits, GameplayTag[] attributes, DataLensPredicate filter,
            ReactEntityRow react)
        {
            using (DataLensView v = OpenView(traits, attributes, filter))
            {
                v.Refresh();
                int n = v.RowCount;
                for (int i = 0; i < n; i++)
                    react?.Invoke(new EntityId((int)v.SourceRow(i)), v, i);
                if (n > 0) v.Commit();
                return n;
            }
        }

        /// <summary>
        /// Advance the age/lifecycle column of a store by one tick (a Trait System pass, HATE-Spec §9). The
        /// convention: a row inserts at age 0, so <c>age == 0</c> means "entered this tick" (react to it before
        /// calling this); after the bump it is age &gt;= 1 and no longer fires the entered reactor. Removal is a
        /// column predicate too (e.g. a timer &lt;= 0), reacted to with <c>removeMatched</c> before the delete.
        /// Age is an ordinary int column the consumer declares - HATE bakes in no lifecycle assumptions.
        /// </summary>
        public void BumpAges(GameplayTag store, GameplayTag ageColumn)
            => _lens.RunSystem(store, ageColumn, SystemOp.Add, 1);

        /// <summary>The "entered this tick" predicate for an age column (<c>age == 0</c>), per the convention
        /// above - sugar for <c>new DataLensFilter().Eq(ageColumn, 0)</c>.</summary>
        public static DataLensPredicate Entered(GameplayTag ageColumn) => new DataLensFilter().Eq(ageColumn, 0);

        // ── Tall stores (effects / abilities) ────────────────────────────────

        /// <summary>The synthesised EntityIndex column tag of a tall store (scope on it: WHERE EntityIndex == N).</summary>
        public GameplayTag EntityIndexColumn(GameplayTag store)
        {
            if (!_entityIndexTag.TryGetValue((ulong)store, out GameplayTag t))
                throw new ArgumentException($"Unknown tall store {(ulong)store}.");
            return t;
        }

        /// <summary>
        /// Insert a row into a tall store (e.g. grant an effect/ability to an entity): sets EntityIndex to the
        /// owner and the given instance-state values. This is the consumer-defined Insert (an Entity System op).
        /// </summary>
        public void AddToStore(GameplayTag store, EntityId owner, params (GameplayTag col, double value)[] values)
        {
            DataLensView view = GetTallInsertView(store);
            view.Refresh();                       // reset stale New rows (perf TODO: batch insert)
            int r = view.AddRow();
            view.Set<int>(r, _entityIndexTag[(ulong)store], owner.Index);
            if (values != null)
                foreach ((GameplayTag col, double value) in values)
                    if (_attrType.TryGetValue((ulong)col, out DataLensValueType t))
                        SetTyped(view, r, col, t, value);
            view.SetState(r, ViewRowState.New);
            view.Commit();
        }

        /// <summary>Number of rows in a tall store owned by an entity (e.g. how many effects on it).</summary>
        public int CountFor(GameplayTag store, EntityId owner)
        {
            GameplayTag ei = EntityIndexColumn(store);
            using (DataLensView v = OpenStoreView(store, new[] { ei }, new DataLensFilter().Eq(ei, owner.Index)))
            {
                v.Refresh();
                return v.RowCount;
            }
        }

        /// <summary>A view directly over a (tall) store, optionally scoped - the consumer drives its tick/reads.</summary>
        public DataLensView OpenStoreView(GameplayTag store, GameplayTag[] columns, DataLensPredicate filter = null)
        {
            var from = new DataLensFrom(store);
            if (filter != null) from.Where(_ => filter);
            return _lens.View(from, columns);
        }

        /// <summary>
        /// Free every row of a tall store whose <paramref name="timerColumn"/> has reached zero (expired
        /// effects): an Entity System View Delete (§7.1 - a Trait System ticks the timer down, this deletes).
        /// Returns the number of rows freed.
        /// </summary>
        public int Expire(GameplayTag store, GameplayTag timerColumn)
        {
            if (!_attrType.TryGetValue((ulong)timerColumn, out DataLensValueType t))
                throw new ArgumentException($"Unknown column {(ulong)timerColumn}.");
            DataLensPredicate expired = t == DataLensValueType.Float
                ? new DataLensFilter().LessEq(timerColumn, 0f)
                : new DataLensFilter().LessEq(timerColumn, 0);
            using (DataLensView v = OpenStoreView(store, new[] { timerColumn }, expired))
            {
                v.Refresh();
                int n = v.RowCount;
                for (int i = 0; i < n; i++) v.SetState(i, ViewRowState.Removed);
                v.Commit();
                return n;
            }
        }

        private DataLensView GetTallInsertView(GameplayTag store)
        {
            if (_tallInsertViews.TryGetValue((ulong)store, out DataLensView cached)) return cached;
            if (!_tallOf.TryGetValue((ulong)store, out HateTallStore tall))
                throw new ArgumentException($"Unknown tall store {(ulong)store}.");
            var select = new List<GameplayTag> { _entityIndexTag[(ulong)store] };
            foreach (HateAttribute a in tall.Columns) select.Add(a.Id);
            DataLensView view = _lens.View(new DataLensFrom(store), select.ToArray());
            _tallInsertViews[(ulong)store] = view;
            return view;
        }

        private static GameplayTag EntityIndexTagFor(GameplayTag store)
        {
            ulong id = (ulong)store;
            ulong mixed = unchecked(id * 0x9E3779B97F4A7C15UL) ^ 0x456E74497A6E6478UL; // distinct salt from catalog index
            return GameplayTag.FromId(mixed);
        }

        // Append the authored superset to a store's columns, skipping any tag already present as a reserved
        // machinery column (so authoring e.g. an explicit "Duration" does not double the timer column).
        private static void AppendUnique(List<HateAttribute> cols, IReadOnlyList<HateAttribute> extra)
        {
            if (extra == null || extra.Count == 0) return;
            var seen = new HashSet<ulong>();
            foreach (HateAttribute c in cols) seen.Add((ulong)c.Id);
            foreach (HateAttribute a in extra) if (seen.Add((ulong)a.Id)) cols.Add(a);
        }

        private static GameplayTag EffectColTag(GameplayTag store, int salt)
        {
            ulong id = (ulong)store;
            ulong mixed = unchecked((id + (ulong)salt * 0x100000001B3UL) * 0x9E3779B97F4A7C15UL) ^ (0xEFFEC70000UL + (ulong)salt);
            return GameplayTag.FromId(mixed);
        }

        // ── Effects: definition, application, duration aggregation (§7) ───────

        /// <summary>Register an effect's static modifier bag (A), addressed by tag (HATE-Spec §7.2).</summary>
        public void DefineEffect(GameplayTag effectTag, params HateModifier[] modifiers)
            => _effectDefs[(ulong)effectTag] = modifiers ?? Array.Empty<HateModifier>();

        /// <summary>
        /// Apply an effect instantly: each modifier permanently changes the target attribute (§7.1 Instant).
        /// Use base attributes here; duration buffs target the current attribute and fold via
        /// <see cref="RecomputeAttribute"/>. Returns false if the effect is undefined.
        /// </summary>
        public bool ApplyInstant(EntityId entity, GameplayTag effectTag)
        {
            if (!_effectDefs.TryGetValue((ulong)effectTag, out HateModifier[] mods)) return false;
            foreach (HateModifier m in mods)
                SetAttribute(entity, m.Attribute, Combine(GetAttribute(entity, m.Attribute), m.Op, m.Magnitude));
            return true;
        }

        /// <summary>
        /// Grant a duration effect to an entity (a tall row): <paramref name="reapply"/> chooses Instance (new
        /// row), Refresh (reset the timer) or Stack (increment stacks + reset). Its modifiers contribute to the
        /// current attribute via <see cref="RecomputeAttribute"/> until it expires (§7.1/§7.3).
        /// </summary>
        public void AddEffect(EntityId entity, GameplayTag effectTag, double duration,
            HateReapply reapply = HateReapply.Refresh, int stacks = 1)
        {
            if (!_hasEffects) throw new InvalidOperationException("No Effects store declared (HateSchema.WithEffects).");
            DataLensView view = GetTallInsertView(_effectsStore);
            view.Refresh();

            if (reapply != HateReapply.Instance)
            {
                int existing = FindEffectRow(view, entity.Index, (ulong)effectTag);
                if (existing >= 0)
                {
                    if (reapply == HateReapply.Stack)
                        view.Set<int>(existing, _effStacksCol, view.Get<int>(existing, _effStacksCol) + stacks);
                    view.Set<float>(existing, _effDurationCol, (float)duration); // reset the timer
                    view.SetState(existing, ViewRowState.Modified);
                    view.Commit();
                    return;
                }
            }

            int r = view.AddRow();
            view.Set<int>(r, _effEntityIndexCol, entity.Index);
            view.Set<ulong>(r, _effTagCol, (ulong)effectTag);
            view.Set<float>(r, _effDurationCol, (float)duration);
            view.Set<int>(r, _effStacksCol, stacks);
            view.SetState(r, ViewRowState.New);
            view.Commit();
        }

        /// <summary>Number of active effect rows on an entity.</summary>
        public int CountEffects(EntityId entity) => _hasEffects ? CountFor(_effectsStore, entity) : 0;

        /// <summary>
        /// Tick all effect timers down by <paramref name="dt"/> (a Trait System column pass) and delete those
        /// that reach zero (an Entity System View Delete). Recompute attributes afterwards to revert expired
        /// contributions (§7.1).
        /// </summary>
        public void TickEffects(double dt)
        {
            if (!_hasEffects) return;
            _lens.RunSystem(_effectsStore, _effDurationCol, SystemOp.Sub, dt);
            Expire(_effectsStore, _effDurationCol);
        }

        /// <summary>
        /// Recompute a buffable attribute over its active duration effects (HATE-Spec §7.2):
        /// <c>Current = override ?? (Base + ΣAdd)·ΠMul</c>, stacking-aware (Add → stacks·mag, Multiply →
        /// mag^stacks). Run after instant changes / effect add/expire; an expired effect simply stops
        /// contributing, so duration buffs auto-revert. This is an Entity System: it folds the hydrated effect
        /// records in HATE code (DataLens neither types nor aggregates).
        /// </summary>
        public void RecomputeAttribute(GameplayTag baseAttribute, GameplayTag currentAttribute)
        {
            var add = new Dictionary<int, double>();
            var mul = new Dictionary<int, double>();
            var over = new Dictionary<int, double>();

            if (_hasEffects)
            {
                using (DataLensView ev = OpenStoreView(_effectsStore,
                    new[] { _effEntityIndexCol, _effTagCol, _effStacksCol }))
                {
                    ev.Refresh();
                    int n = ev.RowCount;
                    for (int i = 0; i < n; i++)
                    {
                        int ent = ev.Get<int>(i, _effEntityIndexCol);
                        ulong tag = ev.Get<ulong>(i, _effTagCol);
                        int st = ev.Get<int>(i, _effStacksCol);
                        if (st < 1) st = 1;
                        if (!_effectDefs.TryGetValue(tag, out HateModifier[] mods)) continue;
                        foreach (HateModifier m in mods)
                        {
                            if ((ulong)m.Attribute != (ulong)currentAttribute) continue;
                            switch (m.Op)
                            {
                                case HateOp.Add:      add[ent] = (add.TryGetValue(ent, out double a) ? a : 0) + st * m.Magnitude; break;
                                case HateOp.Multiply: mul[ent] = (mul.TryGetValue(ent, out double mv) ? mv : 1) * Math.Pow(m.Magnitude, st); break;
                                case HateOp.Override: over[ent] = m.Magnitude; break;
                            }
                        }
                    }
                }
            }

            DataLensView master = Master();
            master.Refresh();
            DataLensValueType baseType = _attrType[(ulong)baseAttribute];
            DataLensValueType curType = _attrType[(ulong)currentAttribute];
            int rows = master.RowCount;
            bool any = false;
            for (int vr = 0; vr < rows; vr++)
            {
                int ent = (int)master.SourceRow(vr);
                double baseV = GetTyped(master, vr, baseAttribute, baseType);
                double a = add.TryGetValue(ent, out double av) ? av : 0;
                double mv = mul.TryGetValue(ent, out double mvv) ? mvv : 1;
                double cur = over.TryGetValue(ent, out double ov) ? ov : (baseV + a) * mv;
                SetTyped(master, vr, currentAttribute, curType, cur);
                master.SetState(vr, ViewRowState.Modified);
                any = true;
            }
            if (any) master.Commit();
        }

        /// <summary>
        /// Drain a Targetable damage buffer (§10): for every row of <paramref name="trait"/>, mitigate the
        /// <paramref name="bufferColumn"/> by an optional resist fraction and an optional shield (which absorbs
        /// up to its value, overflow continues), subtract the remainder from <paramref name="healthColumn"/>,
        /// and zero the buffer. A coded Entity System over the trait's rows; pass <c>default</c> to skip resist
        /// or shield. Returns the number of rows that took damage. Mana Burn and other cross-trait effects are
        /// the scatter path (a custom Entity System), not this column-expressible default.
        /// </summary>
        public int DrainBuffer(GameplayTag trait, GameplayTag bufferColumn, GameplayTag healthColumn,
            GameplayTag resistColumn = default, GameplayTag shieldColumn = default)
        {
            bool hasResist = resistColumn.IsValid && _attrType.ContainsKey((ulong)resistColumn);
            bool hasShield = shieldColumn.IsValid && _attrType.ContainsKey((ulong)shieldColumn);

            var cols = new List<GameplayTag> { bufferColumn, healthColumn };
            if (hasResist) cols.Add(resistColumn);
            if (hasShield) cols.Add(shieldColumn);

            DataLensValueType tBuffer = _attrType[(ulong)bufferColumn];
            DataLensValueType tHealth = _attrType[(ulong)healthColumn];

            using (DataLensView v = OpenStoreView(trait, cols.ToArray()))
            {
                v.Refresh();
                int n = v.RowCount;
                int affected = 0;
                for (int i = 0; i < n; i++)
                {
                    double dmg = GetTyped(v, i, bufferColumn, tBuffer);
                    if (dmg == 0) continue;

                    if (hasResist)
                        dmg *= 1.0 - GetTyped(v, i, resistColumn, _attrType[(ulong)resistColumn]);
                    if (hasShield)
                    {
                        DataLensValueType ts = _attrType[(ulong)shieldColumn];
                        double sh = GetTyped(v, i, shieldColumn, ts);
                        double absorbed = Math.Min(dmg, sh);
                        SetTyped(v, i, shieldColumn, ts, sh - absorbed);
                        dmg -= absorbed;
                    }

                    SetTyped(v, i, healthColumn, tHealth, GetTyped(v, i, healthColumn, tHealth) - dmg);
                    SetTyped(v, i, bufferColumn, tBuffer, 0);
                    v.SetState(i, ViewRowState.Modified);
                    affected++;
                }
                if (affected > 0) v.Commit();
                return affected;
            }
        }

        private int FindEffectRow(DataLensView view, int entityIndex, ulong effectTag)
        {
            int n = view.RowCount;
            for (int i = 0; i < n; i++)
                if (view.Get<int>(i, _effEntityIndexCol) == entityIndex && view.Get<ulong>(i, _effTagCol) == effectTag)
                    return i;
            return -1;
        }

        private static double Combine(double cur, HateOp op, double mag)
        {
            switch (op)
            {
                case HateOp.Add:      return cur + mag;
                case HateOp.Multiply: return cur * mag;
                case HateOp.Override: return mag;
                default:              return cur;
            }
        }

        // ── Abilities (§8) ───────────────────────────────────────────────────

        /// <summary>Register an ability's static definition (A), addressed by tag.</summary>
        public void DefineAbility(GameplayTag abilityTag, HateAbilityDef def)
            => _abilityDefs[(ulong)abilityTag] = def ?? throw new ArgumentNullException(nameof(def));

        /// <summary>Grant an ability to an entity (insert an EquippedAbilities row); no-op if already granted.</summary>
        public void GrantAbility(EntityId entity, GameplayTag abilityTag, int charges = 1)
        {
            if (!_hasAbilities) throw new InvalidOperationException("No Abilities store declared (HateSchema.WithAbilities).");
            DataLensView view = GetTallInsertView(_abilitiesStore);
            view.Refresh();
            if (FindAbilityRow(view, entity.Index, (ulong)abilityTag) >= 0) return;
            int r = view.AddRow();
            view.Set<int>(r, _abEntityIndexCol, entity.Index);
            view.Set<ulong>(r, _abTagCol, (ulong)abilityTag);
            view.Set<float>(r, _abCooldownCol, 0f);
            view.Set<int>(r, _abChargesCol, charges);
            view.SetState(r, ViewRowState.New);
            view.Commit();
        }

        /// <summary>True if the entity has the ability granted.</summary>
        public bool HasAbility(EntityId entity, GameplayTag abilityTag)
        {
            if (!_hasAbilities) return false;
            DataLensView view = GetTallInsertView(_abilitiesStore);
            view.Refresh();
            return FindAbilityRow(view, entity.Index, (ulong)abilityTag) >= 0;
        }

        /// <summary>Tick every ability cooldown down by dt and clamp at zero (a Trait System column pass).</summary>
        public void TickCooldowns(double dt)
        {
            if (!_hasAbilities) return;
            _lens.RunSystem(_abilitiesStore, _abCooldownCol, SystemOp.Sub, dt);
            _lens.RunSystem(_abilitiesStore, _abCooldownCol, SystemOp.Max, 0);
        }

        /// <summary>Activate an ability on the caster itself.</summary>
        public bool Activate(EntityId caster, GameplayTag abilityTag) => Activate(caster, abilityTag, caster);

        /// <summary>
        /// Activate an ability on a target: gated by grant + cooldown + (optional) resource cost. On success it
        /// pays the cost, applies the ability's effects to the target, and starts the cooldown. The resolver
        /// (§8); targeting input schema is the game's, here the target is supplied.
        /// </summary>
        public bool Activate(EntityId caster, GameplayTag abilityTag, EntityId target)
        {
            if (!_hasAbilities || !_abilityDefs.TryGetValue((ulong)abilityTag, out HateAbilityDef def)) return false;

            DataLensView view = GetTallInsertView(_abilitiesStore);
            view.Refresh();
            int row = FindAbilityRow(view, caster.Index, (ulong)abilityTag);
            if (row < 0) return false;                                   // not granted
            if (view.Get<float>(row, _abCooldownCol) > 0f) return false;  // on cooldown
            if (def.CostResource.IsValid && GetAttribute(caster, def.CostResource) < def.CostAmount) return false; // unaffordable

            view.Set<float>(row, _abCooldownCol, (float)def.Cooldown);    // start cooldown (abilities store)
            view.SetState(row, ViewRowState.Modified);
            view.Commit();

            if (def.CostResource.IsValid)
                SetAttribute(caster, def.CostResource, GetAttribute(caster, def.CostResource) - def.CostAmount);
            foreach (GameplayTag e in def.Effects) ApplyInstant(target, e);
            return true;
        }

        /// <summary>Queue an activation request (the §8 AbilityActivationRequest), drained by <see cref="DrainActivations"/>.</summary>
        public void RequestActivation(EntityId caster, GameplayTag abilityTag, EntityId target)
            => _activations.Add((caster, abilityTag, target));

        /// <summary>Process all queued activation requests (the one dispatch Entity System). Returns successes.</summary>
        public int DrainActivations()
        {
            int ok = 0;
            for (int i = 0; i < _activations.Count; i++)
                if (Activate(_activations[i].caster, _activations[i].ability, _activations[i].target)) ok++;
            _activations.Clear();
            return ok;
        }

        private int FindAbilityRow(DataLensView view, int entityIndex, ulong abilityTag)
        {
            int n = view.RowCount;
            for (int i = 0; i < n; i++)
                if (view.Get<int>(i, _abEntityIndexCol) == entityIndex && view.Get<ulong>(i, _abTagCol) == abilityTag)
                    return i;
            return -1;
        }

        // ── Granted tags / immunity (§7.3) ───────────────────────────────────

        /// <summary>
        /// Grant a tag to an entity for as long as it is held (a granted-tags row, ref-counted): a status tag
        /// (<c>State.Stunned</c>) or an immunity classification. Overlapping grants increment the ref-count so
        /// the tag survives until every grant is revoked (HATE-Spec §7.3).
        /// </summary>
        public void GrantTag(EntityId entity, GameplayTag tag, int count = 1)
        {
            if (!_hasGrantedTags) throw new InvalidOperationException("No GrantedTags store declared (HateSchema.WithGrantedTags).");
            if (count <= 0) return;
            DataLensView view = GetTallInsertView(_grantedStore);
            view.Refresh();
            int existing = FindTagRow(view, entity.Index, (ulong)tag);
            if (existing >= 0)
            {
                view.Set<int>(existing, _grRefCol, view.Get<int>(existing, _grRefCol) + count);
                view.SetState(existing, ViewRowState.Modified);
                view.Commit();
                return;
            }
            int r = view.AddRow();
            view.Set<int>(r, _grEntityIndexCol, entity.Index);
            view.Set<ulong>(r, _grTagCol, (ulong)tag);
            view.Set<int>(r, _grRefCol, count);
            view.SetState(r, ViewRowState.New);
            view.Commit();
        }

        /// <summary>
        /// Revoke a previously granted tag: decrement its ref-count, freeing the row when it reaches zero.
        /// Returns false if the entity did not hold the tag.
        /// </summary>
        public bool RevokeTag(EntityId entity, GameplayTag tag, int count = 1)
        {
            if (!_hasGrantedTags || count <= 0) return false;
            DataLensView view = GetTallInsertView(_grantedStore);
            view.Refresh();
            int existing = FindTagRow(view, entity.Index, (ulong)tag);
            if (existing < 0) return false;
            int remaining = view.Get<int>(existing, _grRefCol) - count;
            if (remaining > 0)
            {
                view.Set<int>(existing, _grRefCol, remaining);
                view.SetState(existing, ViewRowState.Modified);
            }
            else
            {
                view.SetState(existing, ViewRowState.Removed);
            }
            view.Commit();
            return true;
        }

        /// <summary>True if the entity currently holds the granted tag (ref-count &gt; 0).</summary>
        public bool HasTag(EntityId entity, GameplayTag tag)
        {
            if (!_hasGrantedTags) return false;
            DataLensView view = GetTallInsertView(_grantedStore);
            view.Refresh();
            int r = FindTagRow(view, entity.Index, (ulong)tag);
            return r >= 0 && view.Get<int>(r, _grRefCol) > 0;
        }

        /// <summary>True if the entity holds any of the given tags (the immunity overlap test).</summary>
        public bool HasAnyTag(EntityId entity, params GameplayTag[] tags)
        {
            if (!_hasGrantedTags || tags == null || tags.Length == 0) return false;
            DataLensView view = GetTallInsertView(_grantedStore);
            view.Refresh();
            foreach (GameplayTag t in tags)
            {
                int r = FindTagRow(view, entity.Index, (ulong)t);
                if (r >= 0 && view.Get<int>(r, _grRefCol) > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Apply an effect instantly unless the target is immune (HATE-Spec §7.3): if any of the effect's
        /// <paramref name="classificationTags"/> overlaps the target's granted tags, the application is blocked.
        /// Returns true only if the effect was applied; false if it was blocked by immunity or is undefined.
        /// </summary>
        public bool TryApplyEffectInstant(EntityId entity, GameplayTag effectTag, params GameplayTag[] classificationTags)
        {
            if (HasAnyTag(entity, classificationTags)) return false; // blocked by immunity
            return ApplyInstant(entity, effectTag);
        }

        // Free every tall-store row owned by an entity (EntityIndex == its catalog row), across effects,
        // abilities, granted tags and any user-declared tall stores - all are EntityIndex-keyed. Called on
        // Despawn so a reused catalog slot never inherits the previous occupant's instance state.
        private void ClearTallRowsFor(EntityId entity)
        {
            foreach (KeyValuePair<ulong, GameplayTag> kv in _entityIndexTag)
            {
                DataLensView view = GetTallInsertView(new GameplayTag(kv.Key));
                view.Refresh();
                int n = view.RowCount;
                bool any = false;
                for (int i = 0; i < n; i++)
                    if (view.Get<int>(i, kv.Value) == entity.Index)
                    {
                        view.SetState(i, ViewRowState.Removed);
                        any = true;
                    }
                if (any) view.Commit();
            }
        }

        private int FindTagRow(DataLensView view, int entityIndex, ulong tag)
        {
            int n = view.RowCount;
            for (int i = 0; i < n; i++)
                if (view.Get<int>(i, _grEntityIndexCol) == entityIndex && view.Get<ulong>(i, _grTagCol) == tag)
                    return i;
            return -1;
        }

        // ── internals ────────────────────────────────────────────────────────

        // The "master" view: catalog + every trait dereferenced + every attribute. Used for single-entity
        // get/set/despawn/membership. O(live entities) per refresh - the rare whole-entity path (§5); scoped
        // Entity Systems (OpenView) are the bulk path.
        private DataLensView Master()
        {
            if (_master != null) return _master;
            var from = new DataLensFrom(_catalogTag);
            var select = new List<GameplayTag>();
            var readOnly = new List<bool>();
            _masterTraitJoin = new Dictionary<ulong, int>();
            int j = 0;
            foreach (HateTrait trait in _schema.Traits)
            {
                GameplayTag ci = _catIndexTag[(ulong)trait.Id];
                from.Dereference(into: trait.Id, via: ci);
                _masterTraitJoin[(ulong)trait.Id] = j++;
                // Project the index col read-only: it keeps the catalog in the delete set (so Despawn frees the
                // catalog row) without the master rewriting indices on an attribute update.
                select.Add(ci); readOnly.Add(true);
                foreach (HateAttribute a in trait.Attributes) { select.Add(a.Id); readOnly.Add(false); }
            }
            _master = _lens.View(from, select.ToArray(), readOnly.ToArray());
            return _master;
        }

        private static int FindRow(DataLensView view, int catalogIndex)
        {
            int n = view.RowCount;
            for (int i = 0; i < n; i++)
                if (view.SourceRow(i) == catalogIndex) return i;
            return -1;
        }

        private static GameplayTag CatalogIndexTag(GameplayTag trait)
        {
            // Bijective mix (odd multiply + xor) so distinct traits get distinct, collision-free catalog
            // index column tags that won't clash with authored attribute tags.
            ulong id = (ulong)trait;
            ulong mixed = unchecked(id * 0x9E3779B97F4A7C15UL) ^ 0x484154456E747921UL;
            return GameplayTag.FromId(mixed);
        }

        private static string TraitSetKey(IReadOnlyList<GameplayTag> traits)
        {
            var ids = new ulong[traits.Count];
            for (int i = 0; i < traits.Count; i++) ids[i] = (ulong)traits[i];
            Array.Sort(ids);
            return string.Join(",", ids);
        }

        private static byte[] DefaultBytes(HateAttribute a)
            => a.Default == 0 ? null : TypedBytes(a.Type, a.Default);

        private static byte[] TypedBytes(DataLensValueType t, double v)
        {
            switch (t)
            {
                case DataLensValueType.Bool:   return new[] { (byte)(v != 0 ? 1 : 0) };
                case DataLensValueType.Int8:   return new[] { unchecked((byte)(sbyte)v) };
                case DataLensValueType.UInt8:  return new[] { (byte)v };
                case DataLensValueType.Int16:  return BitConverter.GetBytes((short)v);
                case DataLensValueType.UInt16: return BitConverter.GetBytes((ushort)v);
                case DataLensValueType.Int32:  return BitConverter.GetBytes((int)v);
                case DataLensValueType.UInt32: return BitConverter.GetBytes((uint)v);
                case DataLensValueType.Int64:  return BitConverter.GetBytes((long)v);
                case DataLensValueType.UInt64: return BitConverter.GetBytes((ulong)v);
                case DataLensValueType.Float:  return BitConverter.GetBytes((float)v);
                case DataLensValueType.Double: return BitConverter.GetBytes(v);
                default: return null;
            }
        }

        private static void SetTyped(DataLensView view, int row, GameplayTag col, DataLensValueType t, double v)
        {
            switch (t)
            {
                case DataLensValueType.Bool:   view.Set<byte>(row, col, (byte)(v != 0 ? 1 : 0)); break;
                case DataLensValueType.Int8:   view.Set<sbyte>(row, col, (sbyte)v); break;
                case DataLensValueType.UInt8:  view.Set<byte>(row, col, (byte)v); break;
                case DataLensValueType.Int16:  view.Set<short>(row, col, (short)v); break;
                case DataLensValueType.UInt16: view.Set<ushort>(row, col, (ushort)v); break;
                case DataLensValueType.Int32:  view.Set<int>(row, col, (int)v); break;
                case DataLensValueType.UInt32: view.Set<uint>(row, col, (uint)v); break;
                case DataLensValueType.Int64:  view.Set<long>(row, col, (long)v); break;
                case DataLensValueType.UInt64: view.Set<ulong>(row, col, (ulong)v); break;
                case DataLensValueType.Float:  view.Set<float>(row, col, (float)v); break;
                case DataLensValueType.Double: view.Set<double>(row, col, v); break;
            }
        }

        private static double GetTyped(DataLensView view, int row, GameplayTag col, DataLensValueType t)
        {
            switch (t)
            {
                case DataLensValueType.Bool:   return view.Get<byte>(row, col) != 0 ? 1 : 0;
                case DataLensValueType.Int8:   return view.Get<sbyte>(row, col);
                case DataLensValueType.UInt8:  return view.Get<byte>(row, col);
                case DataLensValueType.Int16:  return view.Get<short>(row, col);
                case DataLensValueType.UInt16: return view.Get<ushort>(row, col);
                case DataLensValueType.Int32:  return view.Get<int>(row, col);
                case DataLensValueType.UInt32: return view.Get<uint>(row, col);
                case DataLensValueType.Int64:  return view.Get<long>(row, col);
                case DataLensValueType.UInt64: return view.Get<ulong>(row, col);
                case DataLensValueType.Float:  return view.Get<float>(row, col);
                case DataLensValueType.Double: return view.Get<double>(row, col);
                default: return 0;
            }
        }

        public void Dispose()
        {
            _lens?.Dispose(); // disposes owned stores + all registered views (spawn views, master, OpenView views)
            _spawnViews.Clear();
            _tallInsertViews.Clear();
            _master = null;
            _catalogView = null;
        }
    }
}
