# Heathen Attribute [gameplay]Tag Engine (HATE) Foundation

![License](https://img.shields.io/badge/License-Apache_2.0-blue?style=flat-square)
![Maintained](https://img.shields.io/badge/Maintained%3F-yes-green?style=flat-square)
![Unity](https://img.shields.io/badge/Unity-2021.3%20%2B-%23313131?style=flat-square&logo=unity&logoColor=white)
[![Dependency](https://img.shields.io/badge/Built_on-DataLens%20%2B%20GameplayTags-lightgrey?style=flat-square)](https://github.com/heathen-engineering/Unity-DataLens-Foundation)

A data-oriented attribute / effect / ability system for Unity — our answer to Unreal's GAS, attribute-first and deliberately **not** OOP. State lives in [DataLens](https://github.com/heathen-engineering/Unity-DataLens-Foundation) columns and is addressed by [GameplayTags](https://github.com/heathen-engineering/Unity-GameplayTags-Foundation), so 100k+ actors with active buffs simulate as branchless column passes with zero per-effect GC objects.

-----

## ⚙ Two Foundations, one engine

HATE composes two substrates: **DataLens = state** (the columnar store) and **GameplayTags = identity** (hierarchical `u64` addressing). Attributes, status, abilities, and cues are all GameplayTags, so the developer surface reads `Combat.Attributes.Health`, `Combat.Status.Stunned`, `Combat.Abilities.Fireball`. The open-source Foundation is the runtime; the paid Toolkit (visual authoring, debugger, sample kits) layers on top.

-----

## Become a GitHub Sponsor
[![Discord](https://img.shields.io/badge/Discord--1877F2?style=social&logo=discord)](https://discord.gg/6X3xrRc)
[![GitHub followers](https://img.shields.io/github/followers/heathen-engineering?style=social)](https://github.com/heathen-engineering?tab=followers)  
Support Heathen by becoming a [GitHub Sponsor](https://github.com/sponsors/heathen-engineering). Sponsorship directly funds the development and maintenance of free tools like this, as well as our game development [Knowledge Base](https://heathen.group/) and community on [Discord](https://discord.gg/6X3xrRc).

Sponsors also get access to our private SourceRepo, which includes developer tools for O3DE, Unreal, Unity, and Godot.  
Learn more or explore other ways to support @ [heathen.group/kb](https://heathen.group/kb/do-more/)

-----

## Status: feature-complete runtime + authoring, on the Game Framework

> The runtime is **built, verified, and feature-complete for GAS parity** as a pure DataLens consumer — HATE
> owns no storage; it declares a DataLens schema and rides the Lens. Verified end-to-end against the native
> library and in the Unity Test Runner, and homed in a Game Framework **World** via `HateWorldSubsystem`
> (fixed-step auto-tick + a composition hook).

**Built and verified:**
- **Entities, traits, effects, abilities** — attributes (typed + ranged), effects with GAS aggregation
  (`Current = override ?? (Base + ΣAdd)·ΠMul`), stacking, reapply policy, immunity, granted tags/abilities,
  magnitude/execution calcs; abilities with charges, a targeting input schema, source & target **selectors**,
  and Condition-composed cost/cooldown. **Gameplay cues** (cosmetic event stream). **Effect type-stores** (§7.4):
  tag-subtree type-ops on the monolithic store **and** the optional per-type schema-split (a codegen knob).
- **HATE Forge** authoring window (forms-over-`.hate`-JSON, live validation, undo/redo, a shared **Build**
  status button) + the **codegen** pipeline (typed GameplayTag handles, `Schema.BuildSchema()`, effect
  type-split, and **engine-side accessors**: typed `DataView` records + an OOP-membrane `I<Trait>` /
  `<Trait>Accessor` per trait). Staleness is emitter-version-aware.
- **Per-entity runtime debugger** (`HateWorld.Inspect` + the HATE Debugger window).
- **Networking exposure** — HATE opens no socket and implements no authority/prediction; it *exposes* what a
  netcode stack needs (`ReplicatedStores` + `CollectEntityScope`) on top of DataLens' Snapshot/CollectDelta/
  ApplyPayload. Replication is the provider's job (Mirror/NGO/FishNet/Unreal), via these hooks.
- **100k+ entities** under active effects simulate as branchless column passes with zero per-effect GC.

**Remaining:** the `<Archetype>.Spawn(...)` factory + optional ready-made `<Trait>Behaviour` component and the
ECS (DOTS/O3DE) accessor branch; a "playable" onboarding sample; reference netcode provider adapters (external
samples); a few perf wins (O(1) single-entity access, insert-without-hydrate). Utility AI is **Wyrd's** concern
(a later product), not HATE's: HATE is deterministic, Wyrd chooses.

The authoritative design + implementation status live in the SourceRepo at `Assets/Toolkits/DesignSpecs/`:
- **`HATE-Spec.md`** — the runtime/product spec (the model summarised here).
- **`HATE-Authoring-Spec.md`** — the document model + HATE Forge (authoring + codegen).

## The model

HATE is one runtime primitive (a typed DataStore + columns + an optional **System**) expressed as several
authoring concepts, all GameplayTag-addressed and project-wide:

- **Attributes** — `(tag, datatype)`; caps (`MaxHealth`) are ordinary attributes, not a built-in range.
- **Traits** — an ECS component table: a per-trait DataLens store of attributes plus its **Trait Systems** (the
  passives, run as columnar kernels via `Lens.RunSystem`). One store per trait; an **EntityCatalog** relates an
  entity to its row in each trait via dereference index columns. `EntityID` is simply the catalog row index
  (stale-handle safety is an opt-in game trait, not baked in) — an entity need not be an actor; a crate, a
  weather front, or a terrain region are all entities.
- **Effects / Abilities** — tall stores (one row per instance per entity, keyed by an `EntityIndex` column): a
  tag + a static definition + instance state. Effects tick via **Trait Systems** and aggregate
  (`Current = override ?? (Base + ΣAdd)·ΠMul`, stacking-aware, auto-reverting); abilities resolve via **Entity
  Systems** (cross-trait, hydrated View + write-back). Cost = an Effect, cooldown = a Condition.
- **Conditions** — one unified predicate (tag presence, attribute comparison, trait membership) compiled to
  DataLens batch predicates, with interval-encoded hierarchical tag matching.
- **Resolution** — effects carry a tag→value payload + context; an Entity System resolves them through a
  Targetable buffer (mitigation, redirect, contextual scaling), with seeded, reproducible RNG.

## Requirements

- Unity **2021.3** or compatible
- [**DataLens Foundation**](https://github.com/heathen-engineering/Unity-DataLens-Foundation) (`com.heathen.datalensfoundation`)
- [**GameplayTags Foundation**](https://github.com/heathen-engineering/Unity-GameplayTags-Foundation) (`com.heathen.gameplaytags`)

## Namespaces

| Namespace | Contents |
|-----------|----------|
| `Heathen.HATE` | The runtime: `HateWorld`, `HateSchema`/`HateTrait`/`HateAttribute`, `HateArchetype`, `EntityId`, `HateModifier`/`HateAbilityDef`. |
| `Heathen.HATE.Toolkit` | Engine-bridge MonoBehaviours (`HATE_Entity`, `HateEntityBridge`); paid authoring/debugger layers. |

## Quick start

```csharp
// 1. Declare the world: traits (attribute columns) + an effects store. HATE builds the DataLens schema.
var schema = new HateSchema(capacity: 1000,
        new HateTrait("HATE.Trait.Combat", 1000,
            new HateAttribute("HATE.Attribute.Health", DataLensValueType.Int32, 100),
            new HateAttribute("HATE.Attribute.MaxHealth", DataLensValueType.Int32, 100)))
    .WithEffects("HATE.Store.Effects", 4000);

using var world = new HateWorld(schema);

// 2. Spawn from an archetype recipe (returns the entity = its catalog row index).
var goblin = world.Spawn(new HateArchetype()
    .With("HATE.Trait.Combat").Set("HATE.Attribute.Health", 80).Set("HATE.Attribute.MaxHealth", 120));

// 3. A Trait System (columnar fast path): clamp Health to MaxHealth across every Combat entity.
world.Lens.RunSystemColumn("HATE.Trait.Combat", "HATE.Attribute.Health", SystemOp.Min, "HATE.Attribute.MaxHealth");

// 4. Effects: define once, apply instantly (or AddEffect for a duration buff + RecomputeAttribute).
world.DefineEffect("HATE.Effect.Smite", new HateModifier("HATE.Attribute.Health", HateOp.Add, -30));
world.ApplyInstant(goblin, "HATE.Effect.Smite");      // Health 80 -> 50
```

In practice you author the schema, effects, abilities and archetypes in `.hate` files and the Toolkit generates
this code (typed GameplayTag handles + `Schema.BuildSchema()` + the per-trait `Views` / `<Trait>Accessor` engine
bridge); the above is the hand-written shape of what it emits.
