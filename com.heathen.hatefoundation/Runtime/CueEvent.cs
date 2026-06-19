using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// A cosmetic cue event (HATE-Spec §9): a fire-and-forget signal for presentation (VFX/SFX/UI) to
    /// play. Cues are NEVER simulation state — they are an append-only output stream drained by the
    /// host each frame, so they never affect determinism. <see cref="Cue"/> is a GameplayTag identity
    /// (e.g. <c>Cues.Fireball</c>); <see cref="Magnitude"/> carries an optional scalar (damage dealt, etc.).
    /// </summary>
    public readonly struct CueEvent
    {
        public readonly GameplayTag Cue;
        public readonly ulong Actor;
        public readonly float Magnitude;

        public CueEvent(GameplayTag cue, ulong actor, float magnitude)
        {
            Cue = cue;
            Actor = actor;
            Magnitude = magnitude;
        }
    }
}
