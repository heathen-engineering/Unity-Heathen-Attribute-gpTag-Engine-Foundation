using System;
using System.Collections.Generic;
using Heathen.DataLens;
using Heathen.GameplayTags;

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

        // ── Attributes (§4.1). Each attribute is a GameplayTag identity + a HateAttribute descriptor
        //    (value type + declared [Min,Max] envelope), stored as FOUR DataLens columns per actor —
        //    Base, Current, Min, Max — at the attribute's narrowest storage width. Integral attributes are
        //    OFFSET-ENCODED (stored = real - declaredMin) so the column is sized by the span; float/double
        //    store the real value. `_attrCol0[a]` is the index of attribute a's Base column (Current = +1,
        //    Min = +2, Max = +3). The per-attribute storage type drives the typed System ops / get-set. ──
        private readonly HateAttribute[] _attributes;          // by attribute index
        private readonly Dictionary<ulong, int> _attrByTag;    // GameplayTag.Id -> attribute index
        private readonly int[] _attrCol0;                      // attribute index -> its Base column index
        private readonly IrProgram _recomputeCaps;             // Min = MinBase, Max = MaxBase, per attribute
        private readonly IrProgram _recomputeCurrent;          // Current = Base (then clamp), per attribute

        // ── Active duration effects (HATE-Spec §5.2 — buff density: a row per active effect, no heap). ──
        // Columns: Actor, Attr (which attribute), Magnitude (additive), EndTick, Active (1=live).
        private readonly DataStore _effects;
        private readonly int _effectsCapacity;
        private const int FxActor = 0, FxAttr = 1, FxMagnitude = 2, FxEndTick = 3, FxActive = 4, FxGrantTags = 5, FxGrantAbilities = 6, FxOp = 7, FxEffectId = 8, FxStackCount = 9, FxGrantImmunity = 10, FxReqAll = 11, FxReqMode = 12, FxSuspended = 13, FxField = 14, FxReqNone = 15;
        private const int EffectsStoreIndex = 1;

        private DataView _activeView;     // over the effects Active column, for reclaim read-back
        private int[] _activeScratch;     // managed buffer for the bulk read

        private int _currentTick;

        // ── Tags (§5.5 granted tags / §6 Trigger gating). Up to 32 "hot" tags packed as bits in an
        //    Int32 mask column, so a Trigger is a branchless DataLens bitmask predicate over all actors. ──
        private readonly Dictionary<ulong, int> _statusByTag;     // status GameplayTag.Id -> bit index (0..31)
        private readonly GameplayTag[] _statusTags;               // bit index -> status tag (inspection/tooling)
        private readonly int _baseTagsCol, _currentTagsCol, _selectedCol; // Int32 columns on the actor store
        private readonly IrProgram _recomputeTags;                // CurrentTags = BaseTags
        private DataView _selectedView;                           // over the actor Selected column
        private int[] _selectedScratch;

        // ── Mass AI (§7 batch CanActivate / §8 utility): per-actor Eligible (int 1/0) + Utility (float)
        //    columns, computed across ALL actors as branchless column passes, then scanned. ──
        private readonly int _eligibleCol;   // Int32
        private readonly int _utilityCol;    // Float
        // Float scratch holding a non-float (integral/double) metric's REAL Current, materialised for the
        // float-only utility/curve passes (§8). De-offsets integral attributes back to real space.
        private readonly int _metricScratchCol; // Float
        private readonly int _capacity;         // actor row capacity (for live-row managed scans)
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
        private readonly Dictionary<ulong, int> _abilityByTag = new Dictionary<ulong, int>(); // ability GameplayTag.Id -> ability id (catalogue index)

        // ── Immunity (§5.5). Per-actor immunity mask (over the same hot-tag bits): an incoming effect's
        //    asset tags matching it are blocked. Base (intrinsic) + effective (base OR active-effect grants),
        //    like tags/abilities; effects confer immunity for their lifetime via FxGrantImmunity. ──
        private readonly int _baseImmunityCol;     // Int32 bitmask: intrinsic immunity
        private readonly int _immunityCol;         // Int32 bitmask: effective = base OR active-effect grants
        private readonly IrProgram _recomputeImmunity; // Immunity = BaseImmunity

        /// <summary>Sentinel returned by <see cref="SpawnActor"/> when the world is at capacity (the
        /// DataLens "no free row" value).</summary>
        public const ulong InvalidActor = ulong.MaxValue;

        /// <summary>Create a world with a typed attribute set, no status tags, and a maximum actor capacity.</summary>
        public HateWorld(HateAttribute[] attributes, int capacity)
            : this(attributes, System.Array.Empty<GameplayTag>(), 0, capacity) { }

        /// <summary>
        /// Create a world with a typed attribute set, a fixed status-tag set (up to 32 "hot" tags), and a
        /// maximum actor capacity. Each attribute gets four columns (Base, Current, Min, Max) at its
        /// narrowest storage width (§4.1); status tags pack into per-actor Int32 bitmask columns
        /// (BaseTags/CurrentTags) plus a Selected scratch column for Trigger evaluation.
        /// </summary>
        public HateWorld(HateAttribute[] attributes, GameplayTag[] statusTags, int capacity)
            : this(attributes, statusTags, 0, capacity) { }

        /// <summary>
        /// As the status-aware constructor, plus <paramref name="abilityScoreSlots"/> dedicated ability-score
        /// columns for the §8 utility-AI pipeline (D5.2): score (multi-consideration) → perturb (Variance ×
        /// counter-based noise) → select (argmax + Command override). Pass 0 to omit the utility-AI columns.
        /// </summary>
        public HateWorld(HateAttribute[] attributes, GameplayTag[] statusTags, int abilityScoreSlots, int capacity)
        {
            if (attributes == null) throw new ArgumentNullException(nameof(attributes));
            if (attributes.Length == 0) throw new ArgumentException("Need at least one attribute.", nameof(attributes));
            if (statusTags == null) throw new ArgumentNullException(nameof(statusTags));
            if (statusTags.Length > 32) throw new ArgumentException("At most 32 hot status tags (Int32 bitmask).", nameof(statusTags));
            if (capacity <= 0) throw new ArgumentException("Capacity must be positive.", nameof(capacity));
            if (abilityScoreSlots < 0 || abilityScoreSlots > 64)
                throw new ArgumentException("abilityScoreSlots must be in 0..64.", nameof(abilityScoreSlots));

            _attributes = (HateAttribute[])attributes.Clone();
            _statusTags = (GameplayTag[])statusTags.Clone();
            _attrByTag = new Dictionary<ulong, int>(attributes.Length);
            _attrCol0 = new int[attributes.Length];
            _statusByTag = new Dictionary<ulong, int>(statusTags.Length);
            for (int t = 0; t < statusTags.Length; t++)
                _statusByTag[statusTags[t].Id] = t;

            // Actor columns: each attribute occupies SIX columns [Base, Current, Min, Max, MinBase, MaxBase]
            // at its storage width, laid out contiguously (a's Base at _attrCol0[a]). Min/Max are the EFFECTIVE
            // caps (recomputed each step from MinBase/MaxBase + active cap-buff effects, §5.3); MinBase/MaxBase
            // are the persistent caps that SetMin/SetMax write. Then BaseTags, CurrentTags, Selected,
            // Eligible (Int32), Utility (Float), BaseAbilities + GrantedAbilities (Int32, intrinsic vs
            // effective grants), BaseImmunity + Immunity (Int32), MetricScratch (Float, materialises a non-float
            // metric's real Current for the float passes); then (when abilityScoreSlots>0) K Score columns
            // (Float), Variance (Float), Command (Int32), Choice (Int32), WeightedSum scratch (Float).
            _abilityScoreSlots = abilityScoreSlots;
            int attrCols = attributes.Length * 6;
            for (int a = 0; a < attributes.Length; a++) _attrCol0[a] = a * 6;
            _baseTagsCol = attrCols;
            _currentTagsCol = attrCols + 1;
            _selectedCol = attrCols + 2;
            _eligibleCol = attrCols + 3;
            _utilityCol = attrCols + 4;
            _baseAbilitiesCol = attrCols + 5;
            _grantedAbilitiesCol = attrCols + 6;
            _baseImmunityCol = attrCols + 7;
            _immunityCol = attrCols + 8;
            _metricScratchCol = attrCols + 9;
            _capacity = capacity;

            int colCount = attrCols + 10;
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
            for (int a = 0; a < attributes.Length; a++)
            {
                var attr = _attributes[a];
                _attrByTag[attr.Tag.Id] = a;
                string name = attr.Tag.Name ?? attr.Tag.Id.ToString("X16");
                var st = attr.StorageType;
                colNames[BaseCol(a)] = name + ".Base";        colTypes[BaseCol(a)] = st;
                colNames[CurrentCol(a)] = name + ".Current";  colTypes[CurrentCol(a)] = st;
                colNames[MinCol(a)] = name + ".Min";          colTypes[MinCol(a)] = st;
                colNames[MaxCol(a)] = name + ".Max";          colTypes[MaxCol(a)] = st;
                colNames[MinBaseCol(a)] = name + ".MinBase";  colTypes[MinBaseCol(a)] = st;
                colNames[MaxBaseCol(a)] = name + ".MaxBase";  colTypes[MaxBaseCol(a)] = st;
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
            colNames[_metricScratchCol] = "MetricScratch";       colTypes[_metricScratchCol] = DataLensValueType.Float;
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
                new[] { "Actor", "Attr", "Magnitude", "EndTick", "Active", "GrantTags", "GrantAbilities", "Op", "EffectId", "StackCount", "GrantImmunity", "ReqAll", "ReqMode", "Suspended", "Field", "ReqNone" },
                new[] { DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Float,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32, DataLensValueType.Int32, DataLensValueType.Int32,
                        DataLensValueType.Int32 },
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
                        DataLensValueType.Int32, DataLensValueType.Float, DataLensValueType.Double },
                (ulong)_tasksCapacity);

            // Reusable: reset the EFFECTIVE caps to their persistent base (Min = MinBase, Max = MaxBase) for
            // every attribute, before active cap-buff effects (§5.3) fold on top host-side. One parallel pass.
            _recomputeCaps = new IrProgram();
            for (int a = 0; a < _attributes.Length; a++)
            {
                var st = _attributes[a].StorageType;
                _recomputeCaps.Add(IrOp.TypedColumn(0, st, MinCol(a), SystemOp.Set, MinBaseCol(a)));
                _recomputeCaps.Add(IrOp.TypedColumn(0, st, MaxCol(a), SystemOp.Set, MaxBaseCol(a)));
            }

            // Reusable: Current = clamp(Base, Min, Max) for every attribute, in the attribute's storage type.
            // The three ops per attribute touch the same Current column so they run ordered (Set, then clamp
            // to Max, then to Min); clamp is monotonic so it is correct in offset space too. Min/Max are the
            // EFFECTIVE caps (already folded by RecomputeCurrent before this runs). Duration Current-channel
            // aggregation is layered on host-side in RecomputeCurrent (Step 3).
            _recomputeCurrent = new IrProgram();
            for (int a = 0; a < _attributes.Length; a++)
            {
                var st = _attributes[a].StorageType;
                _recomputeCurrent.Add(IrOp.TypedColumn(0, st, CurrentCol(a), SystemOp.Set, BaseCol(a)));
                _recomputeCurrent.Add(IrOp.TypedColumn(0, st, CurrentCol(a), SystemOp.Min, MaxCol(a)));
                _recomputeCurrent.Add(IrOp.TypedColumn(0, st, CurrentCol(a), SystemOp.Max, MinCol(a)));
            }

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

        public int AttributeCount => _attributes.Length;
        public int LiveActorCount => (int)_store.LiveCount;

        /// <summary>Resolve an attribute's tag to its index (throws if unregistered).</summary>
        public int AttributeIndex(GameplayTag attribute) => _attrByTag[attribute.Id];
        /// <summary>True if the attribute tag is registered in this world.</summary>
        public bool HasAttribute(GameplayTag attribute) => _attrByTag.ContainsKey(attribute.Id);
        /// <summary>The descriptor (type + declared [Min,Max] envelope) for a registered attribute.</summary>
        public HateAttribute AttributeDef(GameplayTag attribute) => _attributes[_attrByTag[attribute.Id]];

        // Per-attribute column layout: Base, Current, Min, Max, MinBase, MaxBase contiguously at _attrCol0[a].
        private int BaseCol(int attr) => _attrCol0[attr];
        private int CurrentCol(int attr) => _attrCol0[attr] + 1;
        private int MinCol(int attr) => _attrCol0[attr] + 2;     // effective min (recomputed from MinBase + cap effects)
        private int MaxCol(int attr) => _attrCol0[attr] + 3;     // effective max
        private int MinBaseCol(int attr) => _attrCol0[attr] + 4; // persistent min cap (SetMin)
        private int MaxBaseCol(int attr) => _attrCol0[attr] + 5; // persistent max cap (SetMax)

        // The float operand column for a utility metric attribute, resolved from its tag. Curve/weight passes
        // are float-only: a SinglePrecision attribute feeds its Current column directly (branchless); an
        // integral/double attribute is materialised (de-offset to real space) into the shared MetricScratch
        // column via a managed live-row pass, and that scratch is returned. (Materialise-then-consume is safe
        // for back-to-back considerations because each curve pass reads the scratch before the next overwrites it.)
        private int MetricCol(GameplayTag metric)
        {
            int a = _attrByTag[metric.Id];
            if (_attributes[a].StorageType == DataLensValueType.Float)
                return CurrentCol(a);
            MaterialiseRealCurrent(a, _metricScratchCol);
            return _metricScratchCol;
        }

        // Write each live actor's REAL Current of attribute `attr` (de-offset for integral/double types) into
        // the Float column `destFloatCol`, so a non-float metric can drive the float-only utility/curve passes.
        // A managed per-row pass (the typed branchless predicate primitive is Int32/Float-only at the C ABI);
        // float metrics never reach here, so the mass-AI hot path stays fully branchless.
        private void MaterialiseRealCurrent(int attr, int destFloatCol)
        {
            int cur = CurrentCol(attr);
            for (ulong a = 0; a < (ulong)_capacity; a++)
            {
                if (!_store.IsValid(a)) continue;
                _store.SetFloat(a, (ulong)destFloatCol, (float)ReadAttrCell(a, attr, cur));
            }
        }

        // Knock the Eligible flag to 0 for every live actor that cannot afford `cost` real units of attribute
        // `attr` (Base < cost). A managed live-row pass for non-float cost attributes (the branchless
        // RunIntWhereFloat fast path covers SinglePrecision); reads through ReadAttrCell so offset/width are
        // handled for any integral or double cost attribute.
        private void KnockOutUnaffordable(int attr, double cost)
        {
            int baseCol = BaseCol(attr);
            for (ulong a = 0; a < (ulong)_capacity; a++)
            {
                if (!_store.IsValid(a)) continue;
                if (ReadAttrCell(a, attr, baseCol) < cost)
                    _store.SetInt(a, (ulong)_eligibleCol, 0);
            }
        }

        // Encode a real value into attribute a's cell at `col`: clamp to the declared envelope (storage
        // safety), then store offset (real - declaredMin) for integral types, or the real value for float/
        // double. Integral offsets are written via SetInt (stride-aware, writes the column's byte width).
        private void WriteAttrCell(ulong actor, int attr, int col, double real)
        {
            var d = _attributes[attr];
            real = d.Clamp(real);
            switch (d.StorageType)
            {
                case DataLensValueType.Float:  _store.SetFloat(actor, (ulong)col, (float)real); break;
                case DataLensValueType.Double: _store.SetDouble(actor, (ulong)col, real); break;
                default:                       _store.SetInt(actor, (ulong)col, (int)Math.Round(real - d.Min)); break;
            }
        }

        // Decode attribute a's cell at `col` back to a real value (de-offset for integral types).
        private double ReadAttrCell(ulong actor, int attr, int col)
        {
            var d = _attributes[attr];
            switch (d.StorageType)
            {
                case DataLensValueType.Float:  _store.TryGetFloat(actor, (ulong)col, out float f); return f;
                case DataLensValueType.Double: _store.TryGetDouble(actor, (ulong)col, out double db); return db;
                default:                       _store.TryGetInt(actor, (ulong)col, out int off); return d.Min + off;
            }
        }

        // ── Actors ───────────────────────────────────────────────────────────

        /// <summary>Spawn an actor (allocate a row). Returns its handle, or <see cref="InvalidActor"/> when full.</summary>
        public ulong SpawnActor()
        {
            ulong row = _store.AllocRow();
            if (row == DataStore.InvalidRow) return InvalidActor;
            // Reused slots must not inherit a previous occupant's grants (intrinsic or effective).
            _store.SetInt(row, (ulong)_baseAbilitiesCol, 0);
            _store.SetInt(row, (ulong)_grantedAbilitiesCol, 0);
            _store.SetInt(row, (ulong)_baseImmunityCol, 0);
            _store.SetInt(row, (ulong)_immunityCol, 0);
            // Seed each attribute's dynamic Min/Max to its declared envelope; Base/Current to the declared min.
            for (int a = 0; a < _attributes.Length; a++)
            {
                var d = _attributes[a];
                WriteAttrCell(row, a, MinBaseCol(a), d.Min);
                WriteAttrCell(row, a, MaxBaseCol(a), d.Max);
                WriteAttrCell(row, a, MinCol(a), d.Min);
                WriteAttrCell(row, a, MaxCol(a), d.Max);
                WriteAttrCell(row, a, BaseCol(a), d.Min);
                WriteAttrCell(row, a, CurrentCol(a), d.Min);
            }
            if (_abilityScoreSlots > 0)
            {
                // …or its command/variance/choice.
                _store.SetInt(row, (ulong)_commandCol, -1);
                _store.SetInt(row, (ulong)_choiceCol, -1);
                _store.SetFloat(row, (ulong)_varianceCol, 0f);
            }
            return row;
        }

        /// <summary>Despawn an actor so its slot can be reused.</summary>
        public void DespawnActor(ulong actor) => _store.FreeRow(actor);

        public bool IsAlive(ulong actor) => _store.IsValid(actor);

        // ── Attribute access (real values; offset/typed encoding is internal) ──────────────

        /// <summary>Set an attribute's permanent Base value (clamped to its declared envelope).</summary>
        public void SetBase(ulong actor, GameplayTag attribute, double value)
        {
            int a = _attrByTag[attribute.Id];
            WriteAttrCell(actor, a, BaseCol(a), value);
        }

        public double GetBase(ulong actor, GameplayTag attribute)
        {
            int a = _attrByTag[attribute.Id];
            return ReadAttrCell(actor, a, BaseCol(a));
        }

        /// <summary>The Current value (Base after modifiers, clamped to [Min,Max]) as of the last <see cref="RecomputeCurrent"/>.</summary>
        public double GetCurrent(ulong actor, GameplayTag attribute)
        {
            int a = _attrByTag[attribute.Id];
            return ReadAttrCell(actor, a, CurrentCol(a));
        }

        /// <summary>The actor's dynamic (effective) minimum cap for an attribute, as of the last
        /// <see cref="RecomputeCurrent"/> (cap-buff effects fold into it).</summary>
        public double GetMin(ulong actor, GameplayTag attribute)
        {
            int a = _attrByTag[attribute.Id];
            return ReadAttrCell(actor, a, MinCol(a));
        }

        /// <summary>The actor's dynamic (effective) maximum cap (e.g. Max Health), as of the last
        /// <see cref="RecomputeCurrent"/> (cap-buff effects fold into it).</summary>
        public double GetMax(ulong actor, GameplayTag attribute)
        {
            int a = _attrByTag[attribute.Id];
            return ReadAttrCell(actor, a, MaxCol(a));
        }

        /// <summary>Set the actor's persistent minimum cap (clamped to the declared envelope). Writes both the
        /// base and the effective column so it reads back immediately; cap-buff effects re-fold over the base in
        /// <see cref="RecomputeCurrent"/>.</summary>
        public void SetMin(ulong actor, GameplayTag attribute, double value)
        {
            int a = _attrByTag[attribute.Id];
            WriteAttrCell(actor, a, MinBaseCol(a), value);
            WriteAttrCell(actor, a, MinCol(a), value);
        }

        /// <summary>Set the actor's persistent maximum cap (clamped to the declared envelope). Writes both the
        /// base and the effective column so it reads back immediately; cap-buff effects re-fold over the base in
        /// <see cref="RecomputeCurrent"/>.</summary>
        public void SetMax(ulong actor, GameplayTag attribute, double value)
        {
            int a = _attrByTag[attribute.Id];
            WriteAttrCell(actor, a, MaxBaseCol(a), value);
            WriteAttrCell(actor, a, MaxCol(a), value);
        }

        // ── Effects (Instant, §5.1) ──────────────────────────────────────────

        /// <summary>Apply an Instant effect to one actor's attribute Base (permanent change). Computed in
        /// real space; <see cref="WriteAttrCell"/> clamps to the declared envelope and encodes for storage.</summary>
        public void ApplyInstant(ulong actor, GameplayTag attribute, ModifierOp op, double magnitude)
        {
            int a = _attrByTag[attribute.Id];
            double b = ReadAttrCell(actor, a, BaseCol(a));
            WriteAttrCell(actor, a, BaseCol(a), Combine(b, op, magnitude));
        }

        /// <summary>
        /// Apply an Instant effect to the given attribute Base of EVERY live actor in one parallel
        /// DataLens System pass (offset/bias-correct per the column's storage type). Returns actors affected.
        /// </summary>
        public ulong ApplyInstantAll(GameplayTag attribute, ModifierOp op, double magnitude)
        {
            int a = _attrByTag[attribute.Id];
            using var program = new IrProgram();
            AddBulkModify(program, a, BaseCol(a), op, magnitude);
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
        public int ApplyDuration(ulong actor, GameplayTag attribute, double magnitude, int durationTicks)
            => ApplyDurationCore(actor, _attrByTag[attribute.Id], ModifierOp.Add, magnitude, durationTicks, 0, 0);

        /// <summary>
        /// As <see cref="ApplyDuration(ulong,GameplayTag,double,int)"/> but the effect also GRANTS the given
        /// status tag (§5.5) for its lifetime — set in <see cref="RecomputeTags"/>'s CurrentTags, cleared when
        /// the effect expires. The status-effect backbone (e.g. a stun debuff grants `Status.Stunned`).
        /// </summary>
        public int ApplyDuration(ulong actor, GameplayTag attribute, double magnitude, int durationTicks, GameplayTag grantStatus)
            => ApplyDurationCore(actor, _attrByTag[attribute.Id], ModifierOp.Add, magnitude, durationTicks, StatusMask(grantStatus), 0);

        /// <summary>
        /// Apply a duration effect on a specific aggregation channel (§5.3): <see cref="ModifierOp.Add"/>
        /// (Current += Σmag), <see cref="ModifierOp.Multiply"/> (Current ·= Πmag), or
        /// <see cref="ModifierOp.Override"/> (Current = mag, applied last). Channels are combined by
        /// <see cref="RecomputeCurrent"/> as <c>Current = clamp(override ?? (Base + ΣAdd)·ΠMul, Min, Max)</c>.
        /// Returns the effect handle, or <see cref="InvalidEffect"/> when the store is full.
        /// </summary>
        public int ApplyDuration(ulong actor, GameplayTag attribute, ModifierOp op, double magnitude, int durationTicks)
            => ApplyDurationCore(actor, _attrByTag[attribute.Id], op, magnitude, durationTicks, 0, 0);

        /// <summary>
        /// Apply a duration effect with an ONGOING requirement (§5.5): each step <see cref="RecomputeSuspension"/>
        /// tests <paramref name="ongoingRequirement"/> against the actor's status; while it fails the effect is
        /// SUSPENDED (its modifiers + grants stop applying) but is NOT removed — it resumes when the condition
        /// returns, and still expires on its EndTick. Returns the effect handle.
        /// The <paramref name="ongoingRequirement"/> is compiled (via <see cref="CompileStatusCondition"/>) to a
        /// require-all / require-none status-bit pair stored on the row; each <see cref="RecomputeSuspension"/>
        /// re-tests it against the actor's CurrentTags. (AND-combined Exists/NotExists only, per D6.1.)
        /// </summary>
        public int ApplyDuration(ulong actor, GameplayTag attribute, ModifierOp op, double magnitude, int durationTicks, GameplayTagCondition[] ongoingRequirement)
        {
            CompileStatusCondition(ongoingRequirement, out int reqAll, out int reqNone);
            return ApplyDurationCore(actor, _attrByTag[attribute.Id], op, magnitude, durationTicks, 0, 0, 0, reqAll, reqNone, 0);
        }

        /// <summary>
        /// Apply a duration CAP-BUFF or field-targeted effect (§5.3): the modifier is folded into the
        /// attribute's <paramref name="field"/> — <see cref="HateField.Max"/> for "+10% Max Health",
        /// <see cref="HateField.Min"/> for a floor buff, or <see cref="HateField.Current"/> for the ordinary
        /// working-value channel. Caps fold from their persistent base (MinBase/MaxBase) each
        /// <see cref="RecomputeCurrent"/>, so the buff auto-expires normally; the raised cap then bounds
        /// Current's clamp. Returns the effect handle, or <see cref="InvalidEffect"/> when the store is full.
        /// </summary>
        public int ApplyDuration(ulong actor, GameplayTag attribute, HateField field, ModifierOp op, double magnitude, int durationTicks)
            => ApplyDurationCore(actor, _attrByTag[attribute.Id], op, magnitude, durationTicks, 0, 0, field: field);

        private int ApplyDurationCore(ulong actor, int attr, ModifierOp op, double magnitude, int durationTicks,
            int grantTags, int grantAbilities, int grantImmunity = 0, int reqAll = 0, int reqNone = 0, int reqMode = -1,
            HateField field = HateField.Current)
        {
            ulong r = _effects.AllocRow();
            if (r == DataStore.InvalidRow) return InvalidEffect;
            _effects.SetInt(r, (ulong)FxActor, (int)actor);
            _effects.SetInt(r, (ulong)FxAttr, attr);
            _effects.SetFloat(r, (ulong)FxMagnitude, (float)magnitude);
            _effects.SetInt(r, (ulong)FxEndTick, _currentTick + durationTicks);
            _effects.SetInt(r, (ulong)FxActive, 1);
            _effects.SetInt(r, (ulong)FxGrantTags, grantTags);
            _effects.SetInt(r, (ulong)FxGrantAbilities, grantAbilities);
            _effects.SetInt(r, (ulong)FxOp, (int)op);
            _effects.SetInt(r, (ulong)FxEffectId, -1); // non-stacking
            _effects.SetInt(r, (ulong)FxStackCount, 1);
            _effects.SetInt(r, (ulong)FxGrantImmunity, grantImmunity);
            _effects.SetInt(r, (ulong)FxReqAll, reqAll);
            _effects.SetInt(r, (ulong)FxReqMode, reqMode);   // -1 = no ongoing requirement
            _effects.SetInt(r, (ulong)FxSuspended, 0);
            _effects.SetInt(r, (ulong)FxField, (int)field);
            _effects.SetInt(r, (ulong)FxReqNone, reqNone);
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
        public int ApplyStackingDuration(ulong actor, GameplayTag attribute, ModifierOp op, double magnitudePerStack,
            int durationTicks, int effectId, int stackLimit, StackRefresh refresh = StackRefresh.RefreshDuration)
        {
            int attr = _attrByTag[attribute.Id];
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
            _effects.SetInt(r, (ulong)FxActor, (int)actor);
            _effects.SetInt(r, (ulong)FxAttr, attr);
            _effects.SetFloat(r, (ulong)FxMagnitude, (float)magnitudePerStack);
            _effects.SetInt(r, (ulong)FxEndTick, _currentTick + durationTicks);
            _effects.SetInt(r, (ulong)FxActive, 1);
            _effects.SetInt(r, (ulong)FxGrantTags, 0);
            _effects.SetInt(r, (ulong)FxGrantAbilities, 0);
            _effects.SetInt(r, (ulong)FxOp, (int)op);
            _effects.SetInt(r, (ulong)FxEffectId, effectId);
            _effects.SetInt(r, (ulong)FxStackCount, 1);
            _effects.SetInt(r, (ulong)FxGrantImmunity, 0);
            _effects.SetInt(r, (ulong)FxReqAll, 0);
            _effects.SetInt(r, (ulong)FxReqMode, -1);
            _effects.SetInt(r, (ulong)FxSuspended, 0);
            _effects.SetInt(r, (ulong)FxField, (int)HateField.Current);
            _effects.SetInt(r, (ulong)FxReqNone, 0);
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
        private int FindActiveStack(ulong actor, int effectId)
        {
            for (int r = 0; r < _effectsCapacity; r++)
            {
                if (!_effects.IsValid((ulong)r)) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActive, out int active);
                if (active == 0) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxEffectId, out int id);
                if (id != effectId) continue;
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int a);
                if (a == (int)actor) return r;
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
        /// Recompute every attribute's derived values from its bases and active duration effects (§5.3),
        /// in the order caps → Current so cap buffs bound the Current clamp:
        /// <list type="number">
        /// <item>reset the effective caps to base (Min = MinBase, Max = MaxBase) — one parallel System;</item>
        /// <item>fold active <see cref="HateField.Min"/>/<see cref="HateField.Max"/> effects into Min/Max;</item>
        /// <item>derive Current = clamp(Base, Min, Max) — one parallel System over the effective caps;</item>
        /// <item>fold active <see cref="HateField.Current"/> effects into Current: <c>clamp(override ??
        /// (Base + ΣAdd)·ΠMul, Min, Max)</c>.</item>
        /// </list>
        /// The Set/clamp passes are parallel; the per-channel accumulation is host-side over the ACTIVE effects
        /// (cost ∝ live effects, not actor count — moving it to a dirty-driven System is future work).
        /// </summary>
        public void RecomputeCurrent()
        {
            _lens.Execute(_recomputeCaps, _table);   // effective Min/Max <- persistent base
            ScanActiveModifiers();                    // bucket active effects by (actor, attr, field)
            ApplyCapModifiers();                       // fold Min/Max channels into the cap columns
            _lens.Execute(_recomputeCurrent, _table); // Current = clamp(Base, effective Min, effective Max)
            ApplyCurrentModifiers();                   // fold Current channel into Current, clamp to caps
        }

        // Per-(actor,attrIndex,field) modifier channels, reused across RecomputeCurrent calls (cleared each
        // time). Real-space (double): the per-effect aggregation reads/writes through the typed accessors, so
        // no offset/bias math is needed here — that lives only in the bulk-System path (AddBulkModify).
        private struct ModChannels { public double SumAdd; public double ProdMul; public bool HasOverride; public double OverrideValue; }
        private readonly Dictionary<long, ModChannels> _modAccum = new Dictionary<long, ModChannels>();
        // Key packs field (2 bits) + attr (low 32 below the field) + actor. attr < 2^30 and actor < 2^32 in
        // practice, so: actor in the high 32, field<<30 | attr in the low 32.
        private static long ModKey(int actor, int attr, HateField field) => ((long)actor << 32) | ((long)(int)field << 30) | (uint)attr;

        // One pass over the active effects: accumulate each into its (actor, attr, field) channels.
        private void ScanActiveModifiers()
        {
            _modAccum.Clear();
            if (_effects.LiveCount == 0) return;
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
                _effects.TryGetInt((ulong)r, (ulong)FxField, out int fieldCode);
                if (count < 1) count = 1; // stacks scale the contribution (§5.5)

                long key = ModKey(actor, attr, (HateField)fieldCode);
                if (!_modAccum.TryGetValue(key, out var ch)) ch = new ModChannels { ProdMul = 1.0 };
                switch ((ModifierOp)opCode)
                {
                    case ModifierOp.Add:      ch.SumAdd += (double)mag * count; break;       // count·mag
                    case ModifierOp.Multiply: for (int s = 0; s < count; s++) ch.ProdMul *= mag; break; // mag^count
                    case ModifierOp.Override: ch.HasOverride = true; ch.OverrideValue = mag; break;     // last write in scan wins
                }
                _modAccum[key] = ch;
            }
        }

        // Aggregate a base value through a channel: override ?? (base + ΣAdd)·ΠMul.
        private static double AggregateChannel(double baseV, in ModChannels ch)
            => ch.HasOverride ? ch.OverrideValue : (baseV + ch.SumAdd) * ch.ProdMul;

        // Fold Min/Max-field channels into the effective cap columns (base already reset by _recomputeCaps),
        // clamped to the declared envelope by WriteAttrCell (a cap cannot exceed the storage width).
        private void ApplyCapModifiers()
        {
            if (_modAccum.Count == 0) return;
            foreach (var kv in _modAccum)
            {
                var field = (HateField)(int)((kv.Key >> 30) & 0x3);
                if (field == HateField.Current) continue;
                ulong actor = (ulong)(uint)(kv.Key >> 32);
                int attr = (int)((uint)kv.Key & 0x3fffffff);
                int capCol = field == HateField.Max ? MaxCol(attr) : MinCol(attr);
                int baseCol = field == HateField.Max ? MaxBaseCol(attr) : MinBaseCol(attr);
                double v = AggregateChannel(ReadAttrCell(actor, attr, baseCol), kv.Value);
                WriteAttrCell(actor, attr, capCol, v);
            }
        }

        // Fold the Current-field channel into Current, once per touched (actor,attr). Real-space via the typed
        // accessors; clamp uses the per-actor effective Min/Max cols (clamp to Max then to Min, so Min wins —
        // matching the _recomputeCurrent IR — which matters once a cap buff can push Min above Max).
        private void ApplyCurrentModifiers()
        {
            if (_modAccum.Count == 0) return;
            foreach (var kv in _modAccum)
            {
                var field = (HateField)(int)((kv.Key >> 30) & 0x3);
                if (field != HateField.Current) continue;
                ulong actor = (ulong)(uint)(kv.Key >> 32);
                int attr = (int)((uint)kv.Key & 0x3fffffff);
                double cur = AggregateChannel(ReadAttrCell(actor, attr, BaseCol(attr)), kv.Value);
                double mn = ReadAttrCell(actor, attr, MinCol(attr));
                double mx = ReadAttrCell(actor, attr, MaxCol(attr));
                if (cur > mx) cur = mx;
                if (cur < mn) cur = mn;
                WriteAttrCell(actor, attr, CurrentCol(attr), cur);
            }
        }

        // ── Tags: granted (§5.5) + intrinsic ────────────────────────────────

        /// <summary>Number of registered hot status tags.</summary>
        public int StatusCount => _statusByTag.Count;

        /// <summary>The bit index (0..31) for a status tag, or -1 if it is not a registered status.</summary>
        public int StatusBit(GameplayTag status) => _statusByTag.TryGetValue(status.Id, out int b) ? b : -1;

        /// <summary>The single-bit mask for a status tag (0 if unregistered) — the internal compiled form.</summary>
        public int StatusMask(GameplayTag status) => _statusByTag.TryGetValue(status.Id, out int b) ? (1 << b) : 0;

        /// <summary>Combine several status tags into one hot-tag mask.</summary>
        public int StatusMask(params GameplayTag[] statuses)
        {
            int m = 0;
            foreach (var s in statuses) if (_statusByTag.TryGetValue(s.Id, out int b)) m |= 1 << b;
            return m;
        }

        /// <summary>Add intrinsic (always-on) status tags to an actor's BaseTags. Reflected in CurrentTags after <see cref="RecomputeTags"/>.</summary>
        public void SetBaseStatus(ulong actor, params GameplayTag[] statuses)
        {
            int mask = StatusMask(statuses);
            _store.TryGetInt(actor, (ulong)_baseTagsCol, out int v);
            _store.SetInt(actor, (ulong)_baseTagsCol, v | mask);
        }

        /// <summary>Remove intrinsic status tags from an actor's BaseTags.</summary>
        public void ClearBaseStatus(ulong actor, params GameplayTag[] statuses)
        {
            int mask = StatusMask(statuses);
            _store.TryGetInt(actor, (ulong)_baseTagsCol, out int v);
            _store.SetInt(actor, (ulong)_baseTagsCol, v & ~mask);
        }

        /// <summary>The actor's current status bitmask (intrinsic + granted) as of the last <see cref="RecomputeTags"/>.</summary>
        public int GetCurrentTags(ulong actor)
        {
            _store.TryGetInt(actor, (ulong)_currentTagsCol, out int v);
            return v;
        }

        /// <summary>True if the actor currently has the given status tag (post-<see cref="RecomputeTags"/>).</summary>
        public bool HasStatus(ulong actor, GameplayTag status) => (GetCurrentTags(actor) & StatusMask(status)) != 0;

        /// <summary>True if the actor currently has ALL the given status tags.</summary>
        public bool HasAllStatus(ulong actor, params GameplayTag[] statuses)
        {
            int m = StatusMask(statuses);
            return (GetCurrentTags(actor) & m) == m;
        }

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
        /// Compile a <see cref="GameplayTagCondition"/> set (the authoring form) to the D6.1 batch predicate
        /// over the hot status mask: AND-combined <c>Exists</c> (status must be present) / <c>NotExists</c>
        /// (status must be absent), yielding <paramref name="requireAll"/> and <paramref name="requireNone"/>
        /// bit masks. Value comparisons, OR/XOR, and tag hierarchy are not yet supported (D6.2).
        /// </summary>
        private void CompileStatusCondition(GameplayTagCondition[] conditions, out int requireAll, out int requireNone)
        {
            requireAll = 0; requireNone = 0;
            if (conditions == null) return;
            foreach (var c in conditions)
            {
                if (c == null) continue;
                int bit = StatusMask(c.Tag);
                switch (c.Comparison)
                {
                    case GameplayTagComparisonOp.Exists:    requireAll |= bit; break;
                    case GameplayTagComparisonOp.NotExists: requireNone |= bit; break;
                    default:
                        throw new NotSupportedException(
                            $"HATE D6.1 trigger compiler supports AND-combined Exists/NotExists on status tags only (got {c.Comparison}).");
                }
            }
        }

        /// <summary>
        /// Evaluate a condition set across all live actors in one branchless DataLens pass, writing 1/0 into
        /// the Selected column: zero all, set 1 where all required-present bits hold, zero where any
        /// required-absent bit is present.
        /// </summary>
        public void EvaluateTrigger(params GameplayTagCondition[] conditions)
        {
            CompileStatusCondition(conditions, out int requireAll, out int requireNone);
            using var program = new IrProgram();
            program.Add(IrOp.Int(0, _selectedCol, SystemOp.Set, 0));
            // HasAllBits(0) is always true, so requireAll == 0 selects every row (e.g. an exclude-only gate).
            program.Add(IrOp.Int(0, _selectedCol, SystemOp.Set, 1)
                .WithPredicate(_currentTagsCol, CompareOp.HasAllBits, requireAll));
            if (requireNone != 0)
                program.Add(IrOp.Int(0, _selectedCol, SystemOp.Set, 0)
                    .WithPredicate(_currentTagsCol, CompareOp.HasAnyBits, requireNone));
            _lens.Execute(program, _table);
        }

        /// <summary>Number of live actors whose status satisfies the condition set.</summary>
        public int CountMatching(params GameplayTagCondition[] conditions)
        {
            EvaluateTrigger(conditions);
            _lens.RefreshView(_selectedView, _store);
            int n = _selectedView.CopyInts(_selectedScratch);
            int count = 0;
            for (int i = 0; i < n; i++)
                if (_selectedScratch[i] != 0) count++;
            return count;
        }

        /// <summary>
        /// Apply an Instant effect to an attribute's Base for every actor whose status satisfies the
        /// condition set (e.g. "deal 20 damage to everyone that is Burning"). Evaluates the gate into the
        /// Selected column (branchless), then applies in real space per matched actor (type/offset-correct
        /// for any attribute width). Returns the number of actors affected.
        /// TODO(perf): a typed mixed-type-predicate bulk pass can fuse this back into one branchless System
        /// for single-predicate, single-type cases (the old RunFloatWhereInt fast path).
        /// </summary>
        public int ApplyInstantWhere(GameplayTag attribute, ModifierOp op, double magnitude, params GameplayTagCondition[] conditions)
        {
            EvaluateTrigger(conditions);
            _lens.RefreshView(_selectedView, _store);
            int n = _selectedView.CopyInts(_selectedScratch);
            int affected = 0;
            for (int i = 0; i < n; i++)
                if (_selectedScratch[i] != 0)
                {
                    ApplyInstant(_selectedView.SourceRow((ulong)i), attribute, op, magnitude);
                    affected++;
                }
            return affected;
        }

        // ── Ability granting (§7 "AbilityInstances") ─────────────────────────
        // AbilityDefs are a compact catalogue; which abilities an actor holds is a per-actor bitmask.
        // Only granted abilities can be activated by id (parity with UE GiveAbility / FGameplayAbilitySpec).

        /// <summary>Number of abilities registered in the catalogue.</summary>
        public int AbilityCount => _abilityCatalogue.Count;

        /// <summary>
        /// Register an ability definition (addressed by its <see cref="AbilityDef.Id"/> tag) in the world's
        /// catalogue. Internally each ability gets a 0-based slot (max 32) = the bit used by the grant bitmask;
        /// callers address abilities by GameplayTag. Definitions are shared data; per-actor state lives in the
        /// grant bitmask (and cooldown tags / effect rows).
        /// </summary>
        public void RegisterAbility(in AbilityDef ability)
        {
            if (!ability.Id.IsValid)
                throw new ArgumentException("AbilityDef.Id must be a valid GameplayTag.", nameof(ability));
            if (_abilityByTag.ContainsKey(ability.Id.Id)) return; // idempotent re-register
            if (_abilityCatalogue.Count >= MaxAbilities)
                throw new InvalidOperationException($"At most {MaxAbilities} abilities per world (Int32 grant bitmask).");
            _abilityByTag[ability.Id.Id] = _abilityCatalogue.Count;
            _abilityCatalogue.Add(ability);
        }

        /// <summary>The registered definition for an ability tag.</summary>
        public AbilityDef GetAbility(GameplayTag ability) => _abilityCatalogue[_abilityByTag[ability.Id]];

        /// <summary>True if the ability tag is registered in this world.</summary>
        public bool HasRegisteredAbility(GameplayTag ability) => _abilityByTag.ContainsKey(ability.Id);

        // Internal: resolve an ability tag to its catalogue slot / grant bit.
        private int AbilityId(GameplayTag ability) => _abilityByTag[ability.Id];
        private int AbilityBit(int abilityId)
        {
            if ((uint)abilityId >= (uint)_abilityCatalogue.Count)
                throw new ArgumentOutOfRangeException(nameof(abilityId));
            return 1 << abilityId;
        }
        private int AbilityBit(GameplayTag ability) => AbilityBit(_abilityByTag[ability.Id]);

        // Intrinsic grants live in BaseAbilities; the effective GrantedAbilities (read by HasAbility /
        // activation / eligibility) is BaseAbilities OR active-effect grants, rebuilt by RecomputeAbilities.
        // Direct grant/revoke writes BOTH so the change is usable the same frame without a recompute
        // (mirrors the cooldown's immediate-reflect trick); RecomputeAbilities then keeps them consistent.

        /// <summary>Grant an intrinsic ability to one actor (sets its base + effective grant bit). Idempotent.</summary>
        public void GrantAbility(ulong actor, GameplayTag ability)
        {
            int bit = AbilityBit(ability);
            _store.TryGetInt(actor, (ulong)_baseAbilitiesCol, out int b);
            _store.SetInt(actor, (ulong)_baseAbilitiesCol, b | bit);
            _store.TryGetInt(actor, (ulong)_grantedAbilitiesCol, out int e);
            _store.SetInt(actor, (ulong)_grantedAbilitiesCol, e | bit);
        }

        /// <summary>Revoke an intrinsic ability from one actor (clears its base + effective grant bit).
        /// If an active effect still grants it, the next <see cref="RecomputeAbilities"/> restores it.</summary>
        public void RevokeAbility(ulong actor, GameplayTag ability)
        {
            int bit = AbilityBit(ability);
            _store.TryGetInt(actor, (ulong)_baseAbilitiesCol, out int b);
            _store.SetInt(actor, (ulong)_baseAbilitiesCol, b & ~bit);
            _store.TryGetInt(actor, (ulong)_grantedAbilitiesCol, out int e);
            _store.SetInt(actor, (ulong)_grantedAbilitiesCol, e & ~bit);
        }

        /// <summary>Revoke every intrinsic ability from one actor (effect grants reappear on recompute).</summary>
        public void RevokeAllAbilities(ulong actor)
        {
            _store.SetInt(actor, (ulong)_baseAbilitiesCol, 0);
            _store.SetInt(actor, (ulong)_grantedAbilitiesCol, 0);
        }

        /// <summary>Grant an ability to every live actor in one pass (e.g. a class/role's kit). Parallel.</summary>
        public ulong GrantAbilityAll(GameplayTag ability)
        {
            int bit = AbilityBit(ability);
            _lens.RunInt(_store, (ulong)_baseAbilitiesCol, SystemOp.Or, bit);
            return _lens.RunInt(_store, (ulong)_grantedAbilitiesCol, SystemOp.Or, bit);
        }

        /// <summary>Revoke an ability from every live actor in one pass. Parallel.</summary>
        public ulong RevokeAbilityAll(GameplayTag ability)
        {
            int bit = AbilityBit(ability);
            _lens.RunInt(_store, (ulong)_baseAbilitiesCol, SystemOp.AndNot, bit);
            return _lens.RunInt(_store, (ulong)_grantedAbilitiesCol, SystemOp.AndNot, bit);
        }

        /// <summary>True if the actor currently holds the ability (its effective grant bit is set).</summary>
        public bool HasAbility(ulong actor, GameplayTag ability)
        {
            _store.TryGetInt(actor, (ulong)_grantedAbilitiesCol, out int cur);
            return (cur & AbilityBit(ability)) != 0;
        }

        /// <summary>The actor's full effective granted-ability bitmask (intrinsic + effect-granted).</summary>
        public int GetGrantedAbilities(ulong actor)
        {
            _store.TryGetInt(actor, (ulong)_grantedAbilitiesCol, out int cur);
            return cur;
        }

        /// <summary>
        /// Grant an ability to an actor for <paramref name="durationTicks"/> ticks via an auto-expiring
        /// duration effect (§7 effect-driven granting, UE parity): a pure grant effect (no attribute change).
        /// The grant appears in <see cref="GetGrantedAbilities"/> after the next <see cref="RecomputeAbilities"/>
        /// and is dropped automatically when the effect expires (<see cref="ExpireEffects"/> + recompute).
        /// Returns the effect handle, or <see cref="InvalidEffect"/> when the effects store is full.
        /// </summary>
        public int GrantAbilityFor(ulong actor, GameplayTag ability, int durationTicks)
            => ApplyDurationCore(actor, 0, ModifierOp.Add, 0, durationTicks, 0, AbilityBit(ability));

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

        /// <summary>Add intrinsic immunity tags to an actor (effective immediately + persists across recompute).</summary>
        public void SetBaseImmunity(ulong actor, params GameplayTag[] immunity)
        {
            int mask = StatusMask(immunity);
            _store.TryGetInt(actor, (ulong)_baseImmunityCol, out int b);
            _store.SetInt(actor, (ulong)_baseImmunityCol, b | mask);
            _store.TryGetInt(actor, (ulong)_immunityCol, out int e);
            _store.SetInt(actor, (ulong)_immunityCol, e | mask);
        }

        /// <summary>Remove intrinsic immunity tags (effect-conferred immunity reappears on recompute).</summary>
        public void ClearBaseImmunity(ulong actor, params GameplayTag[] immunity)
        {
            int mask = StatusMask(immunity);
            _store.TryGetInt(actor, (ulong)_baseImmunityCol, out int b);
            _store.SetInt(actor, (ulong)_baseImmunityCol, b & ~mask);
            _store.TryGetInt(actor, (ulong)_immunityCol, out int e);
            _store.SetInt(actor, (ulong)_immunityCol, e & ~mask);
        }

        /// <summary>The actor's effective immunity mask (intrinsic + effect-conferred, post-<see cref="RecomputeImmunity"/>).</summary>
        public int GetImmunity(ulong actor)
        {
            _store.TryGetInt(actor, (ulong)_immunityCol, out int v);
            return v;
        }

        /// <summary>True if any of the given asset tags are in the actor's effective immunity mask.</summary>
        public bool IsImmuneTo(ulong actor, params GameplayTag[] assetTags) => (GetImmunity(actor) & StatusMask(assetTags)) != 0;

        /// <summary>
        /// Confer immunity to <paramref name="immunityMask"/> on an actor for <paramref name="durationTicks"/>
        /// ticks via an auto-expiring duration effect (a "ward"). Folded into the effective immunity by the
        /// next <see cref="RecomputeImmunity"/> and dropped when the effect expires.
        /// </summary>
        public int GrantImmunityFor(ulong actor, GameplayTag immunity, int durationTicks)
            => ApplyDurationCore(actor, 0, ModifierOp.Add, 0, durationTicks, 0, 0, StatusMask(immunity));

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
                _effects.TryGetInt((ulong)r, (ulong)FxReqAll, out int reqAll);
                _effects.TryGetInt((ulong)r, (ulong)FxReqNone, out int reqNone);
                _effects.TryGetInt((ulong)r, (ulong)FxActor, out int actor);
                int tags = GetCurrentTags((ulong)actor);
                // Requirement met = every required-present bit held AND no required-absent bit present.
                bool met = (tags & reqAll) == reqAll && (tags & reqNone) == 0;
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
        public bool TryApplyInstant(ulong actor, GameplayTag attribute, ModifierOp op, double magnitude, GameplayTag assetTag, GameplayTag blockedCue = default)
        {
            if (IsImmuneTo(actor, assetTag))
            {
                if (blockedCue.IsValid) EmitCue(blockedCue, actor, 0f);
                return false;
            }
            ApplyInstant(actor, attribute, op, magnitude);
            return true;
        }

        /// <summary>
        /// Apply a Duration effect UNLESS the actor is immune to <paramref name="assetTag"/> (§5.5). Returns
        /// the effect handle, or <see cref="InvalidEffect"/> if blocked (optionally emitting a cue).
        /// </summary>
        public int TryApplyDuration(ulong actor, GameplayTag attribute, ModifierOp op, double magnitude, int durationTicks,
            GameplayTag assetTag, GameplayTag blockedCue = default)
        {
            if (IsImmuneTo(actor, assetTag))
            {
                if (blockedCue.IsValid) EmitCue(blockedCue, actor, 0f);
                return InvalidEffect;
            }
            return ApplyDuration(actor, attribute, op, magnitude, durationTicks);
        }

        // ── Execution & magnitude calculations (§5.4) ────────────────────────
        // The "escape to host" path: a magnitude or a whole multi-attribute interaction is computed by host
        // code that captures source/target attributes, instead of a constant. (A DataLens IR expression-tree
        // MMC — branchless, batchable — is future work, couples to the A4 read-side expression nodes.)

        /// <summary>Computes an effect magnitude from captured source/target state (a Magnitude Calc, §5.4).
        /// Evaluated at apply time = a snapshot. <paramref name="source"/> == <paramref name="target"/> for a self-effect.</summary>
        public delegate float MagnitudeCalc(HateWorld world, ulong source, ulong target);

        /// <summary>Custom multi-attribute logic over a source/target (an Execution Calc, §5.4): reads captured
        /// attributes and writes back through the normal effect API (e.g. armour → mitigation → final damage → Health).</summary>
        public delegate void ExecutionCalc(HateWorld world, ulong source, ulong target);

        /// <summary>Apply an Instant whose magnitude is computed by <paramref name="calc"/> (snapshot of source/target at apply).</summary>
        public void ApplyInstantCalc(ulong source, ulong target, GameplayTag attribute, ModifierOp op, MagnitudeCalc calc)
        {
            if (calc == null) throw new ArgumentNullException(nameof(calc));
            ApplyInstant(target, attribute, op, calc(this, source, target));
        }

        /// <summary>Apply a Duration whose magnitude is computed by <paramref name="calc"/> (snapshot at apply). Returns the effect handle.</summary>
        public int ApplyDurationCalc(ulong source, ulong target, GameplayTag attribute, ModifierOp op, MagnitudeCalc calc, int durationTicks)
        {
            if (calc == null) throw new ArgumentNullException(nameof(calc));
            return ApplyDuration(target, attribute, op, calc(this, source, target), durationTicks);
        }

        /// <summary>
        /// Run a custom Execution calc (§5.4): host code that reads captured source/target attributes and
        /// applies the resulting changes via the normal effect API (Instants/Durations/cues). The canonical
        /// example is a damage exec — armour → mitigation → final damage → Health — that the constant-modifier
        /// channels can't express. Runs synchronously for the one interaction (execs touch the few actors a
        /// gameplay event hits, not the whole population every frame).
        /// </summary>
        public void RunExecution(ulong source, ulong target, ExecutionCalc exec)
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
        public bool CanActivate(ulong actor, GameplayTag ability)
            => HasAbility(actor, ability) && CanActivate(actor, GetAbility(ability));

        /// <summary>Tag-based <see cref="TryActivate(ulong,in AbilityDef)"/>: no-op unless the ability is granted.</summary>
        public bool TryActivate(ulong actor, GameplayTag ability)
            => HasAbility(actor, ability) && TryActivate(actor, GetAbility(ability));

        /// <summary>
        /// CanActivate (§7): true if the ability's cost is affordable, its cooldown is ready (the actor
        /// lacks the cooldown status), and its activation requirement (if any) passes against the actor's
        /// current status. Reads status as of the last <see cref="RecomputeTags"/>. This overload does NOT
        /// check granting — use <see cref="CanActivate(ulong,GameplayTag)"/> to gate on the grant.
        /// </summary>
        public bool CanActivate(ulong actor, in AbilityDef ability)
        {
            if (ability.CostAttr.IsValid && GetBase(actor, ability.CostAttr) < ability.CostAmount)
                return false;

            int cur = GetCurrentTags(actor);
            int cdMask = StatusMask(ability.CooldownTag);
            if (cdMask != 0 && (cur & cdMask) != 0)
                return false; // on cooldown

            if (!MatchesCondition(cur, ability.Requirement))
                return false;

            return true;
        }

        /// <summary>
        /// Try to activate an ability for an actor. If <see cref="CanActivate"/>, Commit (§7): spend the
        /// cost (Instant on Base) and start the cooldown (a duration effect granting the cooldown status,
        /// reflected immediately so re-activation this step is blocked). Returns whether it activated.
        /// The cooldown ticks down via the normal step loop (AdvanceTick → ExpireEffects → RecomputeTags).
        /// </summary>
        public bool TryActivate(ulong actor, in AbilityDef ability)
        {
            if (!CanActivate(actor, ability))
                return false;

            if (ability.CostAttr.IsValid && ability.CostAmount != 0f)
                ApplyInstant(actor, ability.CostAttr, ModifierOp.Add, -ability.CostAmount);

            int cdMask = StatusMask(ability.CooldownTag);
            if (cdMask != 0 && ability.CooldownTicks > 0)
            {
                GameplayTag cdAttr = ability.CostAttr.IsValid ? ability.CostAttr : _attributes[0].Tag; // mag 0: a pure status grant
                ApplyDuration(actor, cdAttr, 0.0, ability.CooldownTicks, ability.CooldownTag);
                OrCurrentTags(actor, cdMask);
            }

            // Cosmetic cue on activation (e.g. cast start).
            if (ability.ActivateCue.IsValid)
                EmitCue(ability.ActivateCue, actor, 0f);

            // Schedule the payload task (a delayed effect + cue), fired by AdvanceAbilities.
            if (ability.EffectAttr.IsValid || ability.EffectCue.IsValid)
                ScheduleTask(actor, _currentTick + ability.EffectDelayTicks,
                             ability.EffectAttr, ability.EffectOp, ability.EffectMag, ability.EffectCue);

            return true;
        }

        // Single-actor test of a compiled status-condition set (the per-actor counterpart of EvaluateTrigger).
        private bool MatchesCondition(int currentTags, GameplayTagCondition[] conditions)
        {
            if (conditions == null || conditions.Length == 0) return true;
            CompileStatusCondition(conditions, out int requireAll, out int requireNone);
            return (currentTags & requireAll) == requireAll && (currentTags & requireNone) == 0;
        }

        // ── Ability Tasks (§7 step 3) + Cues (§9) ────────────────────────────

        /// <summary>Append a cosmetic cue (by GameplayTag) for presentation to drain. Never simulation state.</summary>
        public void EmitCue(GameplayTag cue, ulong actor, float magnitude)
            => _cues.Add(new CueEvent(cue, actor, magnitude));

        /// <summary>The cue events emitted since the last <see cref="ClearCues"/> (drain each frame).</summary>
        public System.Collections.Generic.IReadOnlyList<CueEvent> PendingCues => _cues;

        /// <summary>Clear the cue stream (after presentation has drained it).</summary>
        public void ClearCues() => _cues.Clear();

        /// <summary>Number of in-flight (scheduled, not yet fired) ability payload tasks.</summary>
        public int PendingTaskCount => (int)_tasks.LiveCount;

        // effectAttr is stored as its attribute INDEX (-1 = none); cue is a GameplayTag (u64) stored into the
        // Double Cue column via a bit-reinterpret (the C# binding has no per-cell SetLong; 0 = no cue).
        private void ScheduleTask(ulong actor, int fireTick, GameplayTag effectAttr, ModifierOp op, float mag, GameplayTag cue)
        {
            ulong r = _tasks.AllocRow();
            if (r == DataStore.InvalidRow) return; // task store full: drop (fixed capacity)
            _tasks.SetInt(r, (ulong)TkActor, (int)actor);
            _tasks.SetInt(r, (ulong)TkFireTick, fireTick);
            _tasks.SetInt(r, (ulong)TkEffectAttr, effectAttr.IsValid ? _attrByTag[effectAttr.Id] : -1);
            _tasks.SetInt(r, (ulong)TkEffectOp, (int)op);
            _tasks.SetFloat(r, (ulong)TkEffectMag, mag);
            _tasks.SetDouble(r, (ulong)TkCue, BitConverter.Int64BitsToDouble((long)cue.Id));
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
                _tasks.TryGetDouble((ulong)r, (ulong)TkCue, out double cueBits);
                ulong cueId = (ulong)BitConverter.DoubleToInt64Bits(cueBits);

                if (effectAttr >= 0)
                    ApplyInstant((ulong)actor, AttributeTag(effectAttr), (ModifierOp)opCode, mag);
                if (cueId != 0)
                    EmitCue(new GameplayTag(cueId), (ulong)actor, mag);

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
        public void EvaluateEligibility(GameplayTag ability)
        {
            EvaluateEligibility(GetAbility(ability));
            // Eligible = 0 where the actor lacks the grant bit.
            _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                (ulong)_grantedAbilitiesCol, CompareOp.LacksBits, AbilityBit(ability));
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

            if (ability.CostAttr.IsValid)
            {
                // knock out where Base[cost] < CostAmount. SinglePrecision cost = one branchless int-op-gated-by-
                // float-predicate pass; integral/double cost = a managed live-row scan (the C-ABI typed predicate
                // is Int32/Float-only and integral attributes store offset-encoded at a narrow width).
                int ca = _attrByTag[ability.CostAttr.Id];
                if (_attributes[ca].StorageType == DataLensValueType.Float)
                    _lens.RunIntWhereFloat(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                        (ulong)BaseCol(ca), CompareOp.Less, (float)ability.CostAmount);
                else
                    KnockOutUnaffordable(ca, ability.CostAmount);
            }

            int cdMask = StatusMask(ability.CooldownTag);
            if (cdMask != 0)
                // knock out where on cooldown (has any cooldown bit)
                _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                    (ulong)_currentTagsCol, CompareOp.HasAnyBits, cdMask);

            // Requirement (compiled status subset): knock out where an excluded status is present, and,
            // per required-present bit, where the actor lacks it (multi-bit RequireAll = several knockouts).
            CompileStatusCondition(ability.Requirement, out int requireAll, out int requireNone);
            if (requireNone != 0)
                _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                    (ulong)_currentTagsCol, CompareOp.HasAnyBits, requireNone);
            int ra = requireAll;
            while (ra != 0)
            {
                int bit = ra & -ra; ra &= ra - 1;
                _lens.RunInt(_store, (ulong)_eligibleCol, SystemOp.Set, 0,
                    (ulong)_currentTagsCol, CompareOp.LacksBits, bit);
            }
        }

        /// <summary>True if the actor's Eligible flag is set (read after <see cref="EvaluateEligibility"/>).</summary>
        public bool IsEligible(ulong actor)
        {
            _store.TryGetInt(actor, (ulong)_eligibleCol, out int v);
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
        public int CountEligible(GameplayTag ability)
        {
            EvaluateEligibility(ability);
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
        public void EvaluateUtility(in AbilityDef ability, GameplayTag weightAttr, float slope, float offset)
        {
            EvaluateEligibility(ability);
            int wa = _attrByTag[weightAttr.Id];
            // Seed Utility with the weight attribute's real Current: branchless column copy for SinglePrecision,
            // a managed de-offset materialisation for an integral/double weight attribute.
            if (_attributes[wa].StorageType == DataLensValueType.Float)
                _lens.RunFloatColumn(_store, (ulong)_utilityCol, SystemOp.Set, (ulong)CurrentCol(wa));
            else
                MaterialiseRealCurrent(wa, _utilityCol);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Mul, slope);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Add, offset);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Max, 0f);
            _lens.RunFloat(_store, (ulong)_utilityCol, SystemOp.Min, 1f);
            // zero out ineligible actors (float op gated by the int Eligible column)
            _lens.RunFloatWhereInt(_store, (ulong)_utilityCol, SystemOp.Set, 0f,
                (ulong)_eligibleCol, CompareOp.Equal, 0);
        }

        /// <summary>Actor's last-computed utility score (read after <see cref="EvaluateUtility"/>).</summary>
        public float GetUtility(ulong actor)
        {
            _store.TryGetFloat(actor, (ulong)_utilityCol, out float v);
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
        public ulong DecisionSourceActor(int viewRow) => _utilityView.SourceRow((ulong)viewRow);

        /// <summary>
        /// Scan the Utility column and return the actor with the highest score (and its score), or
        /// <see cref="InvalidActor"/> if none has utility &gt; 0. The "AI as a column scan" pick step.
        /// </summary>
        public ulong BestActorByUtility(out float bestUtility)
        {
            _lens.RefreshView(_utilityView, _store);
            int n = _utilityView.CopyFloats(_utilityScratch);
            ulong best = InvalidActor;
            float bestU = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = _utilityScratch[i];
                if (u > bestU)
                {
                    bestU = u;
                    best = _utilityView.SourceRow((ulong)i);
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
        public void SetVariance(ulong actor, float variance)
            => _store.SetFloat(actor, (ulong)RequireVarianceCol(), variance);

        /// <summary>Set the Variance of every live actor in one parallel pass.</summary>
        public ulong SetVarianceAll(float variance)
        {
            RequireAI();
            return _lens.RunFloat(_store, (ulong)_varianceCol, SystemOp.Set, variance);
        }

        /// <summary>An actor's current Variance.</summary>
        public float GetVariance(ulong actor)
        {
            _store.TryGetFloat(actor, (ulong)RequireVarianceCol(), out float v);
            return v;
        }

        private int RequireVarianceCol() { RequireAI(); return _varianceCol; }

        // ── Command override (hard "force this ability", §8.5) ───────────────

        /// <summary>Force <paramref name="actor"/> to choose ability slot <paramref name="abilitySlot"/>,
        /// overriding scoring at the next <see cref="Select"/>. Persists until cleared.</summary>
        public void SetCommand(ulong actor, int abilitySlot)
        {
            RequireAI();
            _store.SetInt(actor, (ulong)_commandCol, abilitySlot);
        }

        /// <summary>Clear one actor's command (return it to scored selection).</summary>
        public void ClearCommand(ulong actor)
        {
            RequireAI();
            _store.SetInt(actor, (ulong)_commandCol, -1);
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
                        _lens.RunFloatCurved(_store, (ulong)score, SystemOp.Mul, (ulong)MetricCol(c.MetricAttr), c.Curve);
            }
            else // WeightedSum
            {
                _lens.RunFloat(_store, (ulong)score, SystemOp.Set, 0f); // sum identity
                if (considerations != null)
                    foreach (var c in considerations)
                    {
                        // scratch = curve(metric); scratch *= weight; score += scratch.
                        _lens.RunFloatCurved(_store, (ulong)_scratchCol, SystemOp.Set, (ulong)MetricCol(c.MetricAttr), c.Curve);
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
        public float GetScore(ulong actor, int slot)
        {
            _store.TryGetFloat(actor, (ulong)ScoreCol(slot), out float v);
            return v;
        }

        /// <summary>An actor's chosen ability slot after <see cref="Select"/> (-1 = none).</summary>
        public int GetChoice(ulong actor)
        {
            RequireAI();
            _store.TryGetInt(actor, (ulong)_choiceCol, out int v);
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
        public ulong ChoiceSourceActor(int viewRow) => _choiceView.SourceRow((ulong)viewRow);

        private void OrCurrentTags(ulong actor, int mask)
        {
            _store.TryGetInt(actor, (ulong)_currentTagsCol, out int cur);
            _store.SetInt(actor, (ulong)_currentTagsCol, cur | mask);
        }

        // Real-space combine (host-side per-cell path goes through Read/WriteAttrCell, so no offset math here).
        private static double Combine(double value, ModifierOp op, double magnitude)
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

        // Append to `program` the op(s) applying (col = col OP realMagnitude) over attribute a's column for
        // EVERY live row, honouring the column's storage encoding. Add is offset-clean; Override/clamp encode
        // the operand; Multiply on an offset-encoded integral column applies the bias correction
        // stored' = stored*F + min*(F-1) (real*F mapped into offset space) as a Mul followed by an Add.
        private void AddBulkModify(IrProgram program, int a, int col, ModifierOp op, double magnitude)
        {
            var d = _attributes[a];
            var st = d.StorageType;
            bool offset = st != DataLensValueType.Float && st != DataLensValueType.Double;
            switch (op)
            {
                case ModifierOp.Add:
                    program.Add(IrOp.Typed(0, st, col, SystemOp.Add, magnitude));
                    break;
                case ModifierOp.Override:
                    program.Add(IrOp.Typed(0, st, col, SystemOp.Set, offset ? magnitude - d.Min : magnitude));
                    break;
                case ModifierOp.Multiply:
                    program.Add(IrOp.Typed(0, st, col, SystemOp.Mul, magnitude));
                    if (offset && magnitude != 1.0)
                        program.Add(IrOp.Typed(0, st, col, SystemOp.Add, d.Min * (magnitude - 1.0)));
                    break;
            }
        }

        // ── Inspection / tooling (read-only) ─────────────────────────────────
        // Read-only accessors the HATE Toolkit (and save/debug systems) use to render a world's state.

        /// <summary>Attribute tag for an index (registration order).</summary>
        public GameplayTag AttributeTag(int attr) => _attributes[attr].Tag;

        /// <summary>Attribute display name for an index (the tag's dot-path, or hex Id if unregistered).</summary>
        public string AttributeName(int attr) => _attributes[attr].Tag.Name ?? _attributes[attr].Tag.Id.ToString("X16");

        /// <summary>All attribute display names, in index order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> AttributeNames
        {
            get { var n = new string[_attributes.Length]; for (int i = 0; i < n.Length; i++) n[i] = AttributeName(i); return n; }
        }

        /// <summary>Status tag for a bit index (0..31), or default if out of range.</summary>
        public GameplayTag StatusTag(int bitIndex) => (uint)bitIndex < (uint)_statusTags.Length ? _statusTags[bitIndex] : default;

        /// <summary>Status display name for a bit index (0..31), or null if out of range.</summary>
        public string TagName(int bitIndex) => (uint)bitIndex < (uint)_statusTags.Length
            ? (_statusTags[bitIndex].Name ?? _statusTags[bitIndex].Id.ToString("X16")) : null;

        /// <summary>All status display names, in bit-index order.</summary>
        public System.Collections.Generic.IReadOnlyList<string> TagNames
        {
            get { var n = new string[_statusTags.Length]; for (int i = 0; i < n.Length; i++) n[i] = TagName(i); return n; }
        }

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
            public readonly HateField Field;   // which attribute field the modifier targets (Current/Min/Max)

            public EffectSnapshot(int handle, int actor, int attr, ModifierOp op, float magnitude, int endTick,
                int stackCount, bool suspended, int grantTags, int grantAbilities, int grantImmunity, int effectId,
                HateField field)
            {
                Handle = handle; Actor = actor; Attr = attr; Op = op; Magnitude = magnitude; EndTick = endTick;
                StackCount = stackCount; Suspended = suspended; GrantTags = grantTags; GrantAbilities = grantAbilities;
                GrantImmunity = grantImmunity; EffectId = effectId; Field = field;
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
                _effects.TryGetInt((ulong)r, (ulong)FxField, out int field);
                results.Add(new EffectSnapshot(r, actor, attr, (ModifierOp)op, mag, endTick, stacks,
                    suspended != 0, grantTags, grantAbilities, grantImmunity, effectId, (HateField)field));
            }
        }

        public void Dispose()
        {
            _activeView?.Dispose();
            _selectedView?.Dispose();
            _eligibleView?.Dispose();
            _utilityView?.Dispose();
            _choiceView?.Dispose();
            _recomputeCaps?.Dispose();
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
