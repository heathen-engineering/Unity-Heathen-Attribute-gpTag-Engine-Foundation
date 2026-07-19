using System;

namespace Heathen.HATE
{
    /// <summary>
    /// The EntityStore directory (HATE-Spec.md §7): the registry that locates an entity across its per-trait
    /// stores. The directory row index is the entity's <c>slot</c>; the <see cref="EntityId"/> is slot +
    /// generation. One cell per (slot, trait) holds the entity's local row within that trait's store, or
    /// <see cref="NoRow"/> when the entity does not have the trait, so a cell carries membership AND location
    /// (a free trait-gated test, §7). Capacity is fixed, so <see cref="Spawn"/> is fallible (§5); despawn bumps
    /// the slot's generation for safe reuse and netcode tombstones (§13).
    /// </summary>
    /// <remarks>
    /// Managed v1: the columns are plain arrays behind this API. A DataLens-backed variant (range-narrowed
    /// directory columns) can replace the storage later without changing the surface.
    /// </remarks>
    public sealed class EntityStore
    {
        /// <summary>Directory cell value meaning "this entity does not have this trait".</summary>
        public const int NoRow = -1;

        private readonly int _capacity;
        private readonly int _traitCount;
        private readonly uint[] _generation; // current live generation per slot (starts at 1, bumped on despawn)
        private readonly bool[] _alive;
        private readonly int[] _localRow;     // [slot * traitCount + trait] = local row, or NoRow
        private readonly int[] _freeStack;    // recycled slots
        private int _freeTop;
        private int _nextFresh;
        private int _aliveCount;

        public EntityStore(int capacity, int traitCount)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (traitCount < 0) throw new ArgumentOutOfRangeException(nameof(traitCount));

            _capacity = capacity;
            _traitCount = traitCount;
            _generation = new uint[capacity];
            _alive = new bool[capacity];
            _localRow = new int[capacity * traitCount];
            _freeStack = new int[capacity];

            for (int i = 0; i < capacity; i++)
                _generation[i] = 1;
            for (int i = 0; i < _localRow.Length; i++)
                _localRow[i] = NoRow;
        }

        /// <summary>The maximum number of concurrent entities.</summary>
        public int Capacity => _capacity;

        /// <summary>The number of trait columns the directory tracks.</summary>
        public int TraitCount => _traitCount;

        /// <summary>The number of currently-live entities.</summary>
        public int AliveCount => _aliveCount;

        /// <summary>
        /// Allocates an entity (slot + current generation). Returns <see cref="EntityId.None"/> when the store is
        /// full (fallible spawn): callers degrade gracefully rather than throwing.
        /// </summary>
        public EntityId Spawn()
        {
            int slot;
            if (_freeTop > 0)
                slot = _freeStack[--_freeTop];
            else if (_nextFresh < _capacity)
                slot = _nextFresh++;
            else
                return EntityId.None;

            _alive[slot] = true;
            _aliveCount++;
            return EntityId.From((uint)slot, _generation[slot]);
        }

        /// <summary>True if the handle refers to a currently-live entity (generation-checked, so stale handles fail).</summary>
        public bool IsAlive(EntityId e)
        {
            uint slot = e.Slot;
            return slot < _capacity && _alive[slot] && _generation[slot] == e.Generation;
        }

        /// <summary>
        /// Despawns an entity: clears its trait cells, bumps its generation (stale handles now mismatch), and frees
        /// the slot. Returns false (no-op) for a stale or invalid handle.
        /// </summary>
        public bool Despawn(EntityId e)
        {
            if (!IsAlive(e))
                return false;

            int slot = (int)e.Slot;
            int baseIndex = slot * _traitCount;
            for (int t = 0; t < _traitCount; t++)
                _localRow[baseIndex + t] = NoRow;

            _alive[slot] = false;
            _generation[slot]++; // safe reuse + tombstone
            _freeStack[_freeTop++] = slot;
            _aliveCount--;
            return true;
        }

        /// <summary>Records (or updates) the entity's local row in a trait store. No-op for a stale handle or bad trait index.</summary>
        public void SetLocalRow(EntityId e, int trait, int localRow)
        {
            if (!IsAlive(e) || (uint)trait >= (uint)_traitCount)
                return;
            _localRow[(int)e.Slot * _traitCount + trait] = localRow;
        }

        /// <summary>The entity's local row in a trait store, or <see cref="NoRow"/> if it lacks the trait or is stale.</summary>
        public int GetLocalRow(EntityId e, int trait)
        {
            if (!IsAlive(e) || (uint)trait >= (uint)_traitCount)
                return NoRow;
            return _localRow[(int)e.Slot * _traitCount + trait];
        }

        /// <summary>True if the live entity carries the trait (membership = the directory cell is set).</summary>
        public bool HasTrait(EntityId e, int trait) => GetLocalRow(e, trait) != NoRow;

        /// <summary>Removes the entity from a trait (clears its directory cell). No-op for a stale handle.</summary>
        public void ClearTrait(EntityId e, int trait)
        {
            if (!IsAlive(e) || (uint)trait >= (uint)_traitCount)
                return;
            _localRow[(int)e.Slot * _traitCount + trait] = NoRow;
        }
    }
}
