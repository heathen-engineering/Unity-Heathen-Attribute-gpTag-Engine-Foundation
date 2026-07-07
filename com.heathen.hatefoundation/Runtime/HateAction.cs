using System.Collections.Generic;
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// Which resolved entity set an action step aims at (HATE-Spec §8; the redesign). An ability invocation
    /// carries a caster, a source set (who pays) and a target set (who receives); <see cref="Instigator"/> is who
    /// caused this (procs). Effects re-use <see cref="Caster"/> as "this entity" and <see cref="Instigator"/>.
    /// </summary>
    public enum HateSlot { Caster, Sources, Targets, Instigator }

    /// <summary>
    /// The mutable state threaded through an <see cref="HateAction"/> while it runs: the resolved entity sets, the
    /// <see cref="Subject"/> record a step's guard reads, and the deterministic <see cref="Rng"/>. Passed
    /// <c>ref</c> so a guard's <see cref="HateChance"/> and a <see cref="HateFork"/> draw from the same stream.
    /// </summary>
    public struct HateActionContext
    {
        /// <summary>The activator (OnEntity) — a self-targeted step and effect instigation resolve to it.</summary>
        public EntityId Caster;
        /// <summary>Who pays the cost (defaults to just the caster). May be empty.</summary>
        public EntityId[] Sources;
        /// <summary>Who receives the result (supplied by the game). May be empty.</summary>
        public EntityId[] Targets;
        /// <summary>Who caused this activation/application (procs / receive-side). <see cref="EntityId.None"/> if none.</summary>
        public EntityId Instigator;
        /// <summary>The activator's state, read by step guards (attributes / granted tags / injected params).</summary>
        public EntityRecord Subject;
        /// <summary>The deterministic stream shared by guards (Chance) and forks.</summary>
        public HateRng Rng;

        public HateActionContext(EntityId caster, EntityId[] sources, EntityId[] targets,
            EntityId instigator, EntityRecord subject, HateRng rng)
        {
            Caster = caster;
            Sources = sources;
            Targets = targets;
            Instigator = instigator;
            Subject = subject;
            Rng = rng;
        }

        internal IReadOnlyList<EntityId> Resolve(HateSlot slot)
        {
            switch (slot)
            {
                case HateSlot.Sources:    return Sources ?? System.Array.Empty<EntityId>();
                case HateSlot.Targets:    return Targets ?? System.Array.Empty<EntityId>();
                case HateSlot.Instigator: return Instigator.IsValid ? new[] { Instigator } : System.Array.Empty<EntityId>();
                default:                  return new[] { Caster };
            }
        }
    }

    /// <summary>
    /// One step of an ability's Action (HATE-Spec §8; the redesign): an optional <see cref="Guard"/> predicate
    /// and a <see cref="Run"/> that does something (apply effects, raise an event, fork). Steps are executed in
    /// authored order by <see cref="HateAction"/>. HATE ships a small, closed set of step kinds (below); anything
    /// exotic goes through a custom predicate guard or a raised event the game handles - never a new step kind.
    /// </summary>
    public interface IHateActionStep
    {
        /// <summary>Runs only when this predicate holds over the context's <see cref="HateActionContext.Subject"/>. Null = always.</summary>
        IHatePredicate Guard { get; }

        void Run(HateWorld world, ref HateActionContext ctx);
    }

    /// <summary>An ordered list of guarded steps — "what an ability does" once it passes its eligibility predicate.</summary>
    public sealed class HateAction
    {
        private readonly IHateActionStep[] _steps;
        public HateAction(params IHateActionStep[] steps) => _steps = steps ?? System.Array.Empty<IHateActionStep>();

        public void Execute(HateWorld world, ref HateActionContext ctx)
        {
            for (int i = 0; i < _steps.Length; i++)
            {
                var step = _steps[i];
                if (GuardPasses(step.Guard, ref ctx))
                    step.Run(world, ref ctx);
            }
        }

        // Evaluate a step's guard against the context, sharing the one RNG stream (so Chance advances it).
        internal static bool GuardPasses(IHatePredicate guard, ref HateActionContext ctx)
        {
            if (guard == null) return true;
            var p = new HatePredicateContext(ctx.Subject, ctx.Instigator, ctx.Rng);
            bool ok = guard.Evaluate(ref p);
            ctx.Rng = p.Rng;
            return ok;
        }
    }

    // ── Step kinds (the closed set) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply one or more effects to a resolved slot. Cost = apply to <see cref="HateSlot.Sources"/>; result =
    /// apply to <see cref="HateSlot.Targets"/>; a reflect/thorns = apply to <see cref="HateSlot.Instigator"/>.
    /// The caster is recorded as the instigator so the applied effect can itself proc (<see cref="HateWorld.EffectAppliedBy"/>).
    /// </summary>
    public sealed class HateApplyEffects : IHateActionStep
    {
        private readonly GameplayTag[] _effects;
        private readonly HateSlot _slot;
        public IHatePredicate Guard { get; }

        public HateApplyEffects(HateSlot slot, GameplayTag[] effects, IHatePredicate guard = null)
        {
            _slot = slot;
            _effects = effects ?? System.Array.Empty<GameplayTag>();
            Guard = guard;
        }

        public void Run(HateWorld world, ref HateActionContext ctx)
        {
            var entities = ctx.Resolve(_slot);
            for (int e = 0; e < entities.Count; e++)
                for (int f = 0; f < _effects.Length; f++)
                    // Unified dispatch: instant vs duration per the effect's registered timing. Instigator = the
                    // caster who ran this action (so an applied effect can itself proc via EffectAppliedBy). The
                    // context RNG is threaded so ranged magnitudes draw from this activation's one stream, and the
                    // caster's Subject record is the SetByCaller payload (injected magnitudes read by tag).
                    world.ApplyEffect(entities[e], _effects[f], ctx.Caster, ref ctx.Rng, ctx.Subject);
        }
    }

    /// <summary>Raise a cosmetic/game event (a cue) other systems react to — the seam for HATE-data-free results
    /// (a jump, a dodge, VFX). Fires <see cref="HateWorld.RaiseCue"/> with the caster as source.</summary>
    public sealed class HateRaiseEvent : IHateActionStep
    {
        private readonly GameplayTag _cue;
        public IHatePredicate Guard { get; }
        public HateRaiseEvent(GameplayTag cue, IHatePredicate guard = null) { _cue = cue; Guard = guard; }

        public void Run(HateWorld world, ref HateActionContext ctx)
            => world.RaiseCue(_cue, HateCueType.Executed, 0, ctx.Caster);
    }

    /// <summary>
    /// A weighted, mutually-exclusive branch (the Ogham Fork Node reused): draw one branch by weight from the
    /// shared RNG and run its sub-action. Covers "chance to X else Y", "always X + sometimes Y", combos. Weights
    /// need not sum to 1 (they are normalised by their total); a zero-total fork does nothing.
    /// </summary>
    public sealed class HateFork : IHateActionStep
    {
        public readonly struct Branch
        {
            public readonly double Weight;
            public readonly HateAction Action;
            public Branch(double weight, HateAction action) { Weight = weight < 0 ? 0 : weight; Action = action; }
        }

        private readonly Branch[] _branches;
        public IHatePredicate Guard { get; }

        public HateFork(Branch[] branches, IHatePredicate guard = null)
        {
            _branches = branches ?? System.Array.Empty<Branch>();
            Guard = guard;
        }

        public void Run(HateWorld world, ref HateActionContext ctx)
        {
            double total = 0;
            for (int i = 0; i < _branches.Length; i++) total += _branches[i].Weight;
            if (total <= 0) return;

            double roll = ctx.Rng.NextDouble() * total;
            double acc = 0;
            for (int i = 0; i < _branches.Length; i++)
            {
                acc += _branches[i].Weight;
                if (roll < acc)
                {
                    _branches[i].Action?.Execute(world, ref ctx);
                    return;
                }
            }
            // Floating-point tail: fall to the last non-empty branch.
            _branches[_branches.Length - 1].Action?.Execute(world, ref ctx);
        }
    }
}
