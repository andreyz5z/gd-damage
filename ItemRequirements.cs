// What a character needs to wear an item.
//
// The level requirement is a plain field of the item record: exact, and the only
// one the editor acts on.
//
// The attribute requirements are not stored anywhere. The game computes them
// from equations that live in the database as text — one set per item tier,
// picked by the item's own "itemCostName" field, keyed by the item's class. The
// machinery below evaluates those equations, and it is deliberately NOT used for
// display or for refusing to equip, because the numbers it produces are too high:
// run `--reqtest`, and 81 of the 166 items the sample characters are actually
// wearing come out unwearable, even after counting the attributes their own gear
// grants. Something scales them down that has not been found yet. Until
// `--reqtest` reports no over-statements, these numbers must not be shown as
// fact.
//
// Kept rather than deleted because the archaeology is the expensive part: the
// equations are in records/game/itemcostformulas*.dbr, their variables are
// itemLevel, damageAvgBase, damageAvgPierceRatio, defenseAttrArmor,
// shieldBlockDefense, shieldBlockChance, charAttackSpeed and totalAttCount, and
// the class-to-equation mapping is transcribed from GD Stash.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    // A tiny expression evaluator for the equation text the game ships, e.g.
    //   (2.1*(itemLevel*6.55)^1.25-1.80*(itemLevel*5.84)^1.2785+((itemLevel^1.5)*0.0125-1)*-5)*0.98+12
    // Power binds tighter than unary minus and is right associative, matching the
    // library GD Stash uses on the same strings.
    public class Equation
    {
        readonly string s;
        readonly Func<string, double> vars;
        int p;

        Equation(string text, Func<string, double> lookup) { s = text; vars = lookup; }

        public static bool TryEval(string text, Func<string, double> lookup, out double result)
        {
            result = 0;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                Equation e = new Equation(text, lookup);
                double v = e.Sum();
                e.Space();
                if (e.p != e.s.Length) return false;
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
                result = v;
                return true;
            }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        void Space() { while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++; }
        bool Take(char c) { Space(); if (p < s.Length && s[p] == c) { p++; return true; } return false; }

        double Sum()
        {
            double v = Term();
            while (true)
            {
                if (Take('+')) v += Term();
                else if (Take('-')) v -= Term();
                else return v;
            }
        }

        double Term()
        {
            double v = Unary();
            while (true)
            {
                if (Take('*')) v *= Unary();
                else if (Take('/')) v /= Unary();
                else return v;
            }
        }

        double Unary()
        {
            Space();
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            return Power();
        }

        double Power()
        {
            double b = Primary();
            if (Take('^')) return Math.Pow(b, Unary());
            return b;
        }

        double Primary()
        {
            Space();
            if (Take('('))
            {
                double v = Sum();
                if (!Take(')')) throw new FormatException("missing closing parenthesis");
                return v;
            }
            int start = p;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
            if (p > start) return double.Parse(s.Substring(start, p - start), CultureInfo.InvariantCulture);
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
            if (p > start) return vars(s.Substring(start, p - start));
            throw new FormatException("unrecognized character");
        }
    }

    public class Requirements
    {
        public int Level, Physique, Cunning, Spirit;
        // False when the item's class has no equations, or the equation text
        // could not be evaluated: then the attribute numbers mean nothing and
        // must not be shown or enforced.
        public bool AttributesKnown;

        public bool Any { get { return Level > 0 || Physique > 0 || Cunning > 0 || Spirit > 0; } }
    }

    public static class ItemRequirements
    {
        // Which equation feeds which attribute, per record class. This mapping
        // exists nowhere in the game data; it is transcribed from GD Stash, which
        // is the only published reading of it.
        static readonly Dictionary<string, string[]> Equations = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // class -> { physique, cunning, spirit }
            { "ArmorProtective_Head",      new[] { "headStrengthEquation", null, "headIntelligenceEquation" } },
            { "ArmorProtective_Shoulders", new[] { "shouldersStrengthEquation", null, null } },
            { "ArmorProtective_Chest",     new[] { "chestStrengthEquation", null, "chestIntelligenceEquation" } },
            { "ArmorProtective_Hands",     new[] { "handsStrengthEquation", null, null } },
            { "ArmorProtective_Legs",      new[] { "legsStrengthEquation", null, null } },
            { "ArmorProtective_Feet",      new[] { "feetStrengthEquation", null, null } },
            { "ArmorProtective_Waist",     new[] { "waistStrengthEquation", null, null } },
            { "ArmorJewelry_Amulet",       new[] { null, null, "amuletIntelligenceEquation" } },
            { "ArmorJewelry_Medal",        new[] { null, null, "amuletIntelligenceEquation" } },
            { "ArmorJewelry_Ring",         new[] { null, null, "ringIntelligenceEquation" } },
            { "WeaponArmor_Offhand",       new[] { null, null, "offhandIntelligenceEquation" } },
            { "WeaponArmor_Shield",        new[] { "shieldStrengthEquation", null, null } },
            { "WeaponMelee_Axe",           new[] { "axeStrengthEquation", null, null } },
            { "WeaponMelee_Mace",          new[] { "maceStrengthEquation", null, null } },
            { "WeaponHunting_Spear",       new[] { "maceStrengthEquation", null, null } },
            { "WeaponMelee_Sword",         new[] { null, "swordDexterityEquation", null } },
            { "WeaponMelee_Dagger",        new[] { null, "daggerDexterityEquation", "daggerIntelligenceEquation" } },
            { "WeaponMelee_Scepter",       new[] { "scepterStrengthEquation", null, "scepterIntelligenceEquation" } },
            { "WeaponHunting_Ranged1h",    new[] { null, "ranged1hDexterityEquation", null } },
            { "WeaponMelee_Axe2h",         new[] { "melee2hStrengthEquation", null, null } },
            { "WeaponMelee_Mace2h",        new[] { "melee2hStrengthEquation", null, null } },
            { "WeaponMelee_Spear2h",       new[] { "maceStrengthEquation", null, null } },
            { "WeaponMagical_Staff",       new[] { null, "daggerDexterityEquation", "daggerIntelligenceEquation" } },
            { "WeaponMelee_Sword2h",       new[] { "melee2hStrengthEquation", null, null } },
            { "WeaponHunting_Ranged2h",    new[] { null, "ranged2hDexterityEquation", null } },
        };

        const string DefaultSet = "records/game/itemcostformulas.dbr";

        public static Requirements For(GameData game, DbRecord item)
        {
            Requirements r = new Requirements();
            if (item == null) return r;
            r.Level = item.GetInt("levelRequirement");

            // An explicit requirement field overrides the formula.
            // Most items derive requirements from `itemCostName`; when a record
            // contains an explicit value, use it and keep the formula as fallback.
            int str = item.GetInt("strengthRequirement");
            int dex = item.GetInt("dexterityRequirement");
            int intl = item.GetInt("intelligenceRequirement");

            string[] eq;
            if (game == null || item.Class == null || !Equations.TryGetValue(item.Class, out eq))
            {
                if (str > 0 || dex > 0 || intl > 0)
                {
                    r.Physique = str; r.Cunning = dex; r.Spirit = intl;
                    r.AttributesKnown = true;
                }
                return r;
            }
            string setId = item.Get("itemCostName");
            if (string.IsNullOrEmpty(setId)) setId = DefaultSet;
            DbRecord formulas = game.Record(setId);
            if (formulas == null) return r;

            Func<string, double> vars = delegate (string name) { return Variable(item, name); };
            bool ok = true;
            r.Physique = Solve(formulas, eq[0], vars, ref ok);
            r.Cunning = Solve(formulas, eq[1], vars, ref ok);
            r.Spirit = Solve(formulas, eq[2], vars, ref ok);
            r.AttributesKnown = ok;
            // An explicit record field overrides the formula described above.
            if (str > 0) { r.Physique = str; r.AttributesKnown = true; }
            if (dex > 0) { r.Cunning = dex; r.AttributesKnown = true; }
            if (intl > 0) { r.Spirit = intl; r.AttributesKnown = true; }
            return r;
        }

        static int Solve(DbRecord formulas, string key, Func<string, double> vars, ref bool ok)
        {
            if (key == null) return 0;
            string text = formulas.Get(key);
            if (string.IsNullOrEmpty(text)) return 0;   // this tier simply has no such requirement
            double v;
            if (!Equation.TryEval(text, vars, out v)) { ok = false; return 0; }
            if (v <= 0) return 0;
            return (int)(v + 0.5);
        }

        // The damage types the game sums into the "average base damage" the
        // weapon equations use.
        static readonly string[] DamageFields = {
            "offensivePhysical", "offensiveFire", "offensiveCold", "offensiveLightning",
            "offensivePoison", "offensiveLife", "offensiveAether", "offensiveChaos" };

        static double Variable(DbRecord item, string name)
        {
            switch (name)
            {
                case "itemLevel": return item.GetInt("itemLevel");
                case "defenseAttrArmor": return (int)Num(item, "defensiveProtection");
                case "shieldBlockDefense": return (int)Num(item, "defensiveBlock");
                case "shieldBlockChance": return (int)Num(item, "defensiveBlockChance");
                case "charAttackSpeed": return Num(item, "characterAttackSpeedModifier") / 100.0;
                case "damageAvgPierceRatio": return AvgPair(item, "offensivePierceRatio");
                case "damageAvgBase":
                    {
                        int total = 0;
                        for (int i = 0; i < DamageFields.Length; i++) total += AvgPair(item, DamageFields[i]);
                        return total;
                    }
                case "totalAttCount": return StatCount(item);
                // Only the cost equations use these, and cost is not shown.
                case "itemBaseCost": return 1000;
                case "itemPrefixCost": return 0;
                case "itemSuffixCost": return 0;
                default: return 0;
            }
        }

        static int AvgPair(DbRecord rec, string prefix)
        {
            int min = (int)Num(rec, prefix + "Min");
            int max = (int)Num(rec, prefix + "Max");
            if (max > 0) min = (min + max) / 2;
            return min;
        }

        static double Num(DbRecord rec, string field)
        {
            string v = rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            int semi = v.IndexOf(';');
            if (semi >= 0) v = v.Substring(0, semi);
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        static readonly string[] StatPrefixes = { "offensive", "defensive", "character", "retaliation", "racial", "skill" };

        // Number of the item's non-zero bonuses; only the jewelry equations use
        // it, and only as a small correction term.
        static int StatCount(DbRecord rec)
        {
            int n = 0;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                bool statish = false;
                for (int i = 0; i < StatPrefixes.Length; i++)
                    if (kv.Key.StartsWith(StatPrefixes[i], StringComparison.Ordinal)) { statish = true; break; }
                if (!statish) continue;
                double d;
                if (double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) n++;
            }
            return n;
        }

        // What the character brings to the comparison.
        public class Have
        {
            public int Level, Physique, Cunning, Spirit;

            public static Have Of(Character c)
            {
                Have h = new Have();
                if (c == null) return h;
                h.Level = c.Level;
                h.Physique = (int)c.Physique;
                h.Cunning = (int)c.Cunning;
                h.Spirit = (int)c.Spirit;
                return h;
            }
        }

        // What the character falls short of, worded for the status bar. Only the
        // level, because only the level is a number the game actually stores.
        public static List<string> Unmet(Requirements need, Have have)
        {
            List<string> missing = new List<string>();
            if (need == null || have == null) return missing;
            if (need.Level > have.Level) missing.Add(Lang.T("main.need.level", need.Level, have.Level));
            return missing;
        }

        // The computed attribute shortfalls, for --reqtest only. Do not put these
        // in front of the user until the test comes back clean.
        public static List<string> UnmetAttributes(Requirements need, Have have)
        {
            List<string> missing = new List<string>();
            if (need == null || have == null || !need.AttributesKnown) return missing;
            if (need.Physique > have.Physique) missing.Add("Physique " + need.Physique + " (have " + have.Physique + ")");
            if (need.Cunning > have.Cunning) missing.Add("Cunning " + need.Cunning + " (have " + have.Cunning + ")");
            if (need.Spirit > have.Spirit) missing.Add("Spirit " + need.Spirit + " (have " + have.Spirit + ")");
            return missing;
        }
    }
}
