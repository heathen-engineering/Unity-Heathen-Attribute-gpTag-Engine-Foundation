# HATE Foundation — Heathen Attribute [gp]Tag Engine

A data-oriented attribute / effect / ability system (our answer to Unreal GAS, attribute-first and
deliberately not OOP), built on the **DataLens** substrate. Open-source Foundation; the paid Toolkit
(visual authoring, debugger, sample kits) layers on top.

Design: `Unity/ToolkitSource/Assets/Toolkits/DesignSpecs/HATE-Spec.md`.

## Status — HATE-P1 + early P2 (2026-06-17)
**Attribute State + Instant/Duration effects + Tags/Triggers + Ability activation** on DataLens. The
runtime is engine-agnostic C# over the DataLens Foundation (no UnityEngine dependency in the core).

- `HateWorld` — one DataLens store of actors (rows) × attributes. Each attribute is a **dedicated**
  pair of bit-packed columns: a **Base** (permanent) and a **Current** (Base after modifiers).
- Actors: `SpawnActor` / `DespawnActor` (fixed capacity, slot reuse), `IsAlive`, `LiveActorCount`.
- Attributes: `SetBase` / `GetBase` / `GetCurrent`, by attribute index (`AttributeIndex(name)`).
- **Instant effects** (§5.1): `ApplyInstant` (one actor) and `ApplyInstantAll` (every live actor in
  one parallel DataLens System pass), with `ModifierOp.Add/Multiply/Override`.
- **Duration effects** (§5.1/§5.2, the buff-density win): `ApplyDuration` stores an active effect as a
  single **row** (no heap) in an `ActiveEffects` store; `AdvanceTick`/`CurrentTick` drive the clock;
  `ExpireEffects` removes due effects via a **parallel vector-compare System** (`Active = 0 where
  EndTick <= CurrentTick`) + slot reclaim. Scales to 100k+ active effects with zero per-effect objects.
- `RecomputeCurrent` derives `Current = Base + Σ active duration modifiers` for all actors.
- **Tags** (§5.5/§6): per-actor 32-bit bitmask — `BaseTags` (intrinsic) + `CurrentTags` (intrinsic OR
  active effect grants). `ApplyDuration(…, grantTags)` grants tags for an effect's lifetime;
  `RecomputeTags` refreshes them. `TagMask`, `SetBaseTags`/`ClearBaseTags`, `HasTags`.
- **Triggers** (§6): `TagTrigger` (RequireAll / RequireAny / Exclude) is one DataLens bitmask predicate
  batch-evaluated across all actors in a branchless System pass. `EvaluateTrigger` (writes a Selected
  flag column), `CountMatching`, and `ApplyInstantWhere` — apply an Instant effect to every actor a
  Trigger matches in ONE branchless pass (the float effect is gated directly by the int tag column via
  DataLens's mixed-type predicate; no host loop).

Resolved design choices (HATE-Spec §14): **dedicated columns first** (tall open/modded store deferred);
**script-first C#** authoring (visual graph later in the Toolkit).

- **Abilities** (§7, HATE-P2): `AbilityDef` (cost = an Instant on a resource attribute; cooldown = a
  duration effect granting a cooldown tag that gates re-use; optional activation requirement = a
  `TagTrigger`; an activation cue; and a delayed payload task = a timed effect + cue). `CanActivate` /
  `TryActivate` run the cost + cooldown + requirement pipeline; cooldowns tick down via the step loop.
- **Cues** (§9): `CueEvent` cosmetic output stream — `EmitCue`, `PendingCues`, `ClearCues`. Never
  simulation state; drained by presentation each frame.
- **Ability Tasks** (§7 step 3): an ability's delayed payload is scheduled as a row in a `PendingTasks`
  store; `AdvanceAbilities` (each step) fires payloads whose FireTick has arrived (apply effect + emit
  cue + recycle the slot). No heap per in-flight task.

- **Mass AI** (§7/§8, HATE-P4 groundwork): `EvaluateEligibility` recasts `CanActivate` as branchless
  column passes filling a per-actor `Eligible` flag (cost / cooldown / requirement knock-outs);
  `CountEligible` / `IsEligible`. `EvaluateUtility` computes a per-actor `Utility` score from a linear
  weight over an attribute (negative slope = "rises as it falls", e.g. execute), zeroed for ineligible
  actors; `BestActorByUtility` scans the array for the pick. AI as a column op, not a tree walk.

## Next
- **Hierarchical tags** via the GameplayTags interval projection store (§6.1) — replaces the flat 32-bit
  mask; multi-condition Trigger trees.
- Mass-AI **demo scene** (utility over 100k+ actors); multi-ability argmax; richer weight curves.
- Richer **task types** (wait-event / wait-attribute-change) + per-actor ability **granting**
  (AbilityInstances) to finish HATE-P2.
- Multiply/Override duration **aggregation channels** (§5.3; additive only today).
- Multiply/Override duration **aggregation channels** (§5.3; additive only today).
- Move the modifier/grant accumulation from host-side to a DataLens scatter System.

## Dependencies
- `com.heathen.datalensfoundation` (the native DataLens core + managed binding).
