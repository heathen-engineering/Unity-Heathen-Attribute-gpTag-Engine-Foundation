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
        private const int FxActor = 0, FxAttr = 1, FxMagnitude = 2, FxEndTick = 3, FxActive = 4, FxGrantTags = 5, FxGrantAbilities = 6, FxOp = 7, FxEffectId = 8, FxStackCount = 9, FxGrantImmunity = 10, FxReqMask = 11, FxReqMode = 12, FxSuspended = 13;
        private const int EffectsStoreIndex = 1;

        private DataView _activeView;     // over the effects Active column, for reclaim read-back
        private int[] _activeScratch;     // managed buffer for the bulk read

        private int _currentTick;

        // ── Tags (§5.5 granted tags / §6 Trigger gating). Up to 32 "hot" tags packed as bits in an
        //    Int32 mask column, so a Trigger is a branchless DataLens bitmask predicate over all actors. ──
        private readonly Dictionary<string, int> _tagIndex;       // tag name -> bit index (0..31)
        private readonly string[] _tagNames;                      // bit index -> tag name (for inspection/tooling)
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

        // ── Utility AI v2 (§8 score→perturb→select, D5.2). K ability score slots + a per-actor Variance
        //    (skill/fatigue dial), a Command override column (-1 = none), a Choice output, and a scratch
        //    column for weighted-sum accumulation. Only allocated when abilityScoreSlots > 0. ──
        private readonly int _abilityScoreSlots;
        private readonly int _scoreCol0;      // first of K Float score columns (slot s = _scoreCol0 + s)
        private readonly int _varianceCol;    // Float, per-actor skill/fatigue
        private readonly int _commandCol;     // Int32, -1 = no command (else forced ability slot)
        private readonly int _choiceCol;      // Int32, the chosen ability slot (-1 = none)
        private readonly int _scratchCol;     // Float, per-consideration scratch for WeightedSum
        private ulong[] _scoreColsUlong;      // score column indices for the argmax select pass
        private DataView _choiceView;
        private int[] _choiceScratch;

        // ── Abilities (§7): in-flight payload TASKS (a delayed effect+cue per row, fired by tick) + the
        //    cosmetic CUE output stream (§9, drained by presentation — never simulation state). ──
        private readonly DataStore _tasks;
        private readonly int _tasksCapacity;
        private const int TkActor = 0, TkFireTick = 1, TkEffectAttr = 2, TkEffectOp = 3, TkEffectMag = 4, TkCue = 5;
        private readonly System.Collections.Generic.List<CueEvent> _cues = new System.Collections.Generic.List<CueEvent>();

        // ── Ability granting (§7 "AbilityInstances"). AbilityDefs are a compact catalogue (managed list,
        //    indexed by ability id); which abilities each actor holds is a per-actor Int32 bitmask column
        //    (bit k = catalogue[k] granted) — the hot, branchless, batch-testable "instance" record, max 32
        //    distinct abilities per world (mirrors the 32 hot tags). Activation is gated on the grant. ──
        private const int MaxAbilities = 32;
        private readonly int _baseAbilitiesCol;    // Int32 bitmask: intrinsic grants (mirrors BaseTags)
        private readonly int _grantedAbilitiesCol; // Int32 bitmask: effective = base OR active-effect grants (mirrors CurrentTags)
        private readonly IrProgram _recomputeAbilities; // GrantedAbilities = BaseGrantedAbilities
        private readonly System.Collections.Generic.List<AbilityDef> _abilityCatalogue = new System.Collections.Generic.List<AbilityDef>();

        // ── Immunity (§5.5). Per-actor immunity mask (over the same hot-tag bits): an incoming effect's
        //    asset tags matching it are blocked. Base (intrinsic) + effective (base OR active-effect grants),
        //    like tags/abilities; effects confer immunity for their lifetime via FxGrantImmunity. ──
        private readonly int _baseImmunityCol;     // Int32 bitmask: intrinsic immunity
        private readonly int _immunityCol;         // Int32 bitmask: effective = base OR active-effect grants
        private readonly IrProgram _recomputeImmunity; // Immunity = BaseImmunity

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
            : this(attributeNames, tagNames, 0, capacity) { }

        /// <summary>
        /// As the tag-aware constructor, plus <paramref name="abilityScoreSlots"/> dedicated ability-score
        /// columns for the §8 utility-AI pipeline (D5.2): score (multi-consideration) → perturb (Variance ×
        /// counter-based noise) → select (argmax + Command override). Pass 0 to omit the utility-AI columns.
        /// </summary>
        public HateWorld(string[] attributeNames, string[] tagNames, int abilityScoreSlots, int capacity)
        {
            if (attributeNames == null) throw new ArgumentNullException(nameof(attributeNames));
            if (attributeNames.Length == 0) throw new ArgumentException("Need at least one attribute.", nameof(attributeNames));
            if (tagNames == null) throw new ArgumentNullException(nameof(tagNames));
            if (tagNames.Length > 32) throw new ArgumentException("At most 32 hot tags (Int32 bitmask).", nameof(tagNames));
            if (capacity <= 0) throw new ArgumentException("Capacity must be positive.", nameof(capacity));
            if (abilityScoreSlots < 0 || abilityScoreSlots > 64)
                throw new ArgumentException("abilityScoreSlots must be in 0..64.", nameof(abilityScoreSlots));

            _attrNames = (string[])attributeNames.Clone();
            _tagNames = (string[])tagNames.Clone();
            _attrIndex = new Dictionary<string, int>(attributeNames.Length);
            _tagIndex = new Dictionary<string, int>(tagNames.Length);
            for (int t = 0; t < tagNames.Length; t++)
                _tagIndex[tagNames[t]] = t;

            // Actor columns: [a0.Base, a0.Current, …] (Float) then BaseTags, CurrentTags, Selected,
            // Eligible (Int32), Utility (Float), BaseAbilities + GrantedAbilities (Int32, intrinsic vs
            // effective grants — mirrors BaseTags/CurrentTags); then (when abilityScoreSlots>0) K Score
            // columns (Float), Variance (Float), Command (Int32), Choice (Int32), WeightedSum scratch (Float).
            _abilityScoreSlots = abilityScoreSlots;
            int attrCols = attributeNames.Length * 2;
            _baseTagsCol = attrCols;
            _currentTagsCol = attrCols + 1;
            _selectedCol = attrCols + 2;
            _eligibleCol = attrCols + 3;
            _utilityCol = attrCols + 4;
            _baseAbilitiesCol = attrCols + 5;
            _grantedAbilitiesCol = attrCols + 6;
            _baseImmunityCol = attrCols + 7;
            _immunityCol = attrCols + 8;

            int colCount = attrCols + 9;
            if (abilityScoreSlots > 0)
            {
                _scoreCol0 = colCount;
                _varianceCol = _scoreCol0 + abilityScoreSlots;
                _commandCol = _varianceCol + 1;
                _choiceCol = _commandCol + 1;
                _scratchCol = _choiceCol + 1;
                colCount = _scratchCol + 1;
            }

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
            colNames[_baseAbilitiesCol] = "BaseAbilities";       colTypes[_baseAbilitiesCol] = DataLensValueType.Int32;
            colNames[_grantedAbilitiesCol] = "GrantedAbilities"; colTypes[_grantedAbilitiesCol] = DataLensValueType.Int32;
            colNames[_baseImmunityCol] = "BaseImmunity";         colTypes[_baseImmunityCol] = DataLensValueType.Int32;
            colNames[_immunityCol] = "Immunity";                 colTypes[_immunityCol] = DataLensValueType.Int32;
            if (abilityScoreSlots > 0)
            {
                for (int s = 0; s < abilityScoreSlots; s++)
                {
                    colNames[_scoreCol0 + s] = "Score" + s; colTypes[_scoreCol0 + s] = DataLensValueType.Float;
                }
                colNames[_varianceCol] = "Variance"; colTypes[_varianceCol] = DataLensValueType.Float;
                colNames[_commandCol] = "Command";   colTypes[_commandCol] = DataLensValueType.Int32;
                colNames[_choiceCol] = "Choice";     colTypes[_choiceCol] = DataLensValueType.Int32;
                colNames[_scratchCol] = "ScoreScratch"; colTypes[_scratchCol] = DataLensValueType.Float;

                _scoreColsUlong = new ulong[abilityScoreSlots];
                for (int s = 0; s < abilityScoreSlots; s++) _scoreColsUlong[s] = (ulong)(_scoreCol0 + s);
            }

            _store = new DataStore(colNames, colTypes, (ulong)capacity);
            _lens = new Lens(0); // hardware concurrency

            // Active-effects store: a row per live duration effect (no heap object — the §5.2 win).
            _effectsCapacity = capacity * 4; // a few effects per actor; fixed, recycled on expiry
            _effects = new DataStore(
                new[] { "Actor", "Attr", "Magnitude", "EndTick", "Active", "GrantTags", "GrantAbilities", "Op", "EffectId", "StackCount", "GrantImmunity", "ReqMask", "ReqMode", "Suspended" },
                new[] { DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Float,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32 },
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
            if (abilityScoreSlots > 0)
            {
                _choiceView = new DataView(new ulong[] { (ulong)_choiceCol });
                _choiceScratch = new int[capacity];
            }

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

            // Reusable: GrantedAbilities = BaseAbilities (effect-granted abilities OR'd on top per recompute).
            _recomputeAbilities = new IrProgram();
            _recomputeAbilities.Add(IrOp.IntColumn(0, _grantedAbilitiesCol, SystemOp.Set, _baseAbilitiesCol));

            // Reusable: Immunity = BaseImmunity (effect-granted immunity OR'd on top per recompute).
            _recomputeImmunity = new IrProgram();
            _recomputeImmunity.Add(IrOp.IntColumn(0, _immunityCol, SystemOp.Set, _baseImmunityCol));
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
            if (row == DataStore.InvalidRow) return InvalidActor;
            // Reused slots must not inherit a previous occupant's grants (intrinsic or effective).
            _store.SetInt(row, (ulong)_baseAbilitiesCol, 0);
            _store.SetInt(row, (ulong)_grantedAbilitiesCol, 0);
            _store.SetInt(row, (ulong)_baseImmunityCol, 0);
            _store.SetInt(row, (ulong)_immunityCol, 0);
            if (_abilityScoreSlots > 0)
            {
                // …or its command/variance/choice.
                _store.SetInt(row, (ulong)_commandCol, -1);
                _store.SetInt(row, (ulong)_choiceCol, -1);
                _store.SetFloat(row, (ulong)_varianceCol, 0f);
            }
            return (int)row;
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
            => ApplyDuration(actor, attr, magnitude, durationTicks, grantTags, 0);

        /// <summary>
        /// As <see cref="ApplyDuration(int,int,float,int,int)"/> but the effect also GRANTS the given
        /// ability mask (§7 effect-driven granting) for its lifetime — OR'd into GrantedAbilities by
        /// <see cref="RecomputeAbilities"/>, dropped when the effect expires. Use <see cref="GrantAbilityFor"/>
        /// for the common "grant one ability for N ticks" case.
        /// </summary>
        public int ApplyDuration(int actor, int attr, float magnitude, int durationTicks, int grantTags, int grantAbilities)
            => ApplyDurationCore(actor, attr, ModifierOp.Add, magnitude, durationTicks, grantTags, grantAbilities);

        /// <summary>
        /// Apply a duration effect on a specific aggregation channel (§5.3): <see cref="ModifierOp.Add"/>
        /// (Current += Σmag), <see cref="ModifierOp.Multiply"/> (Current ·= Πmag), or
        /// <see cref="ModifierOp.Override"/> (Current = mag, applied last). Channels are combined by
        /// <see cref="RecomputeCurrent"/> as <c>Current = override ?? (Base + ΣAdd)·ΠMul</c>. Returns the
        /// effect handle, or <see cref="InvalidEffect"/> when the store is full.
        /// </summary>
        public int ApplyDuration(int actor, int attr, ModifierOp op, float magnitude, int durationTicks)
            => ApplyDurationCore(actor, attr, op, magnitude, durationTicks, 0, 0);

        /// <summary>
        /// Apply a duration effect with an ONGOING requirement (§5.5): each step <see cref="RecomputeSuspension"/>
        /// tests <paramref name="ongoingRequirement"/> against the actor's CurrentTags; while it fails the
        /// effect is SUSPENDED (its modifiers, granted tags/abilities/immunity stop applying) but is NOT
        /// removed — it resumes when the condition returns, and still expires on its EndTick. Returns the
        /// effect handle.
        /// </summary>
        public int ApplyDuration(int actor, int attr, ModifierOp op, float magnitude, int durationTicks, TagTrigger ongoingRequirement)
            => ApplyDurationCore(actor, attr, op, magnitude, durationTicks, 0, 0, 0, ongoingRequirement.Mask, (int)ongoingRequirement.Mode);

        private int ApplyDurationCore(int actor, int attr, ModifierOp op, float magnitude, int durationTicks,
            int grantTags, int grantAbilities, int grantImmunity = 0, int reqMask = 0, int reqMode = -1)
        {
            ulong r = _effects.AllocRow();
            if (r == DataStore.InvalidRow) return InvalidEffect;
            _effects.SetInt(r, (ulong)FxActor, actor);
            _effects.SetInt(r, (ulong)FxAttr, attr);
            _effects.SetFloat(r, (ulong)FxMagnitude, magnitude);
            _effects.SetInt(r, (ulong)FxEndTick, _currentTick + durationTicks);
            _effects.SetInt(r, (ulong)FxActive, 1);
            _effects.SetInt(r, (ulong)FxGrantTags, grantTags);
            _effects.SetInt(r, (ulong)FxGrantAbilities, grantAbilities);
            _effects.SetInt(r, (ulong)FxOp, (int)op);
            _effects.SetInt(r, (ulong)FxEffectId, -1); // non-stacking
            _effects.SetInt(r, (ulong)FxStackCount, 1);
            _effects.SetInt(r, (ulong)FxGrantImmunity, grantImmunity);
            _effects.SetInt(r, (ulong)FxReqMask, reqMask);
            _effects.SetInt(r, (ulong)FxReqMode, reqMode);   // -1 = no ongoing requirement
            _effects.SetInt(r, (ulong)FxSuspended, 0);
            return (int)r;
        }

        /// <summary>
        /// Apply a STACKING duration effect (§5.5): effects sharing an <paramref name="effectId"/> on the
        /// same actor collapse to one row carrying a <c>StackCount</c> instead of spawning a new row. A
        /// re-application bumps the count (capped at <paramref name="stackLimit"/>) and applies the
        /// <paramref name="refresh"/> policy to the remaining duration. The modifier's contribution scales
        /// with the count: Add → count·mag, Multiply → mag^count, Override → mag. (Aggregate-by-target
        /// stacking; per-source stacking is a later refinement.) Returns the stack's effect handle.
        /// </summary>
        public int ApplyStackingDuration(int actor, int attr, ModifierOp op, float magnitudePerStack,
            int durationTicks, int effectId, int stackLimit, StackRefresh refresh = StackRefresh.RefreshDuration)
        {
            if (effectId < 0) // not actually stacking → an ordinary duration effect
                return ApplyDurationCore(actor, attr, op, magnitudePerStack, durationTicks, 0, 0);

            int cap = stackLimit < 1 ? 1 : stackLimit;
            int existing = FindActiveStack(actor, effectId);
            if (existing >= 0)
            {
                _effects.TryGetInt((ulong)existing, (ulong)FxStackCount, out int count);
                count = count + 1 > cap ? cap : count + 1; // overflow: cap, still refresh below
                _effects.SetInt((ulong)existing, (ulong)FxStackCount, count);

                int newEnd = _currentTick + durationTicks;
                if (refresh == StackRefresh.RefreshDuration)
                {
                    _effects.SetInt((ulong)existing, (ulong)FxEndTick, newEnd);
                }
                else if (refresh == StackRefresh.KeepLongest)
                {
                    _effects.TryGetInt((ulong)existing, (ulong)FxEndTick, out int curEnd);
                    if (newEnd > curEnd) _effects.SetInt((ulong)existing, (ulong)FxEndTick, newEnd);
                }
                // KeepExisting: leave EndTick untouched.
                return existing;
            }

            // First stack: a fresh row with StackCount 1.
            ulong r = _effects.AllocRow();
            if (r == DataStore.InvalidRow) return InvalidEffect;
            _effects.SetInt(r, (ulong)FxActor, actor);
            _effects.SetInt(r, (ulong)FxAttr, attr);
            _effects.SetFloat(r, (ulong)FxMagnitude, magnitudePerStack);
            _effects.SetInt(r, (ulong)FxEndTick, _currentTick + durationTicks);
            _effects.SetInt(r, (ulong)FxActive, 1);
            _effects.SetInt(r, (ulong)FxGrantTags, 0);
            _effects.SetInt(r, (ulong)FxGrantAbilities, 0);
            _effects.SetInt(r, (ulong)FxOp, (int)op);
            _effects.SetInt(r, (ulong)FxEffectId, effectId);
            _effects.SetInt(r, (ulong)FxStackCount, 1);
            _effects.SetInt(r, (ulong)FxGrantImmunity, 0);
            _effects.SetInt(r, (ulong)FxReqMask, 0);
            _effects.SetInt(r, (ulong)FxReqMode, -1);
            _effects.SetInt(r, (ulong)FxSuspended, 0);
            return (int)r;
        }

        /// <summary>The current stack count of an effect row (1 for a non-stacking effect).</summary>
        public int GetStackCount(int effectHandle)
        {
            _effects.TryGetInt((ulong)effectHandle, (ulong)FxStackCount, out int c);
            return c;
        }

        // Find an active stacking row for (actor, effectId), or -1. Scans active rows (bounded by live
        // effect count); a (actor,effectId)->row index could replace this if stacking churn ever shows up.
        private int FindActiveStack(int actor, int effectId)
        {
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxEffectId, out int id);
                if (id != effectId) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int a);
                if (a == actor) return r;
            }
            return -1;
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
        /// Derive Current from Base for every attribute (one parallel System), then fold in every active
        /// duration effect by aggregation channel (§5.3): <c>Current = override ?? (Base + ΣAdd)·ΠMul</c>.
        /// The Current=Base pass is parallel; the per-channel accumulation is host-side over the ACTIVE
        /// effects (cost ∝ live effects, not actor count — moving it to a dirty-driven System is future work).
        /// </summary>
        public void RecomputeCurrent()
        {
            _lens.Execute(_recomputeCurrent, _table);
            AddActiveModifiers();
        }

        // Per-(actor,attr) modifier channels, reused across RecomputeCurrent calls (cleared each time).
        private struct ModChannels { public float SumAdd; public float ProdMul; public bool HasOverride; public float OverrideValue; }
        private readonly Dictionary<long, ModChannels> _modAccum = new Dictionary<long, ModChannels>();
        private static long ModKey(int actor, int attr) => ((long)actor << 32) | (uint)attr;

        private void AddActiveModifiers()
        {
            if (_effects.LiveCount == 0) return; // no active duration effects: Current is just Base
            _modAccum.Clear();

            // Pass 1: accumulate each active effect into its (actor,attr) channels.
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxSuspended, out int suspended);
                if (suspended != 0) continue; // a failing ongoing requirement (§5.5) — contributes nothing
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                _effects.TryGetInt((ulong)r, (ulong)FxAttr, out int attr);
                _effects.TryGetFloat((ulong)r, (ulong)FxMagnitude, out float mag);
                _effects.TryGetInt((ulong)r, (ulong)FxOp, out int opCode);
                _effects.TryGetInt((ulong)r, (ulong)FxStackCount, out int count);
                if (count < 1) count = 1; // stacks scale the contribution (§5.5)

                long key = ModKey(actor, attr);
                if (!_modAccum.TryGetValue(key, out var ch)) ch = new ModChannels { ProdMul = 1f };
                switch ((ModifierOp)opCode)
                {
                    case ModifierOp.Add:      ch.SumAdd += mag * count; break;            // count·mag
                    case ModifierOp.Multiply: for (int s = 0; s < count; s++) ch.ProdMul *= mag; break; // mag^count
                    case ModifierOp.Override: ch.HasOverride = true; ch.OverrideValue = mag; break;     // last write in scan wins
                }
                _modAccum[key] = ch;
            }

            // Pass 2: Current = override ?? (Base + ΣAdd)·ΠMul, once per touched (actor,attr).
            foreach (var kv in _modAccum)
            {
                int actor = (int)(kv.Key >> 32);
                int attr = (int)(kv.Key & 0xffffffff);
                var ch = kv.Value;
                float baseV = GetBase(actor, attr);
                float cur = ch.HasOverride ? ch.OverrideValue : (baseV + ch.SumAdd) * ch.ProdMul;
                _store.SetFloat((ulong)actor, (ulong)CurrentCol(attr), cur);
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
            if (_effects.LiveCount == 0) return; // no granting effects: CurrentTags is just BaseTags
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxSuspended, out int suspended);
                if (suspended != 0) continue;
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

        // ── Ability granting (§7 "AbilityInstances") ─────────────────────────
        // AbilityDefs are a compact catalogue; which abilities an actor holds is a per-actor bitmask.
        // Only granted abilities can be activated by id (parity with UE GiveAbility / FGameplayAbilitySpec).

        /// <summary>Number of abilities registered in the catalogue.</summary>
        public int AbilityCount => _abilityCatalogue.Count;

        /// <summary>
        /// Register an ability definition in the world's catalogue and return its <b>ability id</b> (0-based,
        /// max 32). The id is the bit used by <see cref="GrantAbility"/> and the handle for the id-based
        /// <see cref="CanActivate(int,int)"/>/<see cref="TryActivate(int,int)"/>. Definitions are shared data;
        /// per-actor state lives in the grant bitmask (and the existing cooldown tags / effect rows).
        /// </summary>
        public int RegisterAbility(in AbilityDef ability)
        {
            if (_abilityCatalogue.Count >= MaxAbilities)
                throw new InvalidOperationException($"At most {MaxAbilities} abilities per world (Int32 grant bitmask).");
            _abilityCatalogue.Add(ability);
            return _abilityCatalogue.Count - 1;
        }

        /// <summary>The registered definition for an ability id.</summary>
        public AbilityDef GetAbility(int abilityId)
        {
            if ((uint)abilityId >= (uint)_abilityCatalogue.Count)
                throw new ArgumentOutOfRangeException(nameof(abilityId));
            return _abilityCatalogue[abilityId];
        }

        private int AbilityBit(int abilityId)
        {
            if ((uint)abilityId >= (uint)_abilityCatalogue.Count)
                throw new ArgumentOutOfRangeException(nameof(abilityId));
            return 1 << abilityId;
        }

        // Intrinsic grants live in BaseAbilities; the effective GrantedAbilities (read by HasAbility /
        // activation / eligibility) is BaseAbilities OR active-effect grants, rebuilt by RecomputeAbilities.
        // Direct grant/revoke writes BOTH so the change is usable the same frame without a recompute
        // (mirrors the cooldown's immediate-reflect trick); RecomputeAbilities then keeps them consistent.

        /// <summary>Grant an intrinsic ability to one actor (sets its base + effective grant bit). Idempotent.</summary>
        public void GrantAbility(int actor, int abilityId)
        {
            int bit = AbilityBit(abilityId);
            _store.TryGetInt((ulong)actor, (ulong)_baseAbilitiesCol, out int b);
            _store.SetInt((ulong)actor, (ulong)_baseAbilitiesCol, b | bit);
            _store.TryGetInt((ulong)actor, (ulong)_grantedAbilitiesCol, out int e);
            _store.SetInt((ulong)actor, (ulong)_grantedAbilitiesCol, e | bit);
        }

        /// <summary>Revoke an intrinsic ability from one actor (clears its base + effective grant bit).
        /// If an active effect still grants it, the next <see cref="RecomputeAbilities"/> restores it.</summary>
        public void RevokeAbility(int actor, int abilityId)
        {
            int bit = AbilityBit(abilityId);
            _store.TryGetInt((ulong)actor, (ulong)_baseAbilitiesCol, out int b);
            _store.SetInt((ulong)actor, (ulong)_baseAbilitiesCol, b & ~bit);
            _store.TryGetInt((ulong)actor, (ulong)_grantedAbilitiesCol, out int e);
            _store.SetInt((ulong)actor, (ulong)_grantedAbilitiesCol, e & ~bit);
        }

        /// <summary>Revoke every intrinsic ability from one actor (effect grants reappear on recompute).</summary>
        public void RevokeAllAbilities(int actor)
        {
            _store.SetInt((ulong)actor, (ulong)_baseAbilitiesCol, 0);
            _store.SetInt((ulong)actor, (ulong)_grantedAbilitiesCol, 0);
        }

        /// <summary>Grant an ability to every live actor in one pass (e.g. a class/role's kit). Parallel.</summary>
        public ulong GrantAbilityAll(int abilityId)
        {
            int bit = AbilityBit(abilityId);
            _lens.RunInt(_store, (ulong)_baseAbilitiesCol, SystemOp.Or, bit);
            return _lens.RunInt(_store, (ulong)_grantedAbilitiesCol, SystemOp.Or, bit);
        }

        /// <summary>Revoke an ability from every live actor in one pass. Parallel.</summary>
        public ulong RevokeAbilityAll(int abilityId)
        {
            int bit = AbilityBit(abilityId);
            _lens.RunInt(_store, (ulong)_baseAbilitiesCol, SystemOp.AndNot, bit);
            return _lens.RunInt(_store, (ulong)_grantedAbilitiesCol, SystemOp.AndNot, bit);
        }

        /// <summary>True if the actor currently holds the ability (its effective grant bit is set).</summary>
        public bool HasAbility(int actor, int abilityId)
        {
            _store.TryGetInt((ulong)actor, (ulong)_grantedAbilitiesCol, out int cur);
            return (cur & AbilityBit(abilityId)) != 0;
        }

        /// <summary>The actor's full effective granted-ability bitmask (intrinsic + effect-granted).</summary>
        public int GetGrantedAbilities(int actor)
        {
            _store.TryGetInt((ulong)actor, (ulong)_grantedAbilitiesCol, out int cur);
            return cur;
        }

        /// <summary>
        /// Grant an ability to an actor for <paramref name="durationTicks"/> ticks via an auto-expiring
        /// duration effect (§7 effect-driven granting, UE parity): a pure grant effect (no attribute change).
        /// The grant appears in <see cref="GetGrantedAbilities"/> after the next <see cref="RecomputeAbilities"/>
        /// and is dropped automatically when the effect expires (<see cref="ExpireEffects"/> + recompute).
        /// Returns the effect handle, or <see cref="InvalidEffect"/> when the effects store is full.
        /// </summary>
        public int GrantAbilityFor(int actor, int abilityId, int durationTicks)
            => ApplyDuration(actor, 0, 0f, durationTicks, 0, AbilityBit(abilityId));

        /// <summary>
        /// Derive GrantedAbilities = BaseAbilities (one parallel System), then OR in every active effect's
        /// granted abilities (host-side scatter). Call after applying/expiring effects (alongside
        /// <see cref="RecomputeTags"/>) to refresh effect-driven ability grants.
        /// </summary>
        public void RecomputeAbilities()
        {
            _lens.Execute(_recomputeAbilities, _table);
            if (_effects.LiveCount == 0) return; // no granting effects: effective is just BaseAbilities
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxSuspended, out int suspended);
                if (suspended != 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxGrantAbilities, out int grant);
                if (grant == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                _store.TryGetInt((ulong)actor, (ulong)_grantedAbilitiesCol, out int cur);
                _store.SetInt((ulong)actor, (ulong)_grantedAbilitiesCol, cur | grant);
            }
        }

        // ── Immunity (§5.5) ──────────────────────────────────────────────────
        // An actor's immunity mask (over the hot-tag bits) says which effect "asset tags" it is immune to.
        // Applying an effect whose asset tags overlap the mask is blocked. Immunity is intrinsic (base) or
        // conferred by an effect for its lifetime; effective = base OR active grants, via RecomputeImmunity.

        /// <summary>Add intrinsic immunity bits to an actor (effective immediately + persists across recompute).</summary>
        public void SetBaseImmunity(int actor, int immunityMask)
        {
            _store.TryGetInt((ulong)actor, (ulong)_baseImmunityCol, out int b);
            _store.SetInt((ulong)actor, (ulong)_baseImmunityCol, b | immunityMask);
            _store.TryGetInt((ulong)actor, (ulong)_immunityCol, out int e);
            _store.SetInt((ulong)actor, (ulong)_immunityCol, e | immunityMask);
        }

        /// <summary>Remove intrinsic immunity bits (effect-conferred immunity reappears on recompute).</summary>
        public void ClearBaseImmunity(int actor, int immunityMask)
        {
            _store.TryGetInt((ulong)actor, (ulong)_baseImmunityCol, out int b);
            _store.SetInt((ulong)actor, (ulong)_baseImmunityCol, b & ~immunityMask);
            _store.TryGetInt((ulong)actor, (ulong)_immunityCol, out int e);
            _store.SetInt((ulong)actor, (ulong)_immunityCol, e & ~immunityMask);
        }

        /// <summary>The actor's effective immunity mask (intrinsic + effect-conferred, post-<see cref="RecomputeImmunity"/>).</summary>
        public int GetImmunity(int actor)
        {
            _store.TryGetInt((ulong)actor, (ulong)_immunityCol, out int v);
            return v;
        }

        /// <summary>True if any of <paramref name="assetTags"/> are in the actor's effective immunity mask.</summary>
        public bool IsImmuneTo(int actor, int assetTags) => (GetImmunity(actor) & assetTags) != 0;

        /// <summary>
        /// Confer immunity to <paramref name="immunityMask"/> on an actor for <paramref name="durationTicks"/>
        /// ticks via an auto-expiring duration effect (a "ward"). Folded into the effective immunity by the
        /// next <see cref="RecomputeImmunity"/> and dropped when the effect expires.
        /// </summary>
        public int GrantImmunityFor(int actor, int immunityMask, int durationTicks)
            => ApplyDurationCore(actor, 0, ModifierOp.Add, 0f, durationTicks, 0, 0, immunityMask);

        /// <summary>
        /// Derive Immunity = BaseImmunity (one parallel System), then OR in every active effect's conferred
        /// immunity (host-side scatter). Call after applying/expiring effects to refresh ward-style immunity.
        /// </summary>
        public void RecomputeImmunity()
        {
            _lens.Execute(_recomputeImmunity, _table);
            if (_effects.LiveCount == 0) return;
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxSuspended, out int suspended);
                if (suspended != 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxGrantImmunity, out int grant);
                if (grant == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                _store.TryGetInt((ulong)actor, (ulong)_immunityCol, out int cur);
                _store.SetInt((ulong)actor, (ulong)_immunityCol, cur | grant);
            }
        }

        // ── Ongoing requirements: suspend, not remove (§5.5) ─────────────────

        /// <summary>
        /// Re-evaluate each active effect's ONGOING requirement against its actor's CurrentTags and set the
        /// effect's Suspended flag (§5.5): a suspended effect stays in the store and keeps counting toward its
        /// EndTick, but contributes nothing (modifiers, granted tags/abilities/immunity all skip it) until the
        /// requirement passes again. Recommended step order: <see cref="RecomputeTags"/> →
        /// <see cref="RecomputeSuspension"/> → <see cref="RecomputeCurrent"/>/<see cref="RecomputeAbilities"/>/
        /// <see cref="RecomputeImmunity"/>. (Suspension reads CurrentTags as of the last <see cref="RecomputeTags"/>,
        /// so an effect whose ongoing requirement depends on a tag IT itself grants resolves one step lagged.)
        /// </summary>
        public void RecomputeSuspension()
        {
            if (_effects.LiveCount == 0) return;
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxReqMode, out int mode);
                if (mode < 0) continue; // no ongoing requirement -> never suspended
                _effects.TryGetInt((ulong)r, (ulong)FxReqMask, out int mask);
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                bool met = MatchesTrigger(GetCurrentTags(actor), new TagTrigger(mask, (TriggerMode)mode));
                _effects.SetInt((ulong)r, (ulong)FxSuspended, met ? 0 : 1);
            }
        }

        /// <summary>True if the effect is currently suspended by a failing ongoing requirement (post-<see cref="RecomputeSuspension"/>).</summary>
        public bool IsEffectSuspended(int effectHandle)
        {
            _effects.TryGetInt((ulong)effectHandle, (ulong)FxSuspended, out int s);
            return s != 0;
        }

        /// <summary>
        /// Apply an Instant effect UNLESS the actor is immune to <paramref name="assetTags"/> (§5.5). Returns
        /// true if it applied; false if blocked (optionally emitting <paramref name="blockedCue"/>). Reads
        /// immunity as of the last <see cref="RecomputeImmunity"/>.
        /// </summary>
        public bool TryApplyInstant(int actor, int attr, ModifierOp op, float magnitude, int assetTags, int blockedCue = -1)
        {
            if (IsImmuneTo(actor, assetTags))
            {
                if (blockedCue >= 0) EmitCue(blockedCue, actor, 0f);
                return false;
            }
            ApplyInstant(actor, attr, op, magnitude);
            return true;
        }

        /// <summary>
        /// Apply a Duration effect UNLESS the actor is immune to <paramref name="assetTags"/> (§5.5). Returns
        /// the effect handle, or <see cref="InvalidEffect"/> if blocked (optionally emitting a cue).
        /// </summary>
        public int TryApplyDuration(int actor, int attr, ModifierOp op, float magnitude, int durationTicks,
            int assetTags, int blockedCue = -1)
        {
            if (IsImmuneTo(actor, assetTags))
            {
                if (blockedCue >= 0) EmitCue(blockedCue, actor, 0f);
                return InvalidEffect;
            }
            return ApplyDuration(actor, attr, op, magnitude, durationTicks);
        }

        // ── Execution & magnitude calculations (§5.4) ────────────────────────
        // The "escape to host" path: a magnitude or a whole multi-attribute interaction is computed by host
        // code that captures source/target attributes, instead of a constant. (A DataLens IR expression-tree
        // MMC — branchless, batchable — is future work, couples to the A4 read-side expression nodes.)

        /// <summary>Computes an effect magnitude from captured source/target state (a Magnitude Calc, §5.4).
        /// Evaluated at apply time = a snapshot. <paramref name="source"/> == <paramref name="target"/> for a self-effect.</summary>
        public delegate float MagnitudeCalc(HateWorld world, int source, int target);

        /// <summary>Custom multi-attribute logic over a source/target (an Execution Calc, §5.4): reads captured
        /// attributes and writes back through the normal effect API (e.g. armour → mitigation → final damage → Health).</summary>
        public delegate void ExecutionCalc(HateWorld world, int source, int target);

        /// <summary>Apply an Instant whose magnitude is computed by <paramref name="calc"/> (snapshot of source/target at apply).</summary>
        public void ApplyInstantCalc(int source, int target, int attr, ModifierOp op, MagnitudeCalc calc)
        {
            if (calc == null) throw new ArgumentNullException(nameof(calc));
            ApplyInstant(target, attr, op, calc(this, source, target));
        }

        /// <summary>Apply a Duration whose magnitude is computed by <paramref name="calc"/> (snapshot at apply). Returns the effect handle.</summary>
        public int ApplyDurationCalc(int source, int target, int attr, ModifierOp op, MagnitudeCalc calc, int durationTicks)
        {
            if (calc == null) throw new ArgumentNullException(nameof(calc));
            return ApplyDuration(target, attr, op, calc(this, source, target), durationTicks);
        }

        /// <summary>
        /// Run a custom Execution calc (§5.4): host code that reads captured source/target attributes and
        /// applies the resulting changes via the normal effect API (Instants/Durations/cues). The canonical
        /// example is a damage exec — armour → mitigation → final damage → Health — that the constant-modifier
        /// channels can't express. Runs synchronously for the one interaction (execs touch the few actors a
        /// gameplay event hits, not the whole population every frame).
        /// </summary>
        public void RunExecution(int source, int target, ExecutionCalc exec)
        {
            if (exec == null) throw new ArgumentNullException(nameof(exec));
            exec(this, source, target);
        }

        // ── Abilities (§7): activation pipeline (cost + cooldown + requirement) ──

        /// <summary>
        /// Id-based <see cref="CanActivate(int,in AbilityDef)"/>: also requires the actor to have been
        /// GRANTED the ability (§7). Use this once abilities are registered + granted; the AbilityDef
        /// overload is the ungated primitive.
        /// </summary>
        public bool CanActivate(int actor, int abilityId)
            => HasAbility(actor, abilityId) && CanActivate(actor, GetAbility(abilityId));

        /// <summary>Id-based <see cref="TryActivate(int,in AbilityDef)"/>: no-op unless the ability is granted.</summary>
        public bool TryActivate(int actor, int abilityId)
            => HasAbility(actor, abilityId) && TryActivate(actor, GetAbility(abilityId));

        /// <summary>
        /// CanActivate (§7): true if the ability's cost is affordable, its cooldown is ready (the actor
        /// lacks the cooldown tag), and its activation requirement (if any) passes against the actor's
        /// CurrentTags. Reads tags as of the last <see cref="RecomputeTags"/>. This overload does NOT check
        /// granting — use <see cref="CanActivate(int,int)"/> to gate on the grant.
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
        /// Grant-gated batch eligibility (§7): the id-based <see cref="EvaluateEligibility(in AbilityDef)"/>
        /// that ALSO knocks out, in one branchless pass, every actor that has not been granted the ability.
        /// </summary>
        public void EvaluateEligibility(int abilityId)
        {
            EvaluateEligibility(GetAbility(abilityId));
            // Eligible = 0 where the actor lacks the grant bit.
            _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                (ulong)_grantedAbilitiesCol, CompareOp.LacksBits, AbilityBit(abilityId));
        }

        /// <summary>
        /// Compute, for EVERY live actor, whether it could activate the ability right now — into the
        /// per-actor Eligible column (1/0), as a few branchless column passes (the batch form of
        /// <see cref="CanActivate"/> for mass AI). Eligible starts true and is knocked out by: cost
        /// unaffordable (Base &lt; cost, a float-predicate pass), on cooldown (HasAny cooldown tag), and
        /// the activation requirement failing. Reads tags as of the last <see cref="RecomputeTags"/>.
        /// (Batch requirement supports RequireAny/Exclude; multi-bit RequireAll is not expressible as a
        /// single knock-out — use single-bit masks or per-actor CanActivate.) This overload does NOT gate
        /// on granting — use <see cref="EvaluateEligibility(int)"/> for that.
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
            return CountEligibleColumn();
        }

        /// <summary>Grant-gated <see cref="CountEligible(in AbilityDef)"/>: how many live actors are both
        /// granted the ability and pass its limiters.</summary>
        public int CountEligible(int abilityId)
        {
            EvaluateEligibility(abilityId);
            return CountEligibleColumn();
        }

        private int CountEligibleColumn()
        {
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
        /// Bulk-copy the per-actor Utility column (as of the last <see cref="EvaluateUtility"/>) into
        /// <paramref name="dest"/> in one parallel gather — the read-back for visualisation/inspection over
        /// a whole population, instead of a per-actor <see cref="GetUtility"/> interop loop. View row i maps
        /// to the i-th live actor; recover its handle with <see cref="DecisionSourceActor"/>. Returns the
        /// number of scores written (the live actor count).
        /// </summary>
        public int CopyUtilities(float[] dest)
        {
            _lens.RefreshView(_utilityView, _store);
            return _utilityView.CopyFloats(dest);
        }

        /// <summary>
        /// Bulk-copy the per-actor Eligible column (1/0, as of the last <see cref="EvaluateEligibility"/> or
        /// <see cref="EvaluateUtility"/>) into <paramref name="dest"/> in one parallel gather. View row i maps
        /// to the i-th live actor (same ordering as <see cref="CopyUtilities"/>). Returns the count written.
        /// </summary>
        public int CopyEligibility(int[] dest)
        {
            _lens.RefreshView(_eligibleView, _store);
            return _eligibleView.CopyInts(dest);
        }

        /// <summary>Map a row in the last <see cref="CopyUtilities"/>/<see cref="CopyEligibility"/> read-back back to its actor handle.</summary>
        public int DecisionSourceActor(int viewRow) => (int)_utilityView.SourceRow((ulong)viewRow);

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

        // ── Utility AI v2: score → perturb → select (§8, D5.2) ───────────────
        // The decision is three separated, data-driven stages, each branchless column passes over ALL
        // actors. Considerations live in the program (uniform curve passes), never in per-row data, so the
        // hot path never does a per-row lookup or branch. The result is a plain Choice column others read.

        /// <summary>Number of ability score slots this world was created with (0 = utility-AI v2 disabled).</summary>
        public int AbilityScoreSlots => _abilityScoreSlots;

        private void RequireAI()
        {
            if (_abilityScoreSlots <= 0)
                throw new InvalidOperationException(
                    "This HateWorld has no ability score slots; construct it with abilityScoreSlots > 0 to use the utility-AI pipeline.");
        }

        private int ScoreCol(int slot)
        {
            if (slot < 0 || slot >= _abilityScoreSlots)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return _scoreCol0 + slot;
        }

        // ── Variance (the skill / fatigue dial, §8.4) ────────────────────────

        /// <summary>Set one actor's Variance (0 = perfect play; higher = sloppier selection).</summary>
        public void SetVariance(int actor, float variance)
            => _store.SetFloat((ulong)actor, (ulong)RequireVarianceCol(), variance);

        /// <summary>Set the Variance of every live actor in one parallel pass.</summary>
        public ulong SetVarianceAll(float variance)
        {
            RequireAI();
            return _lens.RunFloat(_store, (ulong)_varianceCol, SystemOp.Set, variance);
        }

        /// <summary>An actor's current Variance.</summary>
        public float GetVariance(int actor)
        {
            _store.TryGetFloat((ulong)actor, (ulong)RequireVarianceCol(), out float v);
            return v;
        }

        private int RequireVarianceCol() { RequireAI(); return _varianceCol; }

        // ── Command override (hard "force this ability", §8.5) ───────────────

        /// <summary>Force <paramref name="actor"/> to choose ability slot <paramref name="abilitySlot"/>,
        /// overriding scoring at the next <see cref="Select"/>. Persists until cleared.</summary>
        public void SetCommand(int actor, int abilitySlot)
        {
            RequireAI();
            _store.SetInt((ulong)actor, (ulong)_commandCol, abilitySlot);
        }

        /// <summary>Clear one actor's command (return it to scored selection).</summary>
        public void ClearCommand(int actor)
        {
            RequireAI();
            _store.SetInt((ulong)actor, (ulong)_commandCol, -1);
        }

        /// <summary>Clear every live actor's command in one parallel pass.</summary>
        public ulong ClearCommandAll()
        {
            RequireAI();
            return _lens.RunInt(_store, (ulong)_commandCol, SystemOp.Set, -1);
        }

        // ── Score (multi-consideration, §8.2) ────────────────────────────────

        /// <summary>
        /// Compute the per-actor Utility for one ability into its score slot (§8.2): apply each
        /// <see cref="Consideration"/> as a uniform DataLens curve pass over the metric attribute's Current,
        /// aggregated per <paramref name="aggregate"/> (Product = ∏, WeightedSum = Σ weight·curve), then zero
        /// the score for actors that fail the ability's hard <see cref="Limiters">limiters</see>
        /// (cost/cooldown/requirement, via <see cref="EvaluateEligibility"/>). All branchless column passes;
        /// call <see cref="RecomputeCurrent"/> first so the metric Currents are up to date.
        /// </summary>
        public void ScoreAbility(int slot, in AbilityDef ability, Consideration[] considerations, Aggregate aggregate)
        {
            RequireAI();
            int score = ScoreCol(slot);

            if (aggregate == Aggregate.Product)
            {
                _lens.RunFloat(_store, (ulong)score, SystemOp.Set, 1f); // product identity
                if (considerations != null)
                    foreach (var c in considerations)
                        _lens.RunFloatCurved(_store, (ulong)score, SystemOp.Mul, (ulong)CurrentCol(c.MetricAttr), c.Curve);
            }
            else // WeightedSum
            {
                _lens.RunFloat(_store, (ulong)score, SystemOp.Set, 0f); // sum identity
                if (considerations != null)
                    foreach (var c in considerations)
                    {
                        // scratch = curve(metric); scratch *= weight; score += scratch.
                        _lens.RunFloatCurved(_store, (ulong)_scratchCol, SystemOp.Set, (ulong)CurrentCol(c.MetricAttr), c.Curve);
                        if (c.Weight != 1f) _lens.RunFloat(_store, (ulong)_scratchCol, SystemOp.Mul, c.Weight);
                        _lens.RunFloatColumn(_store, (ulong)score, SystemOp.Add, (ulong)_scratchCol);
                    }
            }

            // Hard eligibility: ineligible actors score 0 (the limiter gate, §8.1).
            EvaluateEligibility(ability);
            _lens.RunFloatWhereInt(_store, (ulong)score, SystemOp.Set, 0f, (ulong)_eligibleCol, CompareOp.Equal, 0);
        }

        // ── Perturb (the determinism ↔ variance dial, §8.4) ──────────────────

        /// <summary>
        /// Jitter every ability's score by <c>Variance[actor] × noise</c> (§8.4) so selection ranges from
        /// perfect (Variance 0) to sloppy. Each slot draws an independent, reproducible counter-based stream
        /// (seed + slot, keyed on row+tick), so the result is deterministic across runs/machines/replay.
        /// Noise defaults to symmetric [-1,1) (no upward bias). Call after scoring all abilities, before
        /// <see cref="Select"/>.
        /// </summary>
        public void PerturbScores(ulong seed, ulong tick, float noiseLo = -1f, float noiseHi = 1f)
        {
            RequireAI();
            for (int s = 0; s < _abilityScoreSlots; s++)
                _lens.RunFloatNoisePerturb(_store, (ulong)ScoreCol(s), SystemOp.Add, (ulong)_varianceCol,
                    noiseLo, noiseHi, seed + (ulong)s, tick);
        }

        // ── Select (policy as data: greedy/noisy argmax + Command, §8.5) ─────

        /// <summary>
        /// Reduce the K ability score columns to a per-actor Choice (the §8.5 pick): greedy argmax across the
        /// scores (ties to the lowest slot), with a winning score below <paramref name="minScore"/> giving
        /// Choice -1 ("do nothing"). A per-actor Command (set via <see cref="SetCommand"/>) overrides the pick
        /// (<c>Choice = Command>=0 ? Command : argmax</c>). One argmax pass + one override pass. Returns the
        /// number of live actors decided.
        /// </summary>
        public ulong Select(float minScore = 0f)
        {
            RequireAI();
            ulong n = _lens.RunFloatArgmax(_store, (ulong)_choiceCol, _scoreColsUlong, minScore, -1);
            // Command override: where Command >= 0, Choice = Command.
            _lens.RunIntColumn(_store, (ulong)_choiceCol, SystemOp.Set, (ulong)_commandCol,
                (ulong)_commandCol, CompareOp.GreaterEqual, 0);
            return n;
        }

        /// <summary>An actor's last-computed score for an ability slot (after <see cref="ScoreAbility"/>/<see cref="PerturbScores"/>).</summary>
        public float GetScore(int actor, int slot)
        {
            _store.TryGetFloat((ulong)actor, (ulong)ScoreCol(slot), out float v);
            return v;
        }

        /// <summary>An actor's chosen ability slot after <see cref="Select"/> (-1 = none).</summary>
        public int GetChoice(int actor)
        {
            RequireAI();
            _store.TryGetInt((ulong)actor, (ulong)_choiceCol, out int v);
            return v;
        }

        /// <summary>
        /// Bulk-copy the per-actor Choice column (after <see cref="Select"/>) into <paramref name="dest"/> in
        /// one parallel gather — the population-scale read-back. View row i maps to the i-th live actor; recover
        /// its handle with <see cref="ChoiceSourceActor"/>. Returns the number of choices written.
        /// </summary>
        public int CopyChoices(int[] dest)
        {
            RequireAI();
            _lens.RefreshView(_choiceView, _store);
            return _choiceView.CopyInts(dest);
        }

        /// <summary>Map a row in the last <see cref="CopyChoices"/> read-back back to its actor handle.</summary>
        public int ChoiceSourceActor(int viewRow) => (int)_choiceView.SourceRow((ulong)viewRow);

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

        // ── Inspection / tooling (read-only) ─────────────────────────────────
        // Read-only accessors the HATE Toolkit (and save/debug systems) use to render a world's state.

        /// <summary>Attribute name for an index (the order passed to the constructor).</summary>
        public string AttributeName(int attr) => _attrNames[attr];

        /// <summary>All attribute names, in index order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> AttributeNames => _attrNames;

        /// <summary>Tag name for a bit index (0..31), or null if that bit is unnamed.</summary>
        public string TagName(int bitIndex) => (uint)bitIndex < (uint)_tagNames.Length ? _tagNames[bitIndex] : null;

        /// <summary>All tag names, in bit-index order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> TagNames => _tagNames;

        /// <summary>A read-only view of one active duration-effect row, for inspection/tooling.</summary>
        public readonly struct EffectSnapshot
        {
            public readonly int Handle;        // effects-store row index
            public readonly int Actor;
            public readonly int Attr;
            public readonly ModifierOp Op;
            public readonly float Magnitude;
            public readonly int EndTick;
            public readonly int StackCount;
            public readonly bool Suspended;
            public readonly int GrantTags;
            public readonly int GrantAbilities;
            public readonly int GrantImmunity;
            public readonly int EffectId;      // -1 = non-stacking

            public EffectSnapshot(int handle, int actor, int attr, ModifierOp op, float magnitude, int endTick,
                int stackCount, bool suspended, int grantTags, int grantAbilities, int grantImmunity, int effectId)
            {
                Handle = handle; Actor = actor; Attr = attr; Op = op; Magnitude = magnitude; EndTick = endTick;
                StackCount = stackCount; Suspended = suspended; GrantTags = grantTags; GrantAbilities = grantAbilities;
                GrantImmunity = grantImmunity; EffectId = effectId;
            }
        }

        /// <summary>
        /// Fill <paramref name="results"/> (cleared first) with a snapshot of every active duration effect,
        /// optionally only those targeting <paramref name="actorFilter"/> (-1 = all). For tooling/debugging.
        /// </summary>
        public void GetActiveEffects(System.Collections.Generic.List<EffectSnapshot> results, int actorFilter = -1)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            results.Clear();
            if (_effects.LiveCount == 0) return;
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                if (actorFilter >= 0 && actor != actorFilter) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxAttr, out int attr);
                _effects.TryGetFloat((ulong)r, (ulong)FxMagnitude, out float mag);
                _effects.TryGetInt((ulong)r, (ulong)FxEndTick, out int endTick);
                _effects.TryGetInt((ulong)r, (ulong)FxOp, out int op);
                _effects.TryGetInt((ulong)r, (ulong)FxStackCount, out int stacks);
                _effects.TryGetInt((ulong)r, (ulong)FxSuspended, out int suspended);
                _effects.TryGetInt((ulong)r, (ulong)FxGrantTags, out int grantTags);
                _effects.TryGetInt((ulong)r, (ulong)FxGrantAbilities, out int grantAbilities);
                _effects.TryGetInt((ulong)r, (ulong)FxGrantImmunity, out int grantImmunity);
                _effects.TryGetInt((ulong)r, (ulong)FxEffectId, out int effectId);
                results.Add(new EffectSnapshot(r, actor, attr, (ModifierOp)op, mag, endTick, stacks,
                    suspended != 0, grantTags, grantAbilities, grantImmunity, effectId));
            }
        }

        public void Dispose()
        {
            _activeView?.Dispose();
            _selectedView?.Dispose();
            _eligibleView?.Dispose();
            _utilityView?.Dispose();
            _choiceView?.Dispose();
            _recomputeCurrent?.Dispose();
            _recomputeTags?.Dispose();
            _recomputeAbilities?.Dispose();
            _recomputeImmunity?.Dispose();
            _lens?.Dispose();
            _effects?.Dispose();
            _tasks?.Dispose();
            _store?.Dispose();
        }
    }
}
