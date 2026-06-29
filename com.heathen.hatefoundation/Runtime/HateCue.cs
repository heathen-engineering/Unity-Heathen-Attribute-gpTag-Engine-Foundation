using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// The lifecycle moment a gameplay cue fires at (HATE-Spec §15). Cosmetic only: cues never alter
    /// authoritative state, so they are safe to fire on predicting clients and skip on dedicated servers.
    /// </summary>
    public enum HateCueType
    {
        /// <summary>An effect/status became active.</summary>
        OnActive,
        /// <summary>A periodic tick while an effect/status is active.</summary>
        WhileActive,
        /// <summary>An effect/status ended (expired, removed, dispelled).</summary>
        OnRemove,
        /// <summary>A one-shot, instantaneous cue (an instant effect, an ability hit).</summary>
        Executed,
    }

    /// <summary>
    /// One entry in the cosmetic <c>CueEvents</c> stream (HATE-Spec §15): a <see cref="Cue"/> tag plus its
    /// lifecycle <see cref="Type"/>, an optional <see cref="Magnitude"/>, and the <see cref="Source"/> entity
    /// that raised it. A presentation system drains the stream each frame and maps the tag to VFX/SFX/anim,
    /// resolved hierarchically. <b>Location is deliberately absent</b> - it is engine-specific, so the Toolkit
    /// bridge resolves a world position from the source entity; the Foundation stays engine-agnostic.
    /// </summary>
    public readonly struct HateCueEvent
    {
        public readonly GameplayTag Cue;
        public readonly HateCueType Type;
        public readonly double Magnitude;
        public readonly EntityId Source;

        public HateCueEvent(GameplayTag cue, HateCueType type, double magnitude, EntityId source)
        {
            Cue = cue;
            Type = type;
            Magnitude = magnitude;
            Source = source;
        }
    }
}
