using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// A HATE store paired with the row-index bitmask of an entity's rows in it - the interest-management input a
    /// networking provider feeds straight to <c>Lens.CollectDelta(store, sinceRevision, scope)</c> (HATE-Spec §16).
    /// </summary>
    public readonly struct HateStoreScope
    {
        public readonly GameplayTag Store;
        public readonly ulong[] Scope;
        public HateStoreScope(GameplayTag store, ulong[] scope) { Store = store; Scope = scope; }
    }

    // Networking EXPOSURE (HATE-Spec §16): HATE does no networking - it opens no socket, enforces no authority,
    // implements no prediction/rollback. It exposes the structural mapping a provider (Mirror / NGO / FishNet /
    // PurrNet / ...) would otherwise have to reverse-engineer: which stores make up replicable HATE state, and
    // which rows in each belong to a given entity. The provider then drives the DataLens Lens surface
    // (Snapshot/CollectDelta/ApplyPayload) over those stores/scopes however its own netcode replicates. Read-only.
    public sealed partial class HateWorld
    {
        private GameplayTag[] _replicatedStores;

        /// <summary>
        /// The stores that make up replicable HATE state: the entity catalog, every trait store, any user tall
        /// stores, and the effects / abilities / granted-tags stores. A provider replicates HATE by driving the
        /// DataLens Lens surface (<c>Snapshot</c> / <c>CollectDelta</c> / <c>ApplyPayload</c>) over these; HATE
        /// itself does no networking. Whole-store (bulk / headless) replication needs only this list; per-object
        /// (actor-backed) replication also uses <see cref="CollectEntityScope(EntityId)"/>.
        /// </summary>
        public IReadOnlyList<GameplayTag> ReplicatedStores
        {
            get
            {
                if (_replicatedStores == null)
                {
                    var list = new List<GameplayTag> { _catalogTag };
                    foreach (HateTrait t in _schema.Traits) list.Add(t.Id);
                    foreach (HateTallStore tall in _schema.TallStores) list.Add(tall.Id);
                    foreach (GameplayTag es in _effectStores) list.Add(es); // monolithic + type-split (§7.4)
                    if (_hasAbilities) list.Add(_abilitiesStore);
                    if (_hasGrantedTags) list.Add(_grantedStore);
                    _replicatedStores = list.ToArray();
                }
                return _replicatedStores;
            }
        }

        /// <summary>
        /// The per-store row-index bitmasks of every row that belongs to <paramref name="entity"/>: its catalog
        /// row, each trait row (1:1 via the catalog dereference), and its effect / ability / granted-tag rows
        /// (0..N via the entity-index filter). Feed each entry to
        /// <c>Lens.CollectDelta(entry.Store, sinceRevision, entry.Scope)</c>. Stores in which the entity has no
        /// rows are omitted. Read-only. An entity's row set changes as traits/effects come and go, so a provider
        /// re-queries per replication tick; a row that has left the scope is the provider's own dropout to handle.
        /// </summary>
        public IReadOnlyList<HateStoreScope> CollectEntityScope(EntityId entity)
        {
            var result = new List<HateStoreScope> { new HateStoreScope(_catalogTag, BitmaskOf(entity.Index)) };

            // Trait rows: one master refresh resolves the entity's row in every trait store it carries.
            DataLensView m = Master();
            m.Refresh();
            int vr = FindRow(m, entity.Index);
            if (vr >= 0)
            {
                foreach (HateTrait trait in _schema.Traits)
                {
                    if (!_masterTraitJoin.TryGetValue((ulong)trait.Id, out int j)) continue;
                    long row = m.SourceJoinRow(vr, j);
                    if (row >= 0) result.Add(new HateStoreScope(trait.Id, BitmaskOf((int)row)));
                }
            }

            // Tall stores (user tall + effects + abilities + granted): 0..N rows where EntityIndex == entity.
            foreach (KeyValuePair<ulong, GameplayTag> kv in _entityIndexTag)
            {
                var store = new GameplayTag(kv.Key);
                ulong[] scope = ScanTallRows(store, kv.Value, entity.Index);
                if (scope.Length > 0) result.Add(new HateStoreScope(store, scope));
            }
            return result;
        }

        /// <summary>
        /// The row-index bitmask of <paramref name="entity"/>'s rows in a single <paramref name="store"/> (the
        /// catalog, a trait store, or a tall store). Empty when the entity has no rows there or the store is not
        /// replicable HATE state. See <see cref="CollectEntityScope(EntityId)"/>.
        /// </summary>
        public ulong[] CollectEntityScope(EntityId entity, GameplayTag store)
        {
            if ((ulong)store == (ulong)_catalogTag) return BitmaskOf(entity.Index);

            if (_entityIndexTag.TryGetValue((ulong)store, out GameplayTag ei))
                return ScanTallRows(store, ei, entity.Index);

            if (_traitOf.ContainsKey((ulong)store))
            {
                DataLensView m = Master();
                m.Refresh();
                int vr = FindRow(m, entity.Index);
                if (vr >= 0 && _masterTraitJoin.TryGetValue((ulong)store, out int j))
                {
                    long row = m.SourceJoinRow(vr, j);
                    if (row >= 0) return BitmaskOf((int)row);
                }
            }
            return Array.Empty<ulong>();
        }

        // Rows in a tall store owned by the entity (EntityIndex == entity), as a row-index bitmask.
        private ulong[] ScanTallRows(GameplayTag store, GameplayTag entityIndexCol, int entityIndex)
        {
            using (DataLensView v = OpenStoreView(store, new[] { entityIndexCol },
                       new DataLensFilter().Eq(entityIndexCol, entityIndex)))
            {
                v.Refresh();
                int n = v.RowCount;
                var rows = new List<int>(n);
                int max = 0;
                for (int i = 0; i < n; i++)
                {
                    long r = v.SourceRow(i);
                    if (r < 0) continue;
                    rows.Add((int)r);
                    if ((int)r > max) max = (int)r;
                }
                if (rows.Count == 0) return Array.Empty<ulong>();
                var bits = new ulong[(max >> 6) + 1];
                foreach (int r in rows) bits[r >> 6] |= 1UL << (r & 63);
                return bits;
            }
        }

        private static ulong[] BitmaskOf(int row)
        {
            if (row < 0) return Array.Empty<ulong>();
            var bits = new ulong[(row >> 6) + 1];
            bits[row >> 6] |= 1UL << (row & 63);
            return bits;
        }
    }
}
