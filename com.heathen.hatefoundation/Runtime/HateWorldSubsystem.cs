using System;
using System.Collections.Generic;
using Heathen;

namespace Heathen.HATE
{
    /// <summary>
    /// World-scoped framework subsystem that owns and drives a <see cref="HateWorld"/> for its framework world.
    /// Gameplay reaches it via <c>world.Get&lt;HateWorldSubsystem&gt;().World</c>; the subsystem disposes the
    /// world on world teardown (releasing the native DataLens stores).
    /// <para>
    /// HATE worlds are game-shaped (capacities, which archetypes) so the subsystem does not auto-build one: game
    /// code constructs it (typically the baked <c>Hate.Schema.BuildWorld(capacity)</c>) and hands it over with
    /// <see cref="Adopt"/>. But once adopted, the subsystem <b>drives HATE's time-based machinery on the fixed
    /// step</b> (<see cref="IOnFixed"/> → <see cref="HateWorld.TickEffects"/> + <see cref="HateWorld.TickCooldowns"/>)
    /// so effects expire and cooldowns roll without the consumer hand-wiring an Update, deterministically (fixed
    /// delta) as the large-scale/Wyrd use cases require. Consumers compose their own Trait/Entity Systems into the
    /// same tick by subscribing to <see cref="Ticked"/> (fires after the built-in machinery each fixed step).
    /// Ability activation and the GameObject bridge stay consumer-driven (drained when the game decides).
    /// </para>
    /// </summary>
    [Subsystem(SubsystemScope.World)]
    public sealed class HateWorldSubsystem : Subsystem, ISubsystemDebug, IOnFixed
    {
        /// <summary>The HATE world for this framework world, or <c>null</c> until one is adopted.</summary>
        public HateWorld World { get; private set; }

        /// <summary>Whether a world has been adopted into this subsystem.</summary>
        public bool HasWorld => World != null;

        /// <summary>
        /// Whether the subsystem advances the adopted world's time-based machinery (effects + cooldowns) on the
        /// fixed step. Default <c>true</c>. Set <c>false</c> to drive the tick yourself (e.g. a custom cadence or
        /// a server that steps HATE on its own schedule).
        /// </summary>
        public bool AutoTick { get; set; } = true;

        /// <summary>
        /// Raised after each automatic fixed-step tick (after effects + cooldowns), with the world and the fixed
        /// delta. The composition hook for consumers — including Wyrd — to run their own Trait/Entity Systems in
        /// order within the framework tick without hand-wiring an Update. Only fires while <see cref="AutoTick"/>
        /// is on and a world is adopted.
        /// </summary>
        public event Action<HateWorld, float> Ticked;

        /// <summary>
        /// Hands a game-constructed <see cref="HateWorld"/> to the subsystem, which then owns its lifetime and
        /// disposes it on world teardown. Adopting a different world disposes the previously adopted one.
        /// </summary>
        /// <param name="world">The world to adopt.</param>
        /// <returns><paramref name="world"/>, for call-site convenience.</returns>
        public HateWorld Adopt(HateWorld world)
        {
            if (!ReferenceEquals(World, world))
            {
                World?.Dispose();
                World = world;
            }
            return world;
        }

        /// <summary>Disposes and clears the adopted world, if any.</summary>
        public void Clear()
        {
            World?.Dispose();
            World = null;
        }

        /// <summary>Disposes the adopted world when the framework world is torn down.</summary>
        protected override void Deinitialize() => Clear();

        /// <summary>Advances the adopted world's time-based machinery each fixed step (when <see cref="AutoTick"/>).</summary>
        public void OnFixed(float deltaTime)
        {
            var w = World;
            if (w == null || !AutoTick) return;
            w.TickEffects(deltaTime);
            w.TickCooldowns(deltaTime);
            Ticked?.Invoke(w, deltaTime);
        }

        /// <inheritdoc/>
        public IEnumerable<(string label, string value)> GetDebugInfo()
        {
            yield return ("World", HasWorld ? "adopted" : "(none)");
            yield return ("Auto-tick", AutoTick ? "on (fixed step)" : "off");
        }
    }
}
