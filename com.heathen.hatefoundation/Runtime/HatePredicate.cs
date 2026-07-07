using System.Collections.Generic;
using Heathen.DataLens;      // CompareOp (SystemOps.cs)
using Heathen.GameplayTags;

namespace Heathen.HATE
{
    /// <summary>
    /// What a predicate is handed to make its yes/no decision (HATE-Spec §8; the redesign): the
    /// <see cref="Subject"/> record it reads by tag, the <see cref="Instigator"/> (who caused this — for procs
    /// and receive-side checks), and a deterministic <see cref="Rng"/>. Passed <c>ref</c> so a
    /// <see cref="HateChance"/> can advance the RNG in place while the whole tree shares one stream.
    /// </summary>
    public struct HatePredicateContext
    {
        /// <summary>The entity being tested (its stored attributes + granted tags + injected params, by tag).</summary>
        public EntityRecord Subject;
        /// <summary>Who caused the thing being gated (attacker, applier). <see cref="EntityId.None"/> if none.</summary>
        public EntityId Instigator;
        /// <summary>The deterministic stream for <see cref="HateChance"/> and any randomised leaf.</summary>
        public HateRng Rng;

        public HatePredicateContext(EntityRecord subject, EntityId instigator, HateRng rng)
        {
            Subject = subject;
            Instigator = instigator;
            Rng = rng;
        }
    }

    /// <summary>
    /// A composable eligibility test that returns a simple boolean (HATE-Spec §8, the redesign). HATE ships a set
    /// of optimised built-ins (below) authored as data in the Forge; a game defines its own by implementing this
    /// interface and registering it by tag (<see cref="HatePredicateRegistry"/>) — the escape hatch for anything
    /// HATE can't know (is-airborne, moving-at-velocity, external-system state). A predicate only reads its
    /// context, so it is pure and engine-agnostic.
    /// </summary>
    public interface IHatePredicate
    {
        bool Evaluate(ref HatePredicateContext ctx);
    }

    /// <summary>Boolean helper shared by attribute-compare leaves (mirrors GAS/GameplayTags comparison ops).</summary>
    public static class HateCompare
    {
        public static bool Eval(double a, CompareOp op, double b)
        {
            switch (op)
            {
                case CompareOp.Always:       return true;
                case CompareOp.Equal:        return a == b;
                case CompareOp.NotEqual:     return a != b;
                case CompareOp.Less:         return a < b;
                case CompareOp.LessEqual:    return a <= b;
                case CompareOp.Greater:      return a > b;
                case CompareOp.GreaterEqual: return a >= b;
                default:                     return true;
            }
        }
    }

    // ── Combinators (the All/Any/None tree) ──────────────────────────────────────────────────────────────

    /// <summary>All children must hold (AND). An empty set is vacuously true.</summary>
    public sealed class HateAll : IHatePredicate
    {
        private readonly IHatePredicate[] _children;
        public HateAll(params IHatePredicate[] children) => _children = children ?? System.Array.Empty<IHatePredicate>();
        public bool Evaluate(ref HatePredicateContext ctx)
        {
            for (int i = 0; i < _children.Length; i++)
                if (!_children[i].Evaluate(ref ctx)) return false;
            return true;
        }
    }

    /// <summary>At least one child must hold (OR). An empty set is false.</summary>
    public sealed class HateAny : IHatePredicate
    {
        private readonly IHatePredicate[] _children;
        public HateAny(params IHatePredicate[] children) => _children = children ?? System.Array.Empty<IHatePredicate>();
        public bool Evaluate(ref HatePredicateContext ctx)
        {
            for (int i = 0; i < _children.Length; i++)
                if (_children[i].Evaluate(ref ctx)) return true;
            return false;
        }
    }

    /// <summary>No child may hold (NOR). An empty set is true.</summary>
    public sealed class HateNone : IHatePredicate
    {
        private readonly IHatePredicate[] _children;
        public HateNone(params IHatePredicate[] children) => _children = children ?? System.Array.Empty<IHatePredicate>();
        public bool Evaluate(ref HatePredicateContext ctx)
        {
            for (int i = 0; i < _children.Length; i++)
                if (_children[i].Evaluate(ref ctx)) return false;
            return true;
        }
    }

    // ── Leaves (the built-in catalogue) ──────────────────────────────────────────────────────────────────

    /// <summary>Compare a subject attribute against a literal (<c>Stamina &gt;= 20</c>).</summary>
    public sealed class HateAttr : IHatePredicate
    {
        private readonly GameplayTag _tag;
        private readonly CompareOp _op;
        private readonly double _value;
        private readonly HateNumericKind _kind;
        public HateAttr(GameplayTag tag, CompareOp op, double value, HateNumericKind kind = HateNumericKind.Double)
        { _tag = tag; _op = op; _value = value; _kind = kind; }
        public bool Evaluate(ref HatePredicateContext ctx)
            => HateCompare.Eval(ctx.Subject.GetNumber(_tag, _kind), _op, _value);
    }

    /// <summary>Requires a granted tag / active effect / ability to be present on the subject.</summary>
    public sealed class HatePresent : IHatePredicate
    {
        private readonly GameplayTag _tag;
        public HatePresent(GameplayTag tag) => _tag = tag;
        public bool Evaluate(ref HatePredicateContext ctx) => ctx.Subject.Has(_tag);
    }

    /// <summary>Requires a tag / effect / ability to be absent (not Silenced, not on cooldown, …).</summary>
    public sealed class HateAbsent : IHatePredicate
    {
        private readonly GameplayTag _tag;
        public HateAbsent(GameplayTag tag) => _tag = tag;
        public bool Evaluate(ref HatePredicateContext ctx) => !ctx.Subject.Has(_tag);
    }

    /// <summary>True with probability <c>p</c>, drawn from the context's deterministic stream.</summary>
    public sealed class HateChance : IHatePredicate
    {
        private readonly double _p;
        public HateChance(double probability) => _p = probability;
        public bool Evaluate(ref HatePredicateContext ctx) => ctx.Rng.Chance(_p);
    }

    /// <summary>References a custom predicate by tag, resolved from <see cref="HatePredicateRegistry"/> at eval.</summary>
    public sealed class HateCustom : IHatePredicate
    {
        private readonly GameplayTag _tag;
        public HateCustom(GameplayTag tag) => _tag = tag;
        public bool Evaluate(ref HatePredicateContext ctx)
            => HatePredicateRegistry.TryGet(_tag, out IHatePredicate p) && p.Evaluate(ref ctx);
    }

    /// <summary>
    /// The registry of custom, code-defined predicates, keyed by GameplayTag so authored data can reference them
    /// (e.g. <c>"MyGame.Pred.IsAirborne"</c>). Populated by the game at startup (or discovered by the editor via
    /// TypeCache). HATE resolves a <see cref="HateCustom"/> reference through here.
    /// </summary>
    public static class HatePredicateRegistry
    {
        private static readonly Dictionary<ulong, IHatePredicate> _byTag = new Dictionary<ulong, IHatePredicate>();

        public static void Register(GameplayTag tag, IHatePredicate predicate) => _byTag[(ulong)tag] = predicate;
        public static bool TryGet(GameplayTag tag, out IHatePredicate predicate) => _byTag.TryGetValue((ulong)tag, out predicate);
        public static bool IsRegistered(GameplayTag tag) => _byTag.ContainsKey((ulong)tag);
        public static void Clear() => _byTag.Clear();
    }
}
