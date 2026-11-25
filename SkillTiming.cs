using System;
using Gdc;

namespace Gdc.Gui
{
    // Skill cooldown and cost calculations shared by tooltips and the timeline.
    //
    // The formula used to be split between the item tooltip and the skills window.
    // Keeping it here lets the timeline use the same values without UI dependencies.
    //
    // Keep calculation logic outside presentation code so all views stay consistent.
    //
    // This is a shared calculation, not a second set of rules.
    public static class SkillTiming
    {
        // Character cooldown reduction, in percent. The game displays the reduced value.
        public static double CooldownCut(CharacterTotals totals)
        {
            TotalsEntry e;
            return totals != null && totals.Fields.TryGetValue("skillCooldownReduction", out e)
                 ? e.Value : 0;
        }

        // Cooldown and cost modifiers from linked tree skills and equipped items.
        //
        // Resolve links through the game field and the parent connector flag;
        // do not infer them from tree position.
        public static void Mods(GameData game, Character c, AttackSkills.Attach attach,
                                ItemSkillMods itemMods, SkillText.LevelBonus bonus,
                                string dbr, out double cdAdd, out double manaCut)
        {
            cdAdd = 0; manaCut = 0;
            if (game == null || c == null || string.IsNullOrEmpty(dbr)) return;
            try
            {
                if (attach == null) attach = AttackSkills.Attach.Of(game, c);
                foreach (Skill s in c.Skills)
                {
                    if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                    if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    DbRecord rec = game.Record(s.Name);
                    if (rec == null) continue;
                    if (string.IsNullOrEmpty(rec.Get("skillCooldownTime")) &&
                        string.IsNullOrEmpty(rec.Get("skillManaCostReduction"))) continue;
                    string parent = null;
                    foreach (string one in attach.Parents(game, s.Name)) { parent = one; break; }
                    if (string.IsNullOrEmpty(parent)) parent = attach.Extra(s.Name);
                    if (!string.Equals(parent, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    int at = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                    int slot = SkillText.Slot(rec, at);
                    cdAdd += AttackSkill.Value(rec, "skillCooldownTime", slot);
                    manaCut += AttackSkill.Value(rec, "skillManaCostReduction", slot);
                }
                if (itemMods == null) itemMods = ItemSkillMods.Of(game, c);
                cdAdd += itemMods.Sum(dbr, "skillCooldownTime");
                manaCut += itemMods.Sum(dbr, "skillManaCostReduction");
            }
            // A failed tree walk must not break the tooltip or timeline.
            catch (Exception) { }
        }

        // COOLDOWN CHARGES AND THE CHANCE TO RESET THE COOLDOWN — two fields the queue did
        // not read at all until 16 October (MECHANICS 151).
        //
        // BOTH SIT ON MODIFIERS, NOT ON THE SKILL ITSELF. Aegis of Menhir carries neither,
        // while its Reprisal modifier holds `cooldownCharges 1;1;…;2` and the pair
        // `skillCooldownReduction 100` + `skillCooldownReductionChance 8;11;…;30`, which the
        // game prints as "Charges: +2" and "30% chance of −100% skill cooldown". So the walk
        // is the one `Mods` above already makes: the skill's own chain of records, the tree
        // modifiers tied to it, and the item modifiers.
        //
        // CHARGES ADD UP, AND THE SKILL'S OWN ONE IS IMPLIED: the game prints "Charges: 3"
        // for a skill whose only source says "+2", so the base is one use, as for any skill.
        //
        // THE CHANCE IS TAKEN AS THE LARGEST OF THE SOURCES, NOT SUMMED, and that is said
        // honestly: two sources each rolling their own die are not one die of the sum, and
        // nothing measures two such sources at once. This character has exactly one, so
        // today the two readings give the same number.
        public static void Charges(GameData game, Character c, AttackSkills.Attach attach,
                                   ItemSkillMods itemMods, SkillText.LevelBonus bonus,
                                   string dbr, int level, out int charges, out double resetChance)
        {
            charges = 1; resetChance = 0;
            if (game == null || c == null || string.IsNullOrEmpty(dbr)) return;
            try
            {
                // The skill's own chain first — a field may sit on a child record, as the
                // cooldown does.
                {
                    string at = dbr;
                    for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
                    {
                        DbRecord r = game.Record(at);
                        if (r == null) break;
                        int slot = SkillText.Slot(r, level);
                        charges += (int)Math.Round(AttackSkill.Value(r, "cooldownCharges", slot));
                        double ch = AttackSkill.Value(r, "skillCooldownReductionChance", slot);
                        if (AttackSkill.Value(r, "skillCooldownReduction", slot) > 0.0001 && ch > resetChance)
                            resetChance = ch;
                        at = ItemViewFactory.NextSkillRecord(r);
                    }
                }
                if (attach == null) attach = AttackSkills.Attach.Of(game, c);
                foreach (Skill s in c.Skills)
                {
                    if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                    if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    DbRecord rec = game.Record(s.Name);
                    if (rec == null) continue;
                    if (string.IsNullOrEmpty(rec.Get("cooldownCharges")) &&
                        string.IsNullOrEmpty(rec.Get("skillCooldownReductionChance"))) continue;
                    string parent = null;
                    foreach (string one in attach.Parents(game, s.Name)) { parent = one; break; }
                    if (string.IsNullOrEmpty(parent)) parent = attach.Extra(s.Name);
                    if (!string.Equals(parent, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    int at2 = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                    int slot = SkillText.Slot(rec, at2);
                    charges += (int)Math.Round(AttackSkill.Value(rec, "cooldownCharges", slot));
                    double ch = AttackSkill.Value(rec, "skillCooldownReductionChance", slot);
                    if (AttackSkill.Value(rec, "skillCooldownReduction", slot) > 0.0001 && ch > resetChance)
                        resetChance = ch;
                }
                if (itemMods == null) itemMods = ItemSkillMods.Of(game, c);
                foreach (ItemSkillMods.Entry e in itemMods.For(dbr))
                {
                    charges += (int)Math.Round(AttackSkill.Value(e.Modifier, "cooldownCharges", 0));
                    double ch = AttackSkill.Value(e.Modifier, "skillCooldownReductionChance", 0);
                    if (AttackSkill.Value(e.Modifier, "skillCooldownReduction", 0) > 0.0001 && ch > resetChance)
                        resetChance = ch;
                }
            }
            // Neither the tooltip nor the run may fall over a tree walk — without the
            // modifiers the plain one use per cooldown stands, as it did before.
            catch (Exception) { }
            if (charges < 1) charges = 1;
            if (resetChance < 0) resetChance = 0;
            if (resetChance > 95) resetChance = 95;   // a full reset every time is not modelled
        }

        // Duration of the skill effect, from `skillActiveDuration`.
        //
        // The field also distinguishes buffs from attacks and is needed to model
        // effects that expire before the next cooldown.
        //
        // Search the record chain until the first record containing the field.
        public static double Duration(GameData game, string dbr, int level)
        {
            string at = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game == null ? null : game.Record(at);
                if (r == null) break;
                double v = AttackSkill.Value(r, "skillActiveDuration", SkillText.Slot(r, level));
                if (v > 0.0001) return v;
                at = ItemViewFactory.NextSkillRecord(r);
            }
            return 0;
        }

        // Skill cooldown in seconds, using the game's calculation order:
        //
        //     (record value + modifiers) × (100 − character reduction) / 100
        //
        // Add modifiers first, then apply the character reduction. Zero means no cooldown.
        public static double Cooldown(GameData game, Character c, CharacterTotals totals,
                                      AttackSkills.Attach attach, ItemSkillMods itemMods,
                                      SkillText.LevelBonus bonus, string dbr, int level)
        {
            DbRecord rec = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            if (rec == null) return 0;
            // The cooldown may be stored below the top-level skill record.
            //
            // Follow the same record chain used for names, damage, and limits.
            // Recompute the level slot for each record because child tables may differ.
            double own = 0;
            {
                string at = dbr;
                for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
                {
                    DbRecord r = game.Record(at);
                    if (r == null) break;
                    double got = AttackSkill.Value(r, "skillCooldownTime", SkillText.Slot(r, level));
                    if (got > 0.0001) { own = got; break; }
                    at = ItemViewFactory.NextSkillRecord(r);
                }
            }
            double cdAdd, manaCut;
            Mods(game, c, attach, itemMods, bonus, dbr, out cdAdd, out manaCut);
            double cut = CooldownCut(totals);
            double v = (own + cdAdd) * (100 - cut) / 100.0;
            return v < 0 ? 0 : v;
        }
    }
}
