using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>How a tag's raw 64-bit <see cref="GameplayTagCollection"/> storage should be read as a number.</summary>
    public enum HateNumericKind : byte { Int, Long, Float, Double, ULong }

    /// <summary>
    /// A read-only, pure-data snapshot of an entity's state (HATE-Spec §8/§10; the redesign's "DataState"): an
    /// <see cref="EntityId"/> plus a tag-addressed <see cref="GameplayTagCollection"/> carrying whatever state is
    /// relevant — stored attributes flashed from a view, granted-tag/effect presence, and transient values an
    /// external system injected (velocity, airborne). It is the value handed to predicates, ability invocation
    /// and effect application, so those never reach for a view or an engine object: they query it by tag. Being a
    /// materialised packet (not a view reference) it is engine-agnostic — the same shape works for a GameObject
    /// game and an ECS job (copy it as a value into the job). Read-only: all mutation goes through the world.
    /// </summary>
    public readonly struct EntityRecord
    {
        /// <summary>Who this record is for.</summary>
        public readonly EntityId Entity;

        private readonly GameplayTagCollection _state;

        public EntityRecord(EntityId entity, GameplayTagCollection state)
        {
            Entity = entity;
            _state = state;
        }

        /// <summary>True when backed by a real state collection.</summary>
        public bool IsValid => _state != null;

        /// <summary>The underlying tag-addressed state (read from; do not mutate through a record).</summary>
        public GameplayTagCollection State => _state;

        /// <summary>Presence test (a granted tag / active effect / ability). Absent tags read as not-present.</summary>
        public bool Has(GameplayTag tag) => _state != null && _state.GetValue(tag) != 0UL;

        public ulong  GetULong (GameplayTag tag) => _state != null ? _state.GetValue(tag)  : 0UL;
        public int    GetInt   (GameplayTag tag) => _state != null ? _state.GetInt(tag)    : 0;
        public long   GetLong  (GameplayTag tag) => _state != null ? _state.GetLong(tag)   : 0L;
        public float  GetFloat (GameplayTag tag) => _state != null ? _state.GetFloat(tag)  : 0f;
        public double GetDouble(GameplayTag tag) => _state != null ? _state.GetDouble(tag) : 0.0;

        /// <summary>
        /// Read a tag as a <see cref="double"/> regardless of how it was stored (the numeric read predicates use).
        /// The <paramref name="kind"/> is the attribute's authored datatype so the raw bits are interpreted right.
        /// </summary>
        public double GetNumber(GameplayTag tag, HateNumericKind kind)
        {
            switch (kind)
            {
                case HateNumericKind.Int:    return GetInt(tag);
                case HateNumericKind.Long:   return GetLong(tag);
                case HateNumericKind.Float:  return GetFloat(tag);
                case HateNumericKind.ULong:  return GetULong(tag);
                default:                     return GetDouble(tag);
            }
        }
    }
}
