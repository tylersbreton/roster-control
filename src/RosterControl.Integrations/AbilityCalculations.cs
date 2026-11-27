using System.Globalization;
using System.Text;
using System.Text.Json;
using RosterControl.Core.GameData;
using RosterControl.Core.Rules;

namespace RosterControl.Integrations;

/// <summary>
/// Reads ability numbers (damage, shields, heals… per rank, with their ratios) from a champion's game data as published by
/// Community Dragon (<c>game/data/characters/{id}/{id}.bin.json</c>). Each ability lists named "spell calculations" made
/// of formula parts; the common part types are evaluated, and a calculation using anything else is left out rather than
/// shown wrong.
/// </summary>
public static class AbilityCalculations
{
    /// <summary>At most this many numbers per ability (the first ones are the main ones).</summary>
    public const int MaxValuesPerAbility = 10;

    private static readonly char[] Slots = ['Q', 'W', 'E', 'R'];

    /// <param name="maxRanks">Ranks per slot (from Data Dragon); Q/W/E default to 5 and R to 3.</param>
    /// <summary>
    /// Attack damage per level from the champion's character record ("damagePerLevelModifiable", or the older
    /// "damagePerLevel"); null when the file has neither.
    /// </summary>
    public static double? AttackDamagePerLevel(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.EndsWith("/CharacterRecords/Root", StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind != JsonValueKind.Object) continue;
            var record = prop.Value;
            if (record.TryGetProperty("damagePerLevelModifiable", out var modifiable) && modifiable.ValueKind == JsonValueKind.Object
                && modifiable.TryGetProperty("baseValue", out var baseValue) && baseValue.TryGetDouble(out var v1))
                return v1;
            if (record.TryGetProperty("damagePerLevel", out var plain) && plain.TryGetDouble(out var v2)) return v2;
            return null;
        }
        return null;
    }

    public static IReadOnlyDictionary<char, IReadOnlyList<AbilityValue>> Parse(JsonElement root, IReadOnlyDictionary<char, int>? maxRanks = null)
    {
        var result = new Dictionary<char, IReadOnlyList<AbilityValue>>();
        if (root.ValueKind != JsonValueKind.Object) return result;
        string? recordKey = null;
        JsonElement record = default;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object && Str(prop.Value, "__type") == "CharacterRecord")
            {
                recordKey = prop.Name;
                record = prop.Value;
                break;
            }
        }
        if (recordKey is null) return result;
        var prefix = string.Join('/', recordKey.Split('/').Take(2)); // "Characters/Ahri"

        var spellPaths = new Dictionary<char, string>();
        if (record.TryGetProperty("spellNames", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var n in names.EnumerateArray())
            {
                if (i >= Slots.Length) break;
                if (n.GetString() is { Length: > 0 } name) spellPaths[Slots[i]] = $"{prefix}/Spells/{name}";
                i++;
            }
        }
        if (Str(record, "mCharacterPassiveSpell") is { Length: > 0 } passive) spellPaths['P'] = passive;

        foreach (var (slot, path) in spellPaths)
        {
            var ranks = slot == 'P' ? 1 : maxRanks?.GetValueOrDefault(slot) is > 0 and var r ? r : slot == 'R' ? 3 : 5;
            var values = new List<AbilityValue>();
            // The ability's main spell first, then other spells of the same ability (recasts, empowered versions…).
            var folder = path[..(path.LastIndexOf('/') + 1)];
            var spells = new List<JsonElement>();
            if (root.TryGetProperty(path, out var main)) spells.Add(main);
            if (folder.EndsWith("Ability/", StringComparison.Ordinal))
            {
                foreach (var prop in root.EnumerateObject())
                    if (prop.Name != path && prop.Name.StartsWith(folder, StringComparison.Ordinal) && Str(prop.Value, "__type") == "SpellObject")
                        spells.Add(prop.Value);
            }
            foreach (var spell in spells)
            {
                if (!spell.TryGetProperty("mSpell", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                foreach (var v in ParseSpell(data, ranks))
                {
                    if (values.Count >= MaxValuesPerAbility) break;
                    if (!values.Any(x => x.Label == v.Label || x.Text == v.Text)) values.Add(v);
                }
            }
            if (values.Count > 0) result[slot] = values;
        }
        return result;
    }

    /// <summary>The displayable calculations of one spell (its <c>mSpell</c> object).</summary>
    public static IReadOnlyList<AbilityValue> ParseSpell(JsonElement spell, int ranks)
    {
        if (!spell.TryGetProperty("mSpellCalculations", out var calcs) || calcs.ValueKind != JsonValueKind.Object) return [];
        return ParseCalculations(calcs, ranks, name => SpellDataValue(spell, name, ranks), index => EffectValue(spell, index, ranks));
    }

    private static List<AbilityValue> ParseCalculations(JsonElement calcs, int ranks, Func<string, double[]?> dataValue, Func<int, double[]?> effectValue,
        Func<string, bool>? skip = null)
    {
        var context = new Context(calcs, ranks, dataValue, effectValue);
        var list = new List<AbilityValue>();
        foreach (var calc in calcs.EnumerateObject())
        {
            if (calc.Name.StartsWith('{')) continue; // unnamed (hashed) calculations have no readable label
            if (skip?.Invoke(calc.Name) == true) continue;
            var label = Label(calc.Name);
            if (label.Length == 0) continue;
            if (context.Display(calc.Name) is not { } shown) continue;
            list.Add(new AbilityValue(label, shown.Text, shown.Formula));
        }
        return list;
    }

    /// <summary>
    /// Rune numbers from <c>perks.cdtb.bin.json</c>: every rune whose script data has calculations (Electrocute, Grasp,
    /// Aery, Comet, Fleet Footwork...). Runes without any (e.g. Conqueror) are left out.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<AbilityValue>> ParsePerks(JsonElement root)
    {
        var result = new Dictionary<int, IReadOnlyList<AbilityValue>>();
        if (root.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in root.EnumerateObject())
        {
            var perk = prop.Value;
            if (perk.ValueKind != JsonValueKind.Object || Str(perk, "__type") != "Perk") continue;
            if (!perk.TryGetProperty("mPerkId", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
            if (!perk.TryGetProperty("mScript", out var script) || !script.TryGetProperty("mSpellScriptData", out var data)) continue;
            if (!data.TryGetProperty("mCalculations", out var calcs) || calcs.ValueKind != JsonValueKind.Object) continue;
            var amounts = data.TryGetProperty("mEffectAmount", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            double[]? Amount(string name)
            {
                if (amounts.ValueKind != JsonValueKind.Object) return null;
                foreach (var v in amounts.EnumerateObject())
                    if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) && v.Value.TryGetDouble(out var d)) return [d];
                return null;
            }
            var values = ParseCalculations(calcs, 1, Amount, _ => null, IsHelperCalculation).Take(MaxValuesPerAbility).ToList();
            if (values.Count > 0) result[id] = values;
        }
        return result;
    }

    /// <summary>Melee/ranged helper calculations of runes only feed other calculations.</summary>
    private static bool IsHelperCalculation(string name) =>
        name.Contains("Melee", StringComparison.Ordinal) || name.Contains("Ranged", StringComparison.Ordinal) || name.Contains("ChampRange", StringComparison.Ordinal);

    // ── Labels ──────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] DropSuffixes = ["ForTooltip", "Tooltip", "Display", "Calculation", "Calc", "TT"];

    /// <summary>"TotalDamageTT" → "Total damage", "RCalculatedDamage" → "Damage", "PercentHPBlast" → "Percent HP blast".</summary>
    internal static string Label(string name)
    {
        var n = name.Trim();
        foreach (var suffix in DropSuffixes)
            if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal)) n = n[..^suffix.Length];
        if (n.Length > 2 && "QWERP".Contains(n[0]) && char.IsUpper(n[1]) && char.IsLower(n[2])) n = n[1..]; // QDamage → Damage
        if (n.Length > 2 && "QWER".Contains(n[0]) && char.IsDigit(n[1])) n = n[2..]; // Q2Damage → Damage
        if (n.Length > 3 && n.StartsWith("TT", StringComparison.Ordinal) && char.IsUpper(n[2])) n = n[2..]; // TTFirstHit → FirstHit
        var words = SplitWords(n).Where(w => !w.Equals("Calculated", StringComparison.Ordinal)).ToList();
        if (words.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var acronym = w.Length > 1 && w.All(c => char.IsUpper(c) || char.IsDigit(c));
            if (i > 0) sb.Append(' ');
            sb.Append(i == 0 || acronym ? (acronym ? w : char.ToUpperInvariant(w[0]) + w[1..]) : w.ToLowerInvariant());
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitWords(string s)
    {
        var current = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '_' or ' ')
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            var boundary = current.Length > 0 && (
                (char.IsUpper(c) && char.IsLower(s[i - 1])) ||
                (char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1])) ||
                (char.IsDigit(c) != char.IsDigit(s[i - 1]) && char.IsLetter(s[i - 1]) && char.IsLower(s[i - 1])));
            if (boundary)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    // ── Evaluation ──────────────────────────────────────────────────────────────────────────────────

    private const int Levels = 18;

    /// <summary>A value per rank: a constant part, stat ratios and an optional value per champion level 1–18.</summary>
    private sealed class Value(int ranks)
    {
        public double[] Base { get; } = new double[ranks];
        public List<(int Stat, int Formula, double[] Coefficient)> Ratios { get; } = [];
        public double[]? ByLevel { get; set; }
        public double RangedMultiplier { get; set; } = 1;

        public bool IsConstant => Ratios.Count == 0 && ByLevel is null;

        public static Value Levelled(int ranks, Func<int, double> atLevel)
        {
            var value = new Value(ranks) { ByLevel = new double[Levels] };
            for (var l = 1; l <= Levels; l++) value.ByLevel[l - 1] = atLevel(l);
            return value;
        }

        public AbilityFormula ToFormula(bool percent, int? precision) => new(
            Base.ToArray(),
            Ratios.Select(r => new StatRatio(r.Stat, r.Formula, r.Coefficient.ToArray())).ToList(),
            ByLevel?.ToArray(),
            percent,
            precision,
            RangedMultiplier);

        public static Value Const(int ranks, double v)
        {
            var value = new Value(ranks);
            Array.Fill(value.Base, v);
            return value;
        }

        public static Value PerRank(double[] values)
        {
            var value = new Value(values.Length);
            values.CopyTo(value.Base, 0);
            return value;
        }

        public static Value? Add(Value a, Value b)
        {
            var sum = new Value(a.Base.Length)
            {
                ByLevel = a.ByLevel is null ? b.ByLevel?.ToArray() : b.ByLevel is null ? a.ByLevel.ToArray() : a.ByLevel.Zip(b.ByLevel, (x, y) => x + y).ToArray(),
            };
            for (var i = 0; i < sum.Base.Length; i++) sum.Base[i] = a.Base[i] + b.Base[i];
            foreach (var ratio in a.Ratios.Concat(b.Ratios))
            {
                var existing = sum.Ratios.FindIndex(x => x.Stat == ratio.Stat && x.Formula == ratio.Formula);
                if (existing < 0) sum.Ratios.Add((ratio.Stat, ratio.Formula, (double[])ratio.Coefficient.Clone()));
                else
                    for (var i = 0; i < ratio.Coefficient.Length; i++) sum.Ratios[existing].Coefficient[i] += ratio.Coefficient[i];
            }
            return sum;
        }

        public static Value? Scale(Value v, double[] factor)
        {
            if (v.ByLevel is not null && factor.Distinct().Count() > 1) return null;
            var scaled = new Value(v.Base.Length);
            for (var i = 0; i < scaled.Base.Length; i++) scaled.Base[i] = v.Base[i] * factor[i];
            foreach (var (stat, formula, coef) in v.Ratios) scaled.Ratios.Add((stat, formula, coef.Select((c, i) => c * factor[i]).ToArray()));
            if (v.ByLevel is { } r) scaled.ByLevel = r.Select(x => x * factor[0]).ToArray();
            return scaled;
        }

        public static Value? Multiply(Value a, Value b) =>
            a.IsConstant ? Scale(b, a.Base) : b.IsConstant ? Scale(a, b.Base) : null;
    }

    private sealed class Context(JsonElement calcs, int ranks, Func<string, double[]?> dataValue, Func<int, double[]?> effectValue)
    {
        private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

        public (string Text, AbilityFormula Formula)? Display(string name)
        {
            if (!calcs.TryGetProperty(name, out var calc)) return null;
            var value = Evaluate(calc);
            if (value is null) return null;
            var percent = calc.TryGetProperty("mDisplayAsPercent", out var p) && p.ValueKind == JsonValueKind.True;
            int? precision = calc.TryGetProperty("mPrecision", out var pr) && pr.TryGetInt32(out var digits) ? digits : null;
            return Format(value, percent, precision) is { } text ? (text, value.ToFormula(percent, precision)) : null;
        }

        private Value? EvaluateNamed(string name)
        {
            if (!_evaluating.Add(name)) return null; // cycle
            try
            {
                return calcs.TryGetProperty(name, out var calc) ? Evaluate(calc) : null;
            }
            finally
            {
                _evaluating.Remove(name);
            }
        }

        private Value? Evaluate(JsonElement calc)
        {
            var type = Str(calc, "__type");
            // Some calculation types only have hashed names (e.g. one with a ranged multiplier); they share the formula layout.
            if (type == "GameCalculation" || (type is not null && type.StartsWith('{') && calc.TryGetProperty("mFormulaParts", out _)))
            {
                var total = Value.Const(ranks, 0);
                if (!calc.TryGetProperty("mFormulaParts", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
                foreach (var part in parts.EnumerateArray())
                {
                    var v = Part(part);
                    if (v is null) return null;
                    total = Value.Add(total, v);
                    if (total is null) return null;
                }
                var result = calc.TryGetProperty("mMultiplier", out var m) && m.ValueKind == JsonValueKind.Object
                    ? Part(m) is { } factor ? Value.Multiply(total, factor) : null
                    : total;
                if (result is not null && calc.TryGetProperty("mRangedMultiplier", out var rm) && rm.ValueKind == JsonValueKind.Object)
                {
                    if (Part(rm) is not { IsConstant: true } ranged) return null;
                    result.RangedMultiplier = ranged.Base[0];
                }
                return result;
            }
            switch (type)
            {
                case "GameCalculationModified":
                {
                    if (Str(calc, "mModifiedGameCalculation") is not { } inner || EvaluateNamed(inner) is not { } value) return null;
                    return calc.TryGetProperty("mMultiplier", out var m) && m.ValueKind == JsonValueKind.Object
                        ? Part(m) is { } factor ? Value.Multiply(value, factor) : null
                        : value;
                }
                case "GameCalculationConditional":
                    return Str(calc, "mDefaultGameCalculation") is { } fallback ? EvaluateNamed(fallback) : null;
                default:
                    return null;
            }
        }

        private Value? Part(JsonElement part)
        {
            switch (Str(part, "__type"))
            {
                case "NamedDataValueCalculationPart":
                    return DataValue(Str(part, "mDataValue")) is { } values ? Value.PerRank(values) : null;
                case "NumberCalculationPart":
                    return Value.Const(ranks, Num(part, "mNumber"));
                case "EffectValueCalculationPart":
                    return effectValue(part.TryGetProperty("mEffectIndex", out var idx) && idx.TryGetInt32(out var i) ? i : 0) is { } effect ? Value.PerRank(effect) : null;
                case "StatByCoefficientCalculationPart":
                    return Ratio(part, Enumerable.Repeat(Num(part, "mCoefficient"), ranks).ToArray());
                case "StatByNamedDataValueCalculationPart":
                    return DataValue(Str(part, "mDataValue")) is { } coef ? Ratio(part, coef) : null;
                case "StatBySubPartCalculationPart":
                    return part.TryGetProperty("mSubpart", out var sub) && Part(sub) is { IsConstant: true } c ? Ratio(part, c.Base) : null;
                case "SumOfSubPartsCalculationPart":
                {
                    if (!part.TryGetProperty("mSubparts", out var subs) || subs.ValueKind != JsonValueKind.Array) return null;
                    Value? total = Value.Const(ranks, 0);
                    foreach (var s in subs.EnumerateArray())
                    {
                        if (Part(s) is not { } v) return null;
                        total = Value.Add(total!, v);
                        if (total is null) return null;
                    }
                    return total;
                }
                case "ProductOfSubPartsCalculationPart":
                    return part.TryGetProperty("mPart1", out var a) && part.TryGetProperty("mPart2", out var b) && Part(a) is { } va && Part(b) is { } vb
                        ? Value.Multiply(va, vb)
                        : null;
                case "ByCharLevelInterpolationCalculationPart":
                {
                    var from = Num(part, "mStartValue");
                    var to = Num(part, "mEndValue");
                    // Either evenly per level, or along the stat growth curve (slower early, faster late).
                    var curve = part.TryGetProperty("mScaleByStatProgressionMultiplier", out var sp) && sp.ValueKind == JsonValueKind.True;
                    return Value.Levelled(ranks, l => from + (to - from) * (curve ? ChampionMath.Growth(l) / 17 : (l - 1) / 17.0));
                }
                case "ByCharLevelBreakpointsCalculationPart":
                    return Value.Levelled(ranks, Breakpoints(part));
                case "ByCharLevelFormulaCalculationPart":
                {
                    if (!part.TryGetProperty("values", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2) return null;
                    var all = arr.EnumerateArray().Select(x => x.TryGetDouble(out var d) ? d : 0).ToList();
                    // 19+ entries are indexed by level (entry 0 unused); otherwise entry 0 is level 1.
                    return Value.Levelled(ranks, l => all.Count > Levels ? all[Math.Min(l, all.Count - 1)] : all[Math.Min(l - 1, all.Count - 1)]);
                }
                case "ClampSubPartsCalculationPart":
                {
                    if (!part.TryGetProperty("mSubparts", out var subs) || subs.ValueKind != JsonValueKind.Array) return null;
                    Value? total = Value.Const(ranks, 0);
                    foreach (var s in subs.EnumerateArray())
                    {
                        if (Part(s) is not { } v) return null;
                        total = Value.Add(total!, v);
                        if (total is null) return null;
                    }
                    var floor = part.TryGetProperty("mFloor", out var f) && f.TryGetDouble(out var fv) ? fv : double.MinValue;
                    var ceiling = part.TryGetProperty("mCeiling", out var ce) && ce.TryGetDouble(out var cv) ? cv : double.MaxValue;
                    if (total!.Ratios.Count > 0) return null;
                    if (total.ByLevel is null)
                    {
                        for (var r = 0; r < total.Base.Length; r++) total.Base[r] = Math.Clamp(total.Base[r], floor, ceiling);
                        return total;
                    }
                    if (total.Base.Any(b => Math.Abs(b) > 1e-9)) return null;
                    for (var l = 0; l < total.ByLevel.Length; l++) total.ByLevel[l] = Math.Clamp(total.ByLevel[l], floor, ceiling);
                    return total;
                }
                default:
                    return null;
            }
        }

        private Value? Ratio(JsonElement part, double[] coefficient)
        {
            var stat = part.TryGetProperty("mStat", out var s) && s.TryGetInt32(out var st) ? st : 0;
            var formula = part.TryGetProperty("mStatFormula", out var f) && f.TryGetInt32(out var fo) ? fo : 0;
            if (StatName(stat, formula) is null) return null;
            var value = new Value(ranks);
            value.Ratios.Add((stat, formula, coefficient));
            return value;
        }

        /// <summary>Value at each level 1–18: a level-1 value plus a bonus per level that can change at breakpoints.</summary>
        private static Func<int, double> Breakpoints(JsonElement part)
        {
            var values = new double[Levels];
            var value = Num(part, "mLevel1Value");
            var perLevel = Num(part, "mInitialBonusPerLevel");
            var breakpoints = part.TryGetProperty("mBreakpoints", out var bps) && bps.ValueKind == JsonValueKind.Array ? bps.EnumerateArray().ToList() : [];
            values[0] = value;
            for (var level = 2; level <= Levels; level++)
            {
                foreach (var bp in breakpoints.Where(b => b.TryGetProperty("mLevel", out var l) && l.TryGetInt32(out var lv) && lv == level))
                {
                    value += Num(bp, "mAdditionalBonusAtThisLevel");
                    if (bp.TryGetProperty("mBonusPerLevelAtAndAfter", out var per) && per.TryGetDouble(out var p)) perLevel = p;
                }
                value += perLevel;
                values[level - 1] = value;
            }
            return l => values[l - 1];
        }

        private double[]? DataValue(string? name) => name is null ? null : dataValue(name) is { } v ? Fit(v) : null;

        /// <summary>Rune data values are a single number; spread it over every rank.</summary>
        private double[] Fit(double[] values) =>
            values.Length == ranks ? values : Enumerable.Range(0, ranks).Select(r => values[Math.Min(r, values.Length - 1)]).ToArray();
    }

    /// <summary>A named data value of a spell, per rank (index 1 = rank 1).</summary>
    private static double[]? SpellDataValue(JsonElement spell, string name, int ranks)
    {
        foreach (var key in new[] { "DataValues", "mDataValues" })
        {
            if (!spell.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var dv in list.EnumerateArray())
            {
                var n = Str(dv, "name") ?? Str(dv, "mName");
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue; // the game matches names case-insensitively
                var values = dv.TryGetProperty("values", out var v) ? v : dv.TryGetProperty("mValues", out var mv) ? mv : default;
                return ByRank(values, ranks);
            }
        }
        return null;
    }

    private static double[]? EffectValue(JsonElement spell, int index, int ranks)
    {
        if (index <= 0 || !spell.TryGetProperty("mEffectAmount", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() < index) return null;
        var effect = list[index - 1];
        return ByRank(effect.TryGetProperty("value", out var v) ? v : default, ranks);
    }

    private static double[] ByRank(JsonElement values, int ranks)
    {
        var result = new double[ranks];
        if (values.ValueKind != JsonValueKind.Array) return result; // omitted = all zero
        var all = values.EnumerateArray().Select(x => x.TryGetDouble(out var d) ? d : 0).ToList();
        for (var r = 1; r <= ranks; r++)
            result[r - 1] = all.Count == 0 ? 0 : all[Math.Min(r, all.Count - 1)];
        return result;
    }

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stat names by the game's stat number (only the ones identified with certainty).</summary>
    private static string? StatName(int stat, int formula)
    {
        var name = stat switch
        {
            0 => "AP",
            1 => "armor",
            2 => "AD",
            4 => "attack speed",
            6 => "magic resist",
            7 => "move speed",
            8 => "crit damage",
            9 => "crit chance",
            12 => formula == 0 ? "max health" : "health",
            29 => "lethality",
            _ => null,
        };
        if (name is null) return null;
        return formula switch
        {
            1 => "base " + name,
            2 => "bonus " + name,
            _ => name,
        };
    }

    private static string? Format(Value value, bool percent, int? precision)
    {
        var scale = percent ? 100 : 1;
        var hasBase = value.Base.Any(b => Math.Abs(b) > 1e-9);
        var ratios = value.Ratios.Where(r => r.Coefficient.Any(c => Math.Abs(c) > 1e-9)).ToList();
        if (!hasBase && ratios.Count == 0 && value.ByLevel is null) return null;
        var unit = percent ? "%" : "";
        var parts = new List<string>();
        if (value.ByLevel is { Length: > 0 } range)
        {
            var levelText = $"{Number(range[0] * scale, precision)}{unit}–{Number(range[^1] * scale, precision)}{unit} (by level)";
            parts.Add(hasBase ? $"{Series(value.Base, scale, precision)}{unit} + {levelText}" : levelText);
        }
        else if (hasBase || ratios.Count == 0)
        {
            parts.Add(Series(value.Base, scale, precision) + unit);
        }
        foreach (var (stat, formula, coef) in ratios)
        {
            var name = StatName(stat, formula)!;
            var negative = coef.All(c => c <= 0);
            var magnitude = negative ? coef.Select(c => -c).ToArray() : coef;
            // Inside a percentage, a ratio to a flat stat reads "1% per 100 AP"; otherwise (and for stats that are
            // themselves fractions, like crit chance) "50% AP".
            var text = percent && !IsFractionStat(stat)
                ? $"{Series(magnitude, 100 * 100, 2)}% per 100 {name}"
                : $"{Series(magnitude, 100, 1)}% {name}";
            parts.Add(parts.Count == 0 ? (negative ? "−" : "") + text : $"({(negative ? "−" : "+")}{text})");
        }
        if (Math.Abs(value.RangedMultiplier - 1) > 1e-6) parts.Add($"(ranged: ×{Number(value.RangedMultiplier, 2)})");
        return string.Join(" ", parts);
    }

    /// <summary>Attack speed, crit damage and crit chance are stored as fractions (0.25 = 25 %).</summary>
    private static bool IsFractionStat(int stat) => stat is 4 or 8 or 9;

    private static string Series(double[] values, double scale, int? precision)
    {
        var texts = values.Select(v => Number(v * scale, precision)).ToList();
        return texts.Distinct().Count() == 1 ? texts[0] : string.Join("/", texts);
    }

    private static string Number(double v, int? precision)
    {
        var digits = Math.Clamp(precision ?? 2, 0, 3);
        var rounded = Math.Round(v, digits, MidpointRounding.AwayFromZero);
        return rounded.ToString("0." + new string('#', Math.Max(digits, 0)), CultureInfo.InvariantCulture).TrimEnd('.');
    }

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
