# Heathen Attribute [gameplay]Tag Engine (HATE) Foundation

![License](https://img.shields.io/badge/License-Apache_2.0-blue?style=flat-square)
![Maintained](https://img.shields.io/badge/Maintained%3F-yes-green?style=flat-square)
![Unity](https://img.shields.io/badge/Unity-2021.3%20%2B-black?style=flat-square&logo=unity&logoColor=white)
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

## What it does

HATE gives you full Unreal-GAS *feature* parity without its object-oriented architecture. Everything is a row, a column, or a tag-addressed bitmask, mutated by data-described Systems. The runtime is one type:

| Type | Purpose |
|------|---------|
| **`HateWorld`** | The simulation: actors (rows) × attributes (typed columns), the active-effects store, status/ability/immunity bitmasks, and a `Lens` that drives it all. |
| **`HateAttribute`** | A typed, ranged attribute descriptor `(GameplayTag, type, min, max)` → narrowest DataLens column. |
| **`AbilityDef`** | A tag-addressed ability: cost, cooldown, activation requirement, cue, and a delayed payload effect. |
| **`Consideration`** | A response-curve over a metric attribute — the unit of utility-AI scoring. |

The following features are included:

- **Typed, ranged attributes** — declare each as `(tag, Integral / SinglePrecision / DoublePrecision, min, max)`. HATE picks the narrowest storage width, offset-encodes integrals, and range-clamps every write. Each attribute is four working fields: `Base`, `Current`, dynamic `Min`, dynamic `Max`.
- **Instant effects** — `ApplyInstant` (one actor) / `ApplyInstantAll` (every live actor in one parallel pass), with `Add` / `Multiply` / `Override`.
- **Duration effects (buff density)** — `ApplyDuration` stores an active effect as a single row (no heap); `ExpireEffects` clears due effects in one parallel vector-compare System. Scales to 100k+ active effects.
- **Cap buffs** — `ApplyDuration(..., HateField.Max, ...)` targets an attribute *field* (e.g. "+10% Max Health for 30 ticks"); caps fold from a persistent base each recompute and auto-expire.
- **Stacking** — `ApplyStackingDuration` collapses re-applications to one row carrying a `StackCount`, with `RefreshDuration` / `KeepLongest` / `KeepExisting` policies.
- **Aggregation channels** — `RecomputeCurrent` derives `Current = clamp(Override ?? (Base + ΣAdd)·ΠMul, Min, Max)`.
- **Status & conditions** — per-actor status as a 32-bit hot-tag bitmask; `GameplayTagCondition`s are **compiled** to DataLens batch predicates (`Exists` / `NotExists` → bitmask tests), never evaluated per-actor in hot paths. `CountMatching`, `ApplyInstantWhere`.
- **Abilities** — `RegisterAbility` / `GrantAbility` / `CanActivate` / `TryActivate`. Cost = an Instant on a resource attribute, cooldown = a duration effect granting a cooldown tag, requirement = compiled conditions, plus an activation cue and a delayed payload task fired by `AdvanceAbilities`.
- **Immunity** — per-actor immunity mask gates incoming effects by their asset tags (`TryApplyInstant` / `TryApplyDuration`, with a blocked cue).
- **Mass utility AI** — `ScoreAbility` (response-curve `Consideration`s over metric attributes) → `PerturbScores` (per-actor variance × reproducible noise) → `Select` (noisy argmax) → a `Choice` column. Deciding between abilities for 100k actors is sub-millisecond.
- **Cues** — `EmitCue` / `PendingCues` cosmetic output stream; never simulation state, drained by presentation each frame.
- **Engine-agnostic by design** — the runtime is plain C# over the two Foundations; the same model is realised once per engine.

---

## Requirements

- Unity **2021.3** or compatible
- [**DataLens Foundation**](https://github.com/heathen-engineering/Unity-DataLens-Foundation) (`com.heathen.datalensfoundation`)
- [**GameplayTags Foundation**](https://github.com/heathen-engineering/Unity-GameplayTags-Foundation) (`com.heathen.gameplaytags`)

---

## Installation

### Via Unity Package Manager (UPM)

Install the two dependency Foundations first (Add package from git URL), then HATE:

1. In Unity, go to `Window > Package Manager`.
2. Click **+** > **Add package from git URL**.
3. Enter:
   ```
   https://github.com/heathen-engineering/Unity-Heathen-Attribute-gpTag-Engine-Foundation.git?path=/com.heathen.hatefoundation
   ```

-----

## Setup & Workflow

### 1. Define Tags & Build a World

Register your attribute / status / ability tags (see the GameplayTags Foundation), then declare a world:

```csharp
using Heathen.HATE;
using Heathen.GameplayTags;

GameplayTag Health   = GameplayTag.FromName("Combat.Attributes.Health");
GameplayTag Mana     = GameplayTag.FromName("Combat.Attributes.Mana");
GameplayTag Stunned  = GameplayTag.FromName("Combat.Status.Stunned");
GameplayTag Fireball = GameplayTag.FromName("Combat.Abilities.Fireball");
GameplayTag CdFire   = GameplayTag.FromName("Combat.Status.Cooldown.Fireball");

using var world = new HateWorld(
    new[]
    {
        new HateAttribute(Health, HateValueType.SinglePrecision, 0, 1000),
        new HateAttribute(Mana,   HateValueType.SinglePrecision, 0, 500),
    },
    new[] { Stunned, CdFire },   // hot status tags (max 32)
    capacity: 10_000);
```

### 2. Spawn Actors & Apply Effects

```csharp
ulong actor = world.SpawnActor();      // a row handle (InvalidActor when full)
world.SetBase(actor, Health, 800);

world.ApplyInstant(actor, Health, ModifierOp.Add, -50);              // permanent: Base 800 → 750
world.ApplyDuration(actor, Health, ModifierOp.Add, 100, durationTicks: 5);   // transient Current buff
world.ApplyDuration(actor, Health, HateField.Max, ModifierOp.Multiply, 1.1, durationTicks: 5); // +10% Max cap

world.RecomputeCurrent(); // fold caps → derive Current = clamp(Base + modifiers, Min, Max)
double hp = world.GetCurrent(actor, Health);
```

### 3. The Step Loop

```csharp
void FixedStep()
{
    world.AdvanceTick();        // advance the sim clock
    world.AdvanceAbilities();   // fire any delayed ability payloads
    world.ExpireEffects();      // clear due effects (one parallel System) + recycle slots
    world.RecomputeTags();      // CurrentTags = BaseTags OR active grants
    world.RecomputeCurrent();   // re-derive every attribute's caps + Current
    // drain world.PendingCues into your presentation layer, then world.ClearCues();
}
```

### 4. Abilities

```csharp
world.RegisterAbility(new AbilityDef(
    id: Fireball, costAttr: Mana, costAmount: 20,
    cooldownTicks: 2, cooldownTag: CdFire,
    requirement: new[] { new GameplayTagCondition { Tag = Stunned, Comparison = GameplayTagComparisonOp.NotExists } }));

world.GrantAbility(actor, Fireball);
world.RecomputeTags();

if (world.TryActivate(actor, Fireball))   // spends Mana, starts cooldown, blocks re-fire this step
    Debug.Log("Fireball cast");
```

### 5. Mass Utility AI

```csharp
using var ai = new HateWorld(attributes, statusTags, abilityScoreSlots: 2, capacity: 100_000);

// Score each ability via response-curve considerations over metric attributes
ai.ScoreAbility(slot: 0, healAbility,
    new[] { new Consideration(Health, Curve.Linear(0, 1000, slope: -1f, intercept: 1f)) },
    Aggregate.Product);

ai.PerturbScores(seed: 1234, tick: 0, noiseLo: 0f, noiseHi: 1f); // variance × reproducible noise
ai.Select();                                                     // noisy argmax → per-actor Choice column
int choice = ai.GetChoice(actor);
```

-----

## API Reference

### `HateWorld` — actors & attributes

| Member | Description |
|--------|-------------|
| `new HateWorld(attributes, [statusTags], [abilityScoreSlots], capacity)` | Build a fixed-capacity world |
| `SpawnActor()` / `DespawnActor(actor)` / `IsAlive` | Actor lifecycle (`InvalidActor` when full) |
| `SetBase` / `GetBase` / `GetCurrent(actor, tag)` | Permanent value vs derived working value |
| `GetMin` / `GetMax` / `SetMin` / `SetMax(actor, tag, v)` | Per-actor dynamic caps |
| `AttributeIndex` / `HasAttribute` / `AttributeDef(tag)` | Attribute lookups |

### `HateWorld` — effects

| Member | Description |
|--------|-------------|
| `ApplyInstant(actor, tag, op, mag)` / `ApplyInstantAll(tag, op, mag)` | Permanent change (one / all live actors) |
| `ApplyDuration(actor, tag, [op], mag, ticks, [grantStatus])` | Transient Current-channel effect |
| `ApplyDuration(actor, tag, HateField field, op, mag, ticks)` | Cap-buff / field-targeted effect |
| `ApplyStackingDuration(...)` / `GetStackCount` | Collapsing stacks with refresh policy |
| `AdvanceTick` / `ExpireEffects` / `RecomputeCurrent` | The per-step effect pipeline |
| `GetActiveEffects(list)` / `EffectSnapshot` | Inspect active effects (for tooling) |

### `HateWorld` — status, abilities, immunity

| Member | Description |
|--------|-------------|
| `SetBaseStatus` / `ClearBaseStatus` / `HasStatus` / `RecomputeTags` | Per-actor status tags |
| `CountMatching(conditions)` / `ApplyInstantWhere(...)` | Compiled-condition batch queries |
| `RegisterAbility` / `GrantAbility` / `RevokeAbility` / `HasAbility` | Ability catalogue & grants |
| `CanActivate` / `TryActivate(actor, ability)` / `AdvanceAbilities` | Activation pipeline + delayed payloads |
| `SetBaseImmunity` / `IsImmuneTo` / `TryApplyInstant` / `TryApplyDuration` | Immunity gating |

### `HateWorld` — mass utility AI

| Member | Description |
|--------|-------------|
| `ScoreAbility(slot, ability, considerations, aggregate)` | Response-curve scoring into a score column |
| `PerturbScores(...)` / `Select()` | Variance-noise perturbation → noisy-argmax `Choice` |
| `EvaluateEligibility` / `CountEligible` / `EvaluateUtility` / `BestActorByUtility` | Batch eligibility + linear-weight utility |
| `GetChoice` / `GetUtility` / `SetVariance` / `SetCommand(actor, ...)` | Per-actor AI accessors |

### Supporting types

| Type | Members |
|------|---------|
| `HateValueType` | `Integral`, `SinglePrecision`, `DoublePrecision` |
| `HateField` | `Current`, `Min`, `Max` |
| `ModifierOp` | `Add`, `Multiply`, `Override` |
| `StackRefresh` | `RefreshDuration`, `KeepLongest`, `KeepExisting` |
| `Aggregate` | `Product`, `WeightedSum` |
| `AbilityDef` | `Id`, `CostAttr`, `CostAmount`, `CooldownTicks`, `CooldownTag`, `Requirement`, `ActivateCue`, `EffectAttr`/`EffectOp`/`EffectMag`/`EffectDelayTicks`/`EffectCue` |
| `Consideration` | `MetricAttr`, `Curve`, `Weight` |
| `CueEvent` | `Cue`, `Actor`, `Magnitude` |

-----

## Namespaces

| Namespace | Contents |
|-----------|----------|
| `Heathen.HATE` | All runtime types: `HateWorld`, `HateAttribute`, `AbilityDef`, `Consideration`, `CueEvent`, and the enums |

Design notes live in the SourceRepo at `Assets/Toolkits/DesignSpecs/HATE-Spec.md`.
