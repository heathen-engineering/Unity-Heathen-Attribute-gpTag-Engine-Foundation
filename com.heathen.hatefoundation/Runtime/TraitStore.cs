using System;

namespace Heathen.HATE
{
    /// <summary>
    /// A densely-packed per-trait store (HATE-Spec.md §5): one row per entity that has the trait, with a fixed
    /// set of attribute columns. Rows are appended on <see cref="AllocRow"/> and removed by swap-remove on
    /// <see cref="FreeRow"/>, which reports the entity moved into the freed slot so the caller updates the
    /// EntityStore directory (so local rows stay correct, §7). A reverse owner column makes that report O(1).
    /// </summary>
    /// <remarks>
    /// Managed v1: columns are <see cref="double"/> arrays (single/double/integral all round-trip through a
    /// double for now). A DataLens-backed variant replaces the storage with range-narrowed, type-correct columns
    /// later without changing this surface. <see cref="Column"/> exposes the dense backing array a Trait System
    /// scans over <c>[0, Count)</c>.
    /// </remarks>
    public sealed class TraitStore
    {
        private readonly int _capacity;
        private readonly int _columnCount;
        private readonly double[][] _columns; // [column][row]
        private readonly EntityId[] _owner;   // row -> owning entity (for swap-remove reporting)
        private int _count;

        public TraitStore(int capacity, int columnCount)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (columnCount < 0) throw new ArgumentOutOfRangeException(nameof(columnCount));

            _capacity = capacity;
            _columnCount = columnCount;
            _owner = new EntityId[capacity];
            _columns = new double[columnCount][];
            for (int c = 0; c < columnCount; c++)
                _columns[c] = new double[capacity];
        }

        /// <summary>The maximum number of rows.</summary>
        public int Capacity => _capacity;

        /// <summary>The number of attribute columns.</summary>
        public int ColumnCount => _columnCount;

        /// <summary>The number of live (dense) rows.</summary>
        public int Count => _count;

        /// <summary>
        /// Appends a row for <paramref name="owner"/> and returns its local row index, or -1 when the store is
        /// full (fallible, §5). Column values for the new row start at 0.
        /// </summary>
        public int AllocRow(EntityId owner)
        {
            if (_count >= _capacity)
                return -1;
            int row = _count++;
            _owner[row] = owner;
            for (int c = 0; c < _columnCount; c++)
                _columns[c][row] = 0d;
            return row;
        }

        /// <summary>
        /// Removes <paramref name="row"/> by swap-remove. Returns the entity that was moved into the freed slot
        /// (so the caller can update its directory local row to <paramref name="row"/>), or
        /// <see cref="EntityId.None"/> when the removed row was the last one (nothing moved).
        /// </summary>
        public EntityId FreeRow(int row)
        {
            if ((uint)row >= (uint)_count)
                return EntityId.None;

            int last = --_count;
            EntityId moved = EntityId.None;
            if (row != last)
            {
                for (int c = 0; c < _columnCount; c++)
                    _columns[c][row] = _columns[c][last];
                _owner[row] = _owner[last];
                moved = _owner[row];
            }
            _owner[last] = EntityId.None;
            return moved;
        }

        /// <summary>The entity that owns a row (the reverse of the directory's forward lookup).</summary>
        public EntityId OwnerOf(int row) => (uint)row < (uint)_count ? _owner[row] : EntityId.None;

        /// <summary>Reads an attribute column value at a row.</summary>
        public double Get(int row, int column) => _columns[column][row];

        /// <summary>Writes an attribute column value at a row.</summary>
        public void Set(int row, int column, double value) => _columns[column][row] = value;

        /// <summary>
        /// The dense backing array of a column. A Trait System scans/writes it over <c>[0, Count)</c> (the
        /// column-major fast path, §6.1). The array length is <see cref="Capacity"/>; only the first
        /// <see cref="Count"/> entries are live.
        /// </summary>
        public double[] Column(int column) => _columns[column];
    }
}
