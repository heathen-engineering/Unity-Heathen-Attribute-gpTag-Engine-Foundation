using System;
using System.Collections.Generic;
using Heathen.DataLens;

namespace Heathen.HATE
{
    /// <summary>
    /// HATE Attribute State (HATE-Spec §4) on the DataLens substrate — the bedrock primitive.
    ///
    /// HATE-P1: a fixed set of float attributes stored as DEDICATED bit-packed columns, two per
    /// attribute — a <b>Base</b> (the permanent value) and a <b>Current</b> (Base after modifiers).
    /// Actors are store rows. Instant effects modify Base immediately (§5.1); duration effects (§5.1/§5.2)
    /// add to Current for a number of ticks and auto-expire; <see cref="RecomputeCurrent"/> derives
    /// Current = Base + active duration modifiers. Bulk operations run as DataLens column Systems through
    /// the Lens, so an effect over every actor — or expiring every due effect — is one parallel pass, not
    /// a per-object loop.
    ///
    /// The duration model is the §5.2 "buff density" win: an active effect is a single store ROW (no heap
    /// object); expiry is a parallel vector-compare System over the EndTick column.
    ///
    /// Still ahead in P1: Multiply/Override duration aggregation channels (§5.3, currently additive only),
    /// granted tags + Trigger gating (§5.5/§6), and a System-side modifier scatter (aggregation is
    /// host-side for now). The tall open/modded attribute store is deferred (open question #1 resolved:
    /// dedicated columns first).
    /// </summary>
    public sealed class HateWorld : IDisposable
    {
        private readonly DataStore _store;
        private readonly Lens _lens;
        private readonly DataStore[] _table;        // store table for IR execution: [0]=actors, [1]=effects
        private readonly Dictionary<string, int> _attrIndex;
        private readonly string[] _attrNames;
        private readonly IrProgram _recomputeCurrent; // Current = Base for every attribute

        // ── Active duration effects (HATE-Spec §5.2 — buff density: a row per active effect, no heap). ──
        // Columns: Actor, Attr (which attribute), Magnitude (additive), EndTick, Active (1=live).
        private readonly DataStore _effects;
        private readonly int _effectsCapacity;
        private const int FxActor = 0, FxAttr = 1, FxMagnitude = 2, FxEndTick = 3, FxActive = 4, FxGrantTags = 5;
        private const int EffectsStoreIndex = 1;

        private DataView _activeView;     // over the effects Active column, for reclaim read-back
        private int[] _activeScratch;     // managed buffer for the bulk read

        private int _currentTick;

        // ── Tags (§5.5 granted tags / §6 Trigger gating). Up to 32 "hot" tags packed as bits in an
        //    Int32 mask column, so a Trigger is a branchless DataLens bitmask predicate over all actors. ──
        private readonly Dictionary<string, int> _tagIndex;       // tag name -> bit index (0..31)
        private readonly int _baseTagsCol, _currentTagsCol, _selectedCol; // Int32 columns on the actor store
        private readonly IrProgram _recomputeTags;                // CurrentTags = BaseTags
        private DataView _selectedView;                           // over the actor Selected column
        private int[] _selectedScratch;

        // ── Mass AI (§7 batch CanActivate / §8 utility): per-actor Eligible (int 1/0) + Utility (float)
        //    columns, computed across ALL actors as branchless column passes, then scanned. ──
        private readonly int _eligibleCol;   // Int32
        private readonly int _utilityCol;    // Float
        private DataView _eligibleView;
        private int[] _eligibleScratch;
        private DataView _utilityView;
        private float[] _utilityScratch;

        // ── Abilities (§7): in-flight payload TASKS (a delayed effect+cue per row, fired by tick) + the
        //    cosmetic CUE output stream (§9, drained by presentation — never simulation state). ──
        private readonly DataStore _tasks;
        private readonly int _tasksCapacity;
        private const int TkActor = 0, TkFireTick = 1, TkEffectAttr = 2, TkEffectOp = 3, TkEffectMag = 4, TkCue = 5;
        private readonly System.Collections.Generic.List<CueEvent> _cues = new System.Collections.Generic.List<CueEvent>();

        /// <summary>Sentinel returned by <see cref="SpawnActor"/> when the world is at capacity.</summary>
        public const int InvalidActor = -1;

        /// <summary>Create a world with a fixed attribute set, no tags, and a maximum actor capacity.</summary>
        public HateWorld(string[] attributeNames, int capacity)
            : this(attributeNames, System.Array.Empty<string>(), capacity) { }

        /// <summary>
        /// Create a world with a fixed attribute set, a fixed tag set (up to 32 "hot" tags), and a
        /// maximum actor capacity. Each attribute gets a Base and a Current float column; tags pack into
        /// per-actor Int32 bitmask columns (BaseTags/CurrentTags) plus a Selected scratch column for
        /// Trigger evaluation.
        /// </summary>
        public HateWorld(string[] attributeNames, string[] tagNames, int capacity)
        {
            if (attributeNames == null) throw new ArgumentNullException(nameof(attributeNames));
            if (attributeNames.Length == 0) throw new ArgumentException("Need at least one attribute.", nameof(attributeNames));
            if (tagNames == null) throw new ArgumentNullException(nameof(tagNames));
            if (tagNames.Length > 32) throw new ArgumentException("At most 32 hot tags (Int32 bitmask).", nameof(tagNames));
            if (capacity <= 0) throw new ArgumentException("Capacity must be positive.", nameof(capacity));

            _attrNames = (string[])attributeNames.Clone();
            _attrIndex = new Dictionary<string, int>(attributeNames.Length);
            _tagIndex = new Dictionary<string, int>(tagNames.Length);
            for (int t = 0; t < tagNames.Length; t++)
                _tagIndex[tagNames[t]] = t;

            // Actor columns: [a0.Base, a0.Current, …] (Float) then BaseTags, CurrentTags, Selected,
            // Eligible (Int32) and Utility (Float).
            int attrCols = attributeNames.Length * 2;
            _baseTagsCol = attrCols;
            _currentTagsCol = attrCols + 1;
            _selectedCol = attrCols + 2;
            _eligibleCol = attrCols + 3;
            _utilityCol = attrCols + 4;

            int colCount = attrCols + 5;
            var colNames = new string[colCount];
            var colTypes = new DataLensValueType[colCount];
            for (int a = 0; a < attributeNames.Length; a++)
            {
                _attrIndex[attributeNames[a]] = a;
                colNames[BaseCol(a)] = attributeNames[a] + ".Base";
                colNames[CurrentCol(a)] = attributeNames[a] + ".Current";
                colTypes[BaseCol(a)] = DataLensValueType.Float;
                colTypes[CurrentCol(a)] = DataLensValueType.Float;
            }
            colNames[_baseTagsCol] = "BaseTags";       colTypes[_baseTagsCol] = DataLensValueType.Int32;
            colNames[_currentTagsCol] = "CurrentTags"; colTypes[_currentTagsCol] = DataLensValueType.Int32;
            colNames[_selectedCol] = "Selected";       colTypes[_selectedCol] = DataLensValueType.Int32;
            colNames[_eligibleCol] = "Eligible";       colTypes[_eligibleCol] = DataLensValueType.Int32;
            colNames[_utilityCol] = "Utility";         colTypes[_utilityCol] = DataLensValueType.Float;

            _store = new DataStore(colNames, colTypes, (ulong)capacity);
            _lens = new Lens(0); // hardware concurrency

            // Active-effects store: a row per live duration effect (no heap object — the §5.2 win).
            _effectsCapacity = capacity * 4; // a few effects per actor; fixed, recycled on expiry
            _effects = new DataStore(
                new[] { "Actor", "Attr", "Magnitude", "EndTick", "Active", "GrantTags" },
                new[] { DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Float,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32 },
                (ulong)_effectsCapacity);

            _table = new[] { _store, _effects };
            _activeView = new DataView(new ulong[] { FxActive });
            _activeScratch = new int[_effectsCapacity];
            _selectedView = new DataView(new ulong[] { (ulong)_selectedCol });
            _selectedScratch = new int[capacity];
            _eligibleView = new DataView(new ulong[] { (ulong)_eligibleCol });
            _eligibleScratch = new int[capacity];
            _utilityView = new DataView(new ulong[] { (ulong)_utilityCol });
            _utilityScratch = new float[capacity];

            // In-flight ability payload tasks: a row per pending (delayed) effect+cue.
            _tasksCapacity = capacity * 2;
            _tasks = new DataStore(
                new[] { "Actor", "FireTick", "EffectAttr", "EffectOp", "EffectMag", "Cue" },
                new[] { DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Float, DataLensValueType.Int32 },
                (ulong)_tasksCapacity);

            // Reusable: Current = Base for every attribute (independent columns → one parallel level).
            _recomputeCurrent = new IrProgram();
            for (int a = 0; a < attributeNames.Length; a++)
                _recomputeCurrent.Add(IrOp.FloatColumn(0, CurrentCol(a), SystemOp.Set, BaseCol(a)));

            // Reusable: CurrentTags = BaseTags (granted tags are OR'd on top, host-side, per recompute).
            _recomputeTags = new IrProgram();
            _recomputeTags.Add(IrOp.IntColumn(0, _currentTagsCol, SystemOp.Set, _baseTagsCol));
        }

        public int AttributeCount => _attrNames.Length;
        public int LiveActorCount => (int)_store.LiveCount;

        /// <summary>Resolve an attribute name to its index (throws if unknown).</summary>
        public int AttributeIndex(string name) => _attrIndex[name];

        private int BaseCol(int attr) => attr * 2;
        private int CurrentCol(int attr) => attr * 2 + 1;

        // ── Actors ───────────────────────────────────────────────────────────

        /// <summary>Spawn an actor (allocate a row). Returns its handle, or <see cref="InvalidActor"/> when full.</summary>
        public int SpawnActor()
        {
            ulong row = _store.AllocRow();
            return row == DataStore.InvalidRow ? InvalidActor : (int)row;
        }

        /// <summary>Despawn an actor so its slot can be reused.</summary>
        public void DespawnActor(int actor) => _store.FreeRow((ulong)actor);

        public bool IsAlive(int actor) => _store.IsValid((ulong)actor);

        // ── Attribute access ─────────────────────────────────────────────────

        public void SetBase(int actor, int attr, float value)
            => _store.SetFloat((ulong)actor, (ulong)BaseCol(attr), value);

        public float GetBase(int actor, int attr)
        {
            _store.TryGetFloat((ulong)actor, (ulong)BaseCol(attr), out float v);
            return v;
        }

        /// <summary>The Current value (Base after modifiers) as of the last <see cref="RecomputeCurrent"/>.</summary>
        public float GetCurrent(int actor, int attr)
        {
            _store.TryGetFloat((ulong)actor, (ulong)CurrentCol(attr), out float v);
            return v;
        }

        // ── Effects (Instant, §5.1) ──────────────────────────────────────────

        /// <summary>Apply an Instant effect to one actor's attribute Base (permanent change).</summary>
        public void ApplyInstant(int actor, int attr, ModifierOp op, float magnitude)
        {
            float b = GetBase(actor, attr);
            SetBase(actor, attr, Combine(b, op, magnitude));
        }

        /// <summary>
        /// Apply an Instant effect to the given attribute Base of EVERY live actor in one parallel
        /// DataLens System pass. Returns the number of actors affected.
        /// </summary>
        public ulong ApplyInstantAll(int attr, ModifierOp op, float magnitude)
        {
            using var program = new IrProgram();
            program.Add(IrOp.Float(0, BaseCol(attr), ToSystemOp(op), magnitude));
            return _lens.Execute(program, _table);
        }

        // ── Sim clock + Duration effects (§5.1 Duration, §5.2 buff density) ───

        /// <summary>The HATE sim tick. Duration effects expire when CurrentTick reaches their EndTick.</summary>
        public int CurrentTick => _currentTick;

        /// <summary>Advance the sim clock (call once per fixed step before <see cref="ExpireEffects"/>).</summary>
        public void AdvanceTick(int ticks = 1) => _currentTick += ticks;

        /// <summary>Number of currently-active duration effects (live rows in the effects store).</summary>
        public int ActiveEffectCount => (int)_effects.LiveCount;

        /// <summary>Sentinel returned by <see cref="ApplyDuration"/> when the effects store is at capacity.</summary>
        public const int InvalidEffect = -1;

        /// <summary>
        /// Apply a duration effect: an additive <paramref name="magnitude"/> to <paramref name="attr"/>'s
        /// Current for <paramref name="durationTicks"/> ticks, auto-removed at EndTick. Stored as one row
        /// (no heap). Returns the effect handle, or <see cref="InvalidEffect"/> when the store is full.
        /// </summary>
        public int ApplyDuration(int actor, int attr, float magnitude, int durationTicks)
            => ApplyDuration(actor, attr, magnitude, durationTicks, 0);

        /// <summary>
        /// As <see cref="ApplyDuration(int,int,float,int)"/> but the effect also GRANTS the given tag
        /// mask (§5.5) for its lifetime — set in <see cref="RecomputeTags"/>'s CurrentTags, cleared when
        /// the effect expires. The status-effect backbone (e.g. a stun debuff grants `State.Stunned`).
        /// </summary>
        public int ApplyDuration(int actor, int attr, float magnitude, int durationTicks, int grantTags)
        {
            ulong r = _effects.AllocRow();
            if (r == DataStore.InvalidRow) return InvalidEffect;
            _effects.SetInt(r, (ulong)FxActor, actor);
            _effects.SetInt(r, (ulong)FxAttr, attr);
            _effects.SetFloat(r, (ulong)FxMagnitude, magnitude);
            _effects.SetInt(r, (ulong)FxEndTick, _currentTick + durationTicks);
            _effects.SetInt(r, (ulong)FxActive, 1);
            _effects.SetInt(r, (ulong)FxGrantTags, grantTags);
            return (int)r;
        }

        /// <summary>
        /// Expire duration effects whose EndTick has passed. The detection is a single PARALLEL
        /// vector-compare System over the whole EndTick column (<c>Active = 0 where EndTick &lt;= CurrentTick</c>)
        /// — the structural win: 100k+ active effects, zero heap, no per-effect object walk. Expired rows
        /// are then reclaimed (freed) so their slots recycle. Returns the number expired.
        /// </summary>
        public ulong ExpireEffects()
        {
            // Phase 1 — the System: mark expired rows inactive in one parallel column pass.
            using (var program = new IrProgram())
            {
                program.Add(IrOp.Int(EffectsStoreIndex, FxActive, SystemOp.Set, 0)
                    .WithPredicate(FxEndTick, CompareOp.LessEqual, _currentTick));
                ulong expired = _lens.Execute(program, _table);
                if (expired == 0) return 0;
            }

            // Phase 2 — reclaim: bulk-read the Active column (one marshalled copy) and free inactive rows
            // so the slots recycle. SourceRow maps each view row back to its effects-store row.
            _lens.RefreshView(_activeView, _effects);
            int n = _activeView.CopyInts(_activeScratch);
            ulong freed = 0;
            for (int i = 0; i < n; i++)
            {
                if (_activeScratch[i] == 0)
                {
                    _effects.FreeRow(_activeView.SourceRow((ulong)i));
                    freed++;
                }
            }
            return freed;
        }

        /// <summary>
        /// Derive Current from Base for every attribute, then add every active duration effect's
        /// magnitude onto its target attribute's Current (§5.3, additive modifiers). The Current=Base
        /// pass is one parallel System; the modifier accumulation is host-side for now (a scatter the
        /// substrate doesn't yet express as a System — future work).
        /// </summary>
        public void RecomputeCurrent()
        {
            _lens.Execute(_recomputeCurrent, _table);
            AddActiveModifiers();
        }

        private void AddActiveModifiers()
        {
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                _effects.TryGetInt((ulong)r, (ulong)FxAttr, out int attr);
                _effects.TryGetFloat((ulong)r, (ulong)FxMagnitude, out float mag);

                float cur = GetCurrent(actor, attr);
                _store.SetFloat((ulong)actor, (ulong)CurrentCol(attr), cur + mag);
            }
        }

        // ── Tags: granted (§5.5) + intrinsic ────────────────────────────────

        /// <summary>Number of registered hot tags.</summary>
        public int TagCount => _tagIndex.Count;

        /// <summary>Resolve a tag name to its bit index (0..31). Throws if unknown.</summary>
        public int TagIndex(string name) => _tagIndex[name];

        /// <summary>The single-bit mask for a named tag (<c>1 &lt;&lt; TagIndex(name)</c>).</summary>
        public int TagMask(string name) => 1 << _tagIndex[name];

        /// <summary>Combine several named tags into one mask.</summary>
        public int TagMask(params string[] names)
        {
            int m = 0;
            for (int i = 0; i < names.Length; i++) m |= 1 << _tagIndex[names[i]];
            return m;
        }

        /// <summary>Add intrinsic (always-on) tags to an actor's BaseTags. Reflected in CurrentTags after <see cref="RecomputeTags"/>.</summary>
        public void SetBaseTags(int actor, int mask)
        {
            _store.TryGetInt((ulong)actor, (ulong)_baseTagsCol, out int v);
            _store.SetInt((ulong)actor, (ulong)_baseTagsCol, v | mask);
        }

        /// <summary>Remove intrinsic tags from an actor's BaseTags.</summary>
        public void ClearBaseTags(int actor, int mask)
        {
            _store.TryGetInt((ulong)actor, (ulong)_baseTagsCol, out int v);
            _store.SetInt((ulong)actor, (ulong)_baseTagsCol, v & ~mask);
        }

        /// <summary>The actor's current tag bitmask (intrinsic + granted) as of the last <see cref="RecomputeTags"/>.</summary>
        public int GetCurrentTags(int actor)
        {
            _store.TryGetInt((ulong)actor, (ulong)_currentTagsCol, out int v);
            return v;
        }

        /// <summary>True if the actor currently has ALL the mask's bits (post-<see cref="RecomputeTags"/>).</summary>
        public bool HasTags(int actor, int mask) => (GetCurrentTags(actor) & mask) == mask;

        /// <summary>
        /// Derive CurrentTags = BaseTags (one parallel System), then OR in every active effect's granted
        /// tags (host-side scatter). Call after applying/expiring effects to refresh tag state.
        /// </summary>
        public void RecomputeTags()
        {
            _lens.Execute(_recomputeTags, _table);
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxGrantTags, out int grant);
                if (grant == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                _store.TryGetInt((ulong)actor, (ulong)_currentTagsCol, out int cur);
                _store.SetInt((ulong)actor, (ulong)_currentTagsCol, cur | grant);
            }
        }

        // ── Triggers (§6): batch tag-condition gating ────────────────────────

        /// <summary>
        /// Evaluate a Trigger across all live actors in one branchless DataLens pass, writing 1/0 into
        /// the Selected column. Two ordered ops (zero all, then set where the bitmask condition holds).
        /// </summary>
        public void EvaluateTrigger(TagTrigger trigger)
        {
            using var program = new IrProgram();
            program.Add(IrOp.Int(0, _selectedCol, SystemOp.Set, 0));
            program.Add(IrOp.Int(0, _selectedCol, SystemOp.Set, 1)
                .WithPredicate(_currentTagsCol, CompareFor(trigger.Mode), trigger.Mask));
            _lens.Execute(program, _table);
        }

        /// <summary>Number of live actors whose tags satisfy the Trigger.</summary>
        public int CountMatching(TagTrigger trigger)
        {
            EvaluateTrigger(trigger);
            _lens.RefreshView(_selectedView, _store);
            int n = _selectedView.CopyInts(_selectedScratch);
            int count = 0;
            for (int i = 0; i < n; i++)
                if (_selectedScratch[i] != 0) count++;
            return count;
        }

        /// <summary>
        /// Apply an Instant effect to an attribute's Base for every actor whose tags satisfy the Trigger
        /// (e.g. "deal 20 damage to everyone that is Burning") in ONE branchless parallel pass — the
        /// float-attribute op is gated directly by the int CurrentTags bitmask column via a mixed-type
        /// predicate System. Returns the number of actors affected. Reads CurrentTags as of the last
        /// <see cref="RecomputeTags"/>.
        /// </summary>
        public int ApplyInstantWhere(int attr, ModifierOp op, float magnitude, TagTrigger trigger)
            => (int)_lens.RunFloatWhereInt(_store, (ulong)BaseCol(attr), ToSystemOp(op), magnitude,
                (ulong)_currentTagsCol, CompareFor(trigger.Mode), trigger.Mask);

        // ── Abilities (§7): activation pipeline (cost + cooldown + requirement) ──

        /// <summary>
        /// CanActivate (§7): true if the ability's cost is affordable, its cooldown is ready (the actor
        /// lacks the cooldown tag), and its activation requirement (if any) passes against the actor's
        /// CurrentTags. Reads tags as of the last <see cref="RecomputeTags"/>.
        /// </summary>
        public bool CanActivate(int actor, in AbilityDef ability)
        {
            if (ability.CostAttr >= 0 && GetBase(actor, ability.CostAttr) < ability.CostAmount)
                return false;

            int cur = GetCurrentTags(actor);
            if (ability.CooldownTag != 0 && (cur & ability.CooldownTag) != 0)
                return false; // on cooldown

            if (ability.Requirement.HasValue && !MatchesTrigger(cur, ability.Requirement.Value))
                return false;

            return true;
        }

        /// <summary>
        /// Try to activate an ability for an actor. If <see cref="CanActivate"/>, Commit (§7): spend the
        /// cost (Instant on Base) and start the cooldown (a duration effect granting the cooldown tag,
        /// reflected immediately so re-activation this step is blocked). Returns whether it activated.
        /// The cooldown ticks down via the normal step loop (AdvanceTick → ExpireEffects → RecomputeTags).
        /// </summary>
        public bool TryActivate(int actor, in AbilityDef ability)
        {
            if (!CanActivate(actor, ability))
                return false;

            if (ability.CostAttr >= 0 && ability.CostAmount != 0f)
                ApplyInstant(actor, ability.CostAttr, ModifierOp.Add, -ability.CostAmount);

            if (ability.CooldownTag != 0 && ability.CooldownTicks > 0)
            {
                int cdAttr = ability.CostAttr >= 0 ? ability.CostAttr : 0; // magnitude 0: a pure tag grant
                ApplyDuration(actor, cdAttr, 0f, ability.CooldownTicks, ability.CooldownTag);
                OrCurrentTags(actor, ability.CooldownTag);
            }

            // Cosmetic cue on activation (e.g. cast start).
            if (ability.ActivateCue >= 0)
                EmitCue(ability.ActivateCue, actor, 0f);

            // Schedule the payload task (a delayed effect + cue), fired by AdvanceAbilities.
            if (ability.EffectAttr >= 0 || ability.EffectCue >= 0)
                ScheduleTask(actor, _currentTick + ability.EffectDelayTicks,
                             ability.EffectAttr, ability.EffectOp, ability.EffectMag, ability.EffectCue);

            return true;
        }

        // ── Ability Tasks (§7 step 3) + Cues (§9) ────────────────────────────

        /// <summary>Append a cosmetic cue for presentation to drain. Never affects simulation state.</summary>
        public void EmitCue(int cueId, int actor, float magnitude)
            => _cues.Add(new CueEvent(cueId, actor, magnitude));

        /// <summary>The cue events emitted since the last <see cref="ClearCues"/> (drain each frame).</summary>
        public System.Collections.Generic.IReadOnlyList<CueEvent> PendingCues => _cues;

        /// <summary>Clear the cue stream (after presentation has drained it).</summary>
        public void ClearCues() => _cues.Clear();

        /// <summary>Number of in-flight (scheduled, not yet fired) ability payload tasks.</summary>
        public int PendingTaskCount => (int)_tasks.LiveCount;

        private void ScheduleTask(int actor, int fireTick, int effectAttr, ModifierOp op, float mag, int cue)
        {
            ulong r = _tasks.AllocRow();
            if (r == DataStore.InvalidRow) return; // task store full: drop (fixed capacity)
            _tasks.SetInt(r, (ulong)TkActor, actor);
            _tasks.SetInt(r, (ulong)TkFireTick, fireTick);
            _tasks.SetInt(r, (ulong)TkEffectAttr, effectAttr);
            _tasks.SetInt(r, (ulong)TkEffectOp, (int)op);
            _tasks.SetFloat(r, (ulong)TkEffectMag, mag);
            _tasks.SetInt(r, (ulong)TkCue, cue);
        }

        /// <summary>
        /// Advance in-flight ability tasks: fire every pending payload whose FireTick has arrived
        /// (apply its effect to Base, emit its cue), then release the slot. Call once per step after
        /// <see cref="AdvanceTick"/>. Returns the number of tasks fired. Effects land on Base, so call
        /// <see cref="RecomputeCurrent"/> afterwards to reflect them in Current.
        /// </summary>
        public int AdvanceAbilities()
        {
            int fired = 0;
            for (int r = 0; r < _tasksCapacity; r++)
            {
                if (!_tasks.IsValid((ulong)r)) continue;
                _tasks.TryGetInt((ulong)r, (ulong)TkFireTick, out int fireTick);
                if (fireTick > _currentTick) continue;

                _tasks.TryGetInt((ulong)r, (ulong)TkActor, out int actor);
                _tasks.TryGetInt((ulong)r, (ulong)TkEffectAttr, out int effectAttr);
                _tasks.TryGetInt((ulong)r, (ulong)TkEffectOp, out int opCode);
                _tasks.TryGetFloat((ulong)r, (ulong)TkEffectMag, out float mag);
                _tasks.TryGetInt((ulong)r, (ulong)TkCue, out int cue);

                if (effectAttr >= 0)
                    ApplyInstant(actor, effectAttr, (ModifierOp)opCode, mag);
                if (cue >= 0)
                    EmitCue(cue, actor, mag);

                _tasks.FreeRow((ulong)r);
                fired++;
            }
            return fired;
        }

        // ── Mass AI: batch CanActivate (§7) + utility weights (§8) ───────────

        /// <summary>
        /// Compute, for EVERY live actor, whether it could activate the ability right now — into the
        /// per-actor Eligible column (1/0), as a few branchless column passes (the batch form of
        /// <see cref="CanActivate"/> for mass AI). Eligible starts true and is knocked out by: cost
        /// unaffordable (Base &lt; cost, a float-predicate pass), on cooldown (HasAny cooldown tag), and
        /// the activation requirement failing. Reads tags as of the last <see cref="RecomputeTags"/>.
        /// (Batch requirement supports RequireAny/Exclude; multi-bit RequireAll is not expressible as a
        /// single knock-out — use single-bit masks or per-actor CanActivate.)
        /// </summary>
        public void EvaluateEligibility(in AbilityDef ability)
        {
            _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 1); // all eligible to start

            if (ability.CostAttr >= 0)
                // knock out where Base[cost] < CostAmount (int op gated by a float predicate)
                _lens.RunIntWhereFloat(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                    (ulong)BaseCol(ability.CostAttr), CompareOp.Less, ability.CostAmount);

            if (ability.CooldownTag != 0)
                // knock out where on cooldown (has any cooldown bit)
                _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                    (ulong)_currentTagsCol, CompareOp.HasAnyBits, ability.CooldownTag);

            if (ability.Requirement.HasValue)
            {
                TagTrigger req = ability.Requirement.Value;
                switch (req.Mode)
                {
                    case TriggerMode.Exclude: // eligible needs none of mask -> knock out where it HAS any
                        _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                            (ulong)_currentTagsCol, CompareOp.HasAnyBits, req.Mask);
                        break;
                    case TriggerMode.RequireAny: // eligible needs any of mask -> knock out where it LACKS all
                        _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                            (ulong)_currentTagsCol, CompareOp.LacksBits, req.Mask);
                        break;
                    case TriggerMode.RequireAll:
                        throw new System.NotSupportedException(
                            "Batch eligibility does not support multi-bit RequireAll; use RequireAny single-bit masks or per-actor CanActivate.");
                }
            }
        }

        /// <summary>True if the actor's Eligible flag is set (read after <see cref="EvaluateEligibility"/>).</summary>
        public bool IsEligible(int actor)
        {
            _store.TryGetInt((ulong)actor, (ulong)_eligibleCol, out int v);
            return v != 0;
        }

        /// <summary>Evaluate eligibility for the ability and return how many live actors are eligible.</summary>
        public int CountEligible(in AbilityDef ability)
        {
            EvaluateEligibility(ability);
            _lens.RefreshView(_eligibleView, _store);
            int n = _eligibleView.CopyInts(_eligibleScratch);
            int count = 0;
            for (int i = 0; i < n; i++)
                if (_eligibleScratch[i] != 0) count++;
            return count;
        }

        /// <summary>
        /// Compute a per-actor utility score for the ability into the Utility column (§8 Weights): a
        /// linear weight over <paramref name="weightAttr"/>'s Current — <c>clamp01(slope*Current + offset)</c>
        /// — zeroed for ineligible actors. For "weight rises as the attribute falls" (e.g. an execute that
        /// favours low-Health targets), pass a negative slope. All branchless column passes; call
        /// <see cref="RecomputeCurrent"/> first so Current is up to date.
        /// </summary>
        public void EvaluateUtility(in AbilityDef ability, int weightAttr, float slope, float offset)
        {
            EvaluateEligibility(ability);
            _lens.RunFloatColumn(_store, (ulong)_utilityCol, SystemOp.Set, (ulong)CurrentCol(weightAttr));
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Mul, slope);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Add, offset);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Max, 0f);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Min, 1f);
            // zero out ineligible actors (float op gated by the int Eligible column)
            _lens.RunFloatWhereInt(_store, (ulong)_utilityCol, SystemOp.Set, 0f,
                (ulong)_eligibleCol, CompareOp.Equal, 0);
        }

        /// <summary>Actor's last-computed utility score (read after <see cref="EvaluateUtility"/>).</summary>
        public float GetUtility(int actor)
        {
            _store.TryGetFloat((ulong)actor, (ulong)_utilityCol, out float v);
            return v;
        }

        /// <summary>
        /// Scan the Utility column and return the actor with the highest score (and its score), or
        /// <see cref="InvalidActor"/> if none has utility &gt; 0. The "AI as a column scan" pick step.
        /// </summary>
        public int BestActorByUtility(out float bestUtility)
        {
            _lens.RefreshView(_utilityView, _store);
            int n = _utilityView.CopyFloats(_utilityScratch);
            int best = InvalidActor;
            float bestU = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = _utilityScratch[i];
                if (u > bestU)
                {
                    bestU = u;
                    best = (int)_utilityView.SourceRow((ulong)i);
                }
            }
            bestUtility = bestU;
            return best;
        }

        private bool MatchesTrigger(int currentTags, TagTrigger t)
        {
            switch (t.Mode)
            {
                case TriggerMode.RequireAll: return (currentTags & t.Mask) == t.Mask;
                case TriggerMode.RequireAny: return (currentTags & t.Mask) != 0;
                case TriggerMode.Exclude:    return (currentTags & t.Mask) == 0;
                default:                     return true;
            }
        }

        private void OrCurrentTags(int actor, int mask)
        {
            _store.TryGetInt((ulong)actor, (ulong)_currentTagsCol, out int cur);
            _store.SetInt((ulong)actor, (ulong)_currentTagsCol, cur | mask);
        }

        private static CompareOp CompareFor(TriggerMode mode)
        {
            switch (mode)
            {
                case TriggerMode.RequireAll: return CompareOp.HasAllBits;
                case TriggerMode.RequireAny: return CompareOp.HasAnyBits;
                case TriggerMode.Exclude:    return CompareOp.LacksBits;
                default:                     return CompareOp.HasAllBits;
            }
        }

        private static float Combine(float value, ModifierOp op, float magnitude)
        {
            switch (op)
            {
                case ModifierOp.Add:      return value + magnitude;
                case ModifierOp.Multiply: return value * magnitude;
                case ModifierOp.Override: return magnitude;
                default:                  return value;
            }
        }

        private static SystemOp ToSystemOp(ModifierOp op)
        {
            switch (op)
            {
                case ModifierOp.Add:      return SystemOp.Add;
                case ModifierOp.Multiply: return SystemOp.Mul;
                case ModifierOp.Override: return SystemOp.Set;
                default:                  return SystemOp.Set;
            }
        }

        public void Dispose()
        {
            _activeView?.Dispose();
            _selectedView?.Dispose();
            _eligibleView?.Dispose();
            _utilityView?.Dispose();
            _recomputeCurrent?.Dispose();
            _recomputeTags?.Dispose();
            _lens?.Dispose();
            _effects?.Dispose();
            _tasks?.Dispose();
            _store?.Dispose();
        }
    }
}
