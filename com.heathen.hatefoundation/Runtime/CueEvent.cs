namespace Heathen.HATE
{
    /// <summary>
    /// A cosmetic cue event (HATE-Spec §9): a fire-and-forget signal for presentation (VFX/SFX/UI) to
    /// play. Cues are NEVER simulation state — they are an append-only output stream drained by the
    /// host each frame, so they never affect determinism. <see cref="CueId"/> is a host-defined id
    /// (e.g. a tag or enum); <see cref="Magnitude"/> carries an optional scalar (damage dealt, etc.).
    /// </summary>
    public readonly struct CueEvent
    {
        public readonly int CueId;
        public readonly int Actor;
        public readonly float Magnitude;

        public CueEvent(int cueId, int actor, float magnitude)
        {
            CueId = cueId;
            Actor = actor;
            Magnitude = magnitude;
        }
    }
}
