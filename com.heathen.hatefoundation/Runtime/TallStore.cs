using System;

namespace Heathen.HATE
{
    /// <summary>
    /// A tall store for Effects or Abilities (HATE-Spec.md §6/§7): one densely-packed row per instance, many rows
    /// per entity. Each row carries its owning <see cref="EntityId"/>, its def tag (which effect/ability), and the
    /// typed-superset instance-state columns. Rows are found by scanning the entity column (not located through the
    /// directory), so removal is a plain swap-remove with no directory maintenance.
    /// </summary>
    /// <remarks>Managed v1: columns are <see cref="double"/> arrays behind this API (DataLens-backed later).</remarks>
    public sealed class TallStore
    {
        private readonly int _capacity;
        private readonly int _columnCount;
        private readonly EntityId[] _entity; // row -> owning entity
        private readonly ulong[] _tag;       // row -> def tag (effect/ability GameplayTag)
        private readonly double[][] _columns;
        private int _count;

        public TallStore(int capacity, int columnCount)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (columnCount < 0) throw new ArgumentOutOfRangeException(nameof(columnCount));

            _capacity = capacity;
            _columnCount = columnCount;
            _entity = new EntityId[capacity];
            _tag = new ulong[capacity];
            _columns = new double[columnCount][];
            for (int c = 0; c < columnCount; c++)
                _columns[c] = new double[capacity];
        }

        public int Capacity => _capacity;
        public int ColumnCount => _columnCount;
        public int Count => _count;

        /// <summary>Appends a row for (owner, tag) with zeroed columns and returns it, or -1 when full (fallible).</summary>
        public int Add(EntityId owner, ulong tag)
        {
            if (_count >= _capacity)
                return -1;
            int row = _count++;
            _entity[row] = owner;
            _tag[row] = tag;
            for (int c = 0; c < _columnCount; c++)
                _columns[c][row] = 0d;
            return row;
        }

        /// <summary>The first row for (owner, tag), or -1. Used by Refresh/Stack reapply (HATE-Spec.md §7.3).</summary>
        public int FindRow(EntityId owner, ulong tag)
        {
            for (int i = 0; i < _count; i++)
                if (_tag[i] == tag && _entity[i] == owner)
                    return i;
            return -1;
        }

        /// <summary>Removes a row by swap-remove (tall rows are scan-found, so nothing else needs updating).</summary>
        public void RemoveRow(int row)
        {
            if ((uint)row >= (uint)_count)
                return;
            int last = --_count;
            if (row != last)
            {
                for (int c = 0; c < _columnCount; c++)
                    _columns[c][row] = _columns[c][last];
                _entity[row] = _entity[last];
                _tag[row] = _tag[last];
            }
            _entity[last] = EntityId.None;
            _tag[last] = 0UL;
        }

        /// <summary>Removes every row owned by <paramref name="owner"/> (despawn cleanup). Returns the count removed.</summary>
        public int RemoveAllFor(EntityId owner)
        {
            int removed = 0;
            int i = 0;
            while (i < _count)
            {
                if (_entity[i] == owner)
                {
                    RemoveRow(i); // swaps the last row into i; re-check i without advancing
                    removed++;
                }
                else
                {
                    i++;
                }
            }
            return removed;
        }

        /// <summary>The entity that owns a row.</summary>
        public EntityId EntityOf(int row) => (uint)row < (uint)_count ? _entity[row] : EntityId.None;

        /// <summary>The def tag of a row.</summary>
        public ulong TagOf(int row) => (uint)row < (uint)_count ? _tag[row] : 0UL;

        public double Get(int row, int column) => _columns[column][row];
        public void Set(int row, int column, double value) => _columns[column][row] = value;

        /// <summary>The dense backing array of a column, scanned by a System over <c>[0, Count)</c>.</summary>
        public double[] Column(int column) => _columns[column];
    }
}
