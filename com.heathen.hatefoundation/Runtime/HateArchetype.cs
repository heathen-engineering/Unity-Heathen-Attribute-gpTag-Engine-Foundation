using System.Collections.Generic;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// A spawn template: the traits an entity of this kind carries, and the attribute values to seed. This is
    /// ordinary spawn data (like a prefab/character template) - <see cref="HateWorld.Spawn"/> reads it to know
    /// what to insert. An archetype is not a DataLens concept; it is HATE's recipe (HATE-Spec §5). Values are
    /// carried as <see cref="double"/> and written to each attribute's actual column type on spawn.
    /// </summary>
    public sealed class HateArchetype
    {
        private readonly List<GameplayTag> _traits = new List<GameplayTag>();
        private readonly Dictionary<ulong, double> _values = new Dictionary<ulong, double>();

        /// <summary>The traits this archetype carries (defines which trait rows a spawn allocates).</summary>
        public IReadOnlyList<GameplayTag> Traits => _traits;

        /// <summary>The seeded attribute values, keyed by attribute tag id.</summary>
        public IReadOnlyDictionary<ulong, double> Values => _values;

        public HateArchetype With(GameplayTag trait)
        {
            if (!_traits.Contains(trait)) _traits.Add(trait);
            return this;
        }

        public HateArchetype Set(GameplayTag attribute, double value)
        {
            _values[(ulong)attribute] = value;
            return this;
        }
    }
}
