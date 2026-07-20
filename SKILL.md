---
name: heathen-hate-unity-foundation
description: Orientation for an agent writing gameplay code against HATE (Heathen Attribute [gp]Tag Engine) — a data-oriented, GameplayTag-addressed attribute/effect/ability runtime for Unity (Heathen's answer to Unreal's GAS).
---

# Attribute gpTag Engine Foundation / HATE (Unity)

HATE (**H**eathen **A**ttribute gp**T**ag **E**ngine — "gpTag" = gameplay tag) is a data-oriented
attribute/effect/ability runtime for Unity: not an OOP component system, a columnar one. Every
entity is a row in an `EntityCatalog`; every attribute is a column addressed by a `GameplayTag`
(`Combat.Attributes.Health`). Effects and abilities are tall stores (one row per instance) with
GAS-style aggregation (`Current = override ?? (Base + ΣAdd)·ΠMul`), stacking, cooldowns, charges,
and cues. It's Heathen's answer to Unreal's GAS, built to simulate 100k+ actors under active
effects as branchless column passes with zero per-effect GC.

HATE is deliberately narrow in scope: it's the deterministic *mechanism* layer (given an
invocation, what happens to which entities), not decision-making — choosing among candidates
(utility AI) is a separate, later product ("Wyrd"), not HATE's job.

## Foundation/Toolkit relationship

**This repo is the Foundation tier** — the FOSS (Apache 2.0) runtime, package
`com.heathen.hatefoundation`, namespace `Heathen.HATE`. It owns the entity catalog, the
attribute/trait/effect/ability data model, spawn/despawn, effect aggregation, ability resolution,
and replication-exposure hooks. It ships no editor tooling.

The paid **Toolkit** (visual authoring "Forge" window, per-actor runtime debugger, codegen
pipeline, and the engine-object bridge MonoBehaviours) lives inside the private `SourceRepo` at
`Unity/ToolkitSource/Assets/Toolkits/com.heathen.hatetoolkit/` (namespace `Heathen.HATE.Toolkit`)
— see that folder's own `SKILL.md` for its surface. This Foundation is fully usable standalone
(write your own schema/spawn code by hand, as in Quick start below); the Toolkit adds authoring
convenience and codegen on top, it doesn't add runtime capability the Foundation lacks.

HATE is Unity-exclusive today — no O3DE/Godot/Unreal port exists.

## Up

[`github.com/heathen-engineering/SourceRepo/SKILL.md`](https://github.com/heathen-engineering/SourceRepo/blob/main/SKILL.md)
(ecosystem-level guide — no local engine-level file to link to from a standalone Foundation repo).

## Key namespaces / entry points

Namespace: `Heathen.HATE`. Everything lives under `com.heathen.hatefoundation/Runtime/`.

| Type | File | Purpose |
| :--- | :--- | :--- |
| `HateWorld : IDisposable` | `HateWorld.cs` | The core, ~90 public members. Spawn/despawn, attribute get/set, effects (`DefineEffect`/`ApplyInstant`/`ApplyEffect`/`AddEffect`/`TickEffects`), abilities (`DefineAbility`/`GrantAbility`/`Activate`/`TickCooldowns`), granted tags, cues, replication scope, `Seed(ulong)` for the deterministic world RNG. |
| `EntityId` (readonly struct) | `EntityId.cs` | Entity handle = bare `EntityCatalog` row index (wraps a `ulong`, `EntityId.None = ulong.MaxValue`). **No baked-in generation/slot packing** — stale-handle safety is opt-in, authored as an ordinary trait if a game needs it. |
| `EntityRecord` | `EntityRecord.cs` | Hydrated per-entity snapshot passed to predicates/actions (`GetNumber`, `Has`, etc.). |
| `HateSchema`, `HateAttribute`, `HateTrait` | `HateSchema.cs` | World declaration: traits (per-entity attribute stores), tall stores, dedicated Effects/Abilities/GrantedTags stores. |
| `HateArchetype` | `HateArchetype.cs` | Spawn-time recipe: traits + default attribute values (literal or scaled). |
| `HateModifier`, `HateOp`, `HateReapply` | `HateEffect.cs` | One attribute modifier in an effect's static def. Use the named factories `HateModifier.Injected(...)` (SetByCaller) / `.Captured(...)` (attribute capture) — the public ctor is deliberately private to avoid an ambiguous overload with `GameplayTag`'s implicit numeric conversion. |
| `HateAbilityDef` | `HateAbility.cs` | Ability static def: cooldown, charges, targeting/source mode, cost, requirements, charge meter, category cooldown/charge pools, `Predicate`/`Action`. |
| `HateCondition` | `HateCondition.cs` | Simpler eligibility check (`Attr`/`Has`/`Lacks` factories), used by `HateAbilityDef.Requires`. |
| `IHatePredicate` family | `HatePredicate.cs` | Composable eligibility tree: `HateAll`/`HateAny`/`HateNone` combinators, `HateAttr`/`HatePresent`/`HateAbsent`/`HateChance`/`HateCustom` leaves. Extend via `HateCustom` + `HatePredicateRegistry.Register`. |
| `HateAction`, `IHateActionStep` family | `HateAction.cs` | What an ability does once its Predicate passes: an ordered list of guarded steps (`HateApplyEffects`, `HateRaiseEvent`, `HateFork` — weighted branch). |
| `HateCueType`, `HateCueEvent` | `HateCue.cs` | Cosmetic event stream (OnActive/WhileActive/OnRemove/Executed) — never authoritative, safe on predicting clients, skippable on dedicated servers. |
| `HateStoreScope`, `HateWorld.ReplicatedStores`/`CollectEntityScope` | `HateReplication.cs` | Networking **exposure** only — HATE opens no socket; this is the structural mapping a netcode provider needs. |
| `HateReserved` (static) | `HateReserved.cs` | Well-known reserved attribute tags HATE's own machinery depends on (`HATE.Effect.Tag/Duration/Stacks`, `HATE.Ability.Tag/Cooldown/Charges/ChargeMeter`). |
| `HateRng` (struct) | `HateRng.cs` | Deterministic seedable RNG (SplitMix64 seed + xoshiro256**, never `System.Random`) — `.Fork(streamId)` for reproducible per-entity substreams. |
| `HateTargetMode`, `HateSourceMode`, `HateTargetInput` | `HateTargeting.cs` | Selector inputs (who pays vs. who receives) — HATE performs no spatial query itself. |
| `HateWorldSubsystem` | `HateWorldSubsystem.cs` | Game Framework integration (`[Subsystem(SubsystemScope.World)]`) — owns/drives a `HateWorld`'s fixed-step tick, exposes a `Ticked` hook. |
| `HateWorld.Inspect(EntityId)` → `HateEntitySnapshot` | `HateInspector.cs` | Read-only per-entity debug surface (backs the Toolkit's debugger window). |

Note: as of this writing, `EntityStore.cs`/`TallStore.cs`/`TraitStore.cs` (internal storage-detail
files referenced in older surveys) have been removed from `Runtime/` and `HateAbility.cs` /
`HateReserved.cs` / `HateWorld.cs` are mid-refactor with uncommitted changes in this checkout —
re-verify exact internals of those three files against current source rather than trusting this
table's line-level detail for them; the public types above (`HateWorld`, `HateAbilityDef`,
`HateReserved`) still exist and match this shape.

## Dependencies

- **DataLens Foundation** (`com.heathen.datalensfoundation`, pinned `0.1.0` in `package.json`) —
  the state substrate. HATE owns no storage: it declares a `DataLensSchema` and rides a DataLens
  `Lens`; every attribute is a column, every entity a row.
- **GameplayTags Foundation** (`com.heathen.gameplaytags`, pinned `1.0.10` in `package.json`) —
  the identity/addressing substrate. Attributes, traits, effects, and abilities are all
  `GameplayTag`s. **One-way dependency**: HATE consumes GameplayTags for hierarchy/identity only
  (no storage, no condition/operation reuse — HATE has its own parallel `HateCondition`/
  `IHatePredicate`); nothing in GameplayTags Foundation references HATE.
- **Game Framework** (`Heathen.GameFramework`) — not UPM-resolvable, so correctly **absent** from
  `package.json`'s `dependencies` (that field only works for real UPM packages). Guarded instead
  via a hard reference in `Heathen.HATE.Foundation.asmdef`'s `references` array — the assembly
  simply won't compile until Game Framework is present in the project. `HateWorldSubsystem`
  directly subclasses Game Framework's `Subsystem`/`ISubsystemDebug`/`IOnFixed`. This is the same
  guard pattern Steamworks' Unity Foundation uses for its own non-UPM dependency — not a manifest
  gap.

No dependency on a "Lexicon Foundation" package despite that having been claimed in this repo's
own README/design specs at one point — confirmed absent from code and since removed from the docs
(2026-07-19). Don't re-add it without a new confirmed use.

## Common tasks

- **Declare a world and spawn an entity**: build a `HateSchema` (traits + attributes + an effects
  store via `.WithEffects(...)`), construct `new HateWorld(schema)`, then `world.Spawn(archetype)`.
  See the Quick start in `README.md` for a working hand-written example.
- **Apply a one-shot effect**: `world.DefineEffect(tag, modifiers...)` once, then
  `world.ApplyInstant(entityId, tag)`.
- **Apply a duration/stacking buff**: `world.AddEffect(...)`, then `world.TickEffects(dt)` each
  frame/step (or let `HateWorldSubsystem` drive it) and `world.RecomputeAttribute(s)` as needed.
- **Grant and activate an ability**: `world.DefineAbility(tag, HateAbilityDef)`,
  `world.GrantAbility(entityId, tag)`, then `world.Activate(...)` (4 overloads) or the queued
  `world.RequestActivation`/`DrainActivations` path for deferred/batched invocation.
- **Run a per-attribute passive across every entity in a trait**: `world.Lens.RunSystemColumn(...)`
  / `world.RunTraitSystem`/`RunTraitPipeline` — the columnar fast path, not a per-entity loop.
- **Build a composite eligibility check for an ability**: compose `IHatePredicate` leaves
  (`HateAttr`, `HatePresent`, `HateChance`, etc.) under `HateAll`/`HateAny`/`HateNone`; assign to
  `HateAbilityDef.Predicate`. Use `HateCondition.Attr/Has/Lacks` instead for the older, simpler
  `Requires` slot.
- **Expose entity state to a netcode provider**: `HateWorld.ReplicatedStores` /
  `CollectEntityScope(entityId)` — HATE does no networking itself, this is the structural mapping
  a provider (Mirror/NGO/FishNet/etc.) reads from.
- **Get deterministic per-entity randomness** (for replay-safe RNG): `HateRng.Fork(streamId)` off
  the world's seeded RNG, never `System.Random`.

## Where full usage docs live

No confirmed live KB article specifically for HATE as of this writing — check
`https://heathen.group/kb/` under HATE/Attribute gpTag Engine before citing a URL. The
authoritative design/status docs ship in the paid Toolkit's `SourceRepo` checkout under
`Unity/ToolkitSource/Assets/Toolkits/DesignSpecs/` (`HATE-Spec.md`, `HATE-Authoring-Spec.md`) —
not visible from this Foundation-only repo, and (per this repo's own convention) may describe
planned-but-unbuilt work, so verify against this repo's actual `Runtime/*.cs` before trusting a
spec claim.

## Version

`com.heathen.hatefoundation/package.json` (`version` field, currently `0.1.0`) +
`com.heathen.hatefoundation/CHANGELOG.md`.
