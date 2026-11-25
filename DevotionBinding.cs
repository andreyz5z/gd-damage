// WHICH SKILL A CELESTIAL POWER HANGS ON, AND WHICH SKILLS IT MAY HANG ON.
//
// A star that grants a celestial power does nothing until it is tied to a skill of
// the character's own: the game then fires the power when that skill fires. The tie
// lives on the SKILL's entry in the save, not on the star's:
//
//     Skill { Name = records/skills/playerclass04/eviscerate1.dbr, Level = 16,
//             AutoCastSkill      = records/skills/devotion/tier1_02e_skill.dbr,
//             AutoCastController = records/controllers/itemskills/cast_@enemyonattack_25%.dbr }
//
// NOT EVERY SKILL MAY CARRY EVERY POWER, and the game says which in the power's own
// record:
//
//     records/skills/devotion/tier1_02e_skill.dbr
//         skillTemplates    records/skills/base_template skills/skill_attackprojectile.dbr; … (58 of them)
//         skillBlackList    records/skills/itemskillsgdx2/runes/teleport_00_basic.dbr; …
//         templateAutoCast  records/controllers/itemskills/cast_@enemyonattack_25%.dbr
//
// `skillTemplates` names BASE-TEMPLATE RECORDS, and each of those carries the
// `templateName` an ordinary skill is built from —
// `skill_attackprojectilering.dbr` → `database/templates/skill_attackprojectilering.tpl`.
// So a skill fits a power when its own `templateName` is among those, and its record
// is not named in `skillBlackList`. That is exactly the test GrimTools' calculator
// makes (`f6Pe` in `calc.js`), and both sides of it are the game's own fields.
//
// `templateAutoCast` is the controller the save has to carry: it is what says when
// the power fires — "25% on attacking an enemy", "at 45% health", and so on.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    // One line of the chooser: a skill the power could be tied to, and everything
    // that decides how it is shown.
    public sealed class PowerTarget
    {
        public string Dbr = "", Name = "";
        public bool Learned;       // the character has points in it
        public bool Current;       // this power hangs on it now
        public string TakenBy;     // another power already hangs on it, named
        public bool FromItem;      // the skill comes from worn gear, not the tree
        public bool None;          // the "no skill" line at the top
    }

    public static class DevotionBinding
    {
        // `--devobindbreak` — read `skillTemplates` without going into the
        // base-template records, so the check that guards the ties can be seen going
        // red (PLAN, rule 12).
        public static bool BreakTemplates;

        // `--devoshapebreak` — write a skill a shapeshift grants the ordinary way
        // (level 0, `Enabled 1`), so the check that reads the entry back can be seen
        // going red (PLAN, rule 12).
        public static bool BreakShapeshift;

        // The `.tpl` names a power will accept, read through the base-template
        // records it points at. Held per database: one power names some sixty of
        // them, and the chooser asks for every skill of both masteries.
        static GameData templatesFor;
        static readonly Dictionary<string, List<string>> templateCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static List<string> TemplatesOf(GameData game, string powerDbr)
        {
            if (game != templatesFor) { templateCache.Clear(); templatesFor = game; }
            List<string> made;
            if (templateCache.TryGetValue(powerDbr ?? "", out made)) return made;
            made = new List<string>();
            DbRecord rec = Holding(game, powerDbr, "skillTemplates");
            foreach (string one in ((rec == null ? null : rec.Get("skillTemplates")) ?? "").Split(';'))
            {
                if (string.IsNullOrEmpty(one)) continue;
                // `--devobindbreak` — do NOT follow `templateName` into the
                // base-template record, and compare against that record's path
                // instead: exactly the mistake that is easy to make without looking
                // inside those records. The check has to go red on it.
                if (BreakTemplates) { if (!made.Contains(one.Trim())) made.Add(one.Trim()); continue; }
                DbRecord basis = game.Record(one.Trim());
                string tpl = basis == null ? null : basis.Get("templateName");
                if (!string.IsNullOrEmpty(tpl) && !made.Contains(tpl)) made.Add(tpl);
            }
            templateCache[powerDbr ?? ""] = made;
            return made;
        }

        public static List<string> BlackListOf(GameData game, string powerDbr)
        {
            List<string> made = new List<string>();
            DbRecord rec = Holding(game, powerDbr, "skillBlackList");
            foreach (string one in ((rec == null ? null : rec.Get("skillBlackList")) ?? "").Split(';'))
                if (!string.IsNullOrEmpty(one)) made.Add(one.Trim());
            return made;
        }

        // The controller the save writes with this tie: it is what says when the
        // power fires.
        public static string ControllerOf(GameData game, string powerDbr)
        {
            DbRecord rec = Holding(game, powerDbr, "templateAutoCast");
            return rec == null ? null : rec.Get("templateAutoCast");
        }

        // THE RECORD OF THE HAND-OFF CHAIN THAT ACTUALLY CARRIES A FIELD. A star can
        // point at a record that is only a shell; such records have no usable data and
        // holds neither `skillTemplates` nor `templateAutoCast`; both sit on the buff
        // it hands off to. Reading the named record alone called 43 of the 147
        // bindings the GAME ITSELF had written illegal, which is how this was found.
        // The same chain the name, the icon and the level limits already walk.
        static DbRecord Holding(GameData game, string dbr, string field)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(dbr);
                if (rec == null) return null;
                if (!string.IsNullOrEmpty(rec.Get(field))) return rec;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        public static bool Fits(GameData game, string powerDbr, string skillDbr)
        {
            if (game == null || string.IsNullOrEmpty(skillDbr)) return false;
            foreach (string black in BlackListOf(game, powerDbr))
                if (string.Equals(black, skillDbr, StringComparison.OrdinalIgnoreCase)) return false;
            DbRecord skill = game.Record(skillDbr);
            string tpl = skill == null ? null : skill.Get("templateName");
            if (string.IsNullOrEmpty(tpl)) return false;
            foreach (string want in TemplatesOf(game, powerDbr))
                if (string.Equals(want, tpl, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The skill this power hangs on now, or null.
        public static string BoundTo(Character c, string powerDbr)
        {
            if (c == null) return null;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.AutoCastSkill, powerDbr, StringComparison.OrdinalIgnoreCase)) return s.Name;
            foreach (ItemSkill s in c.ItemSkills)
                if (string.Equals(s.AutoCastSkill, powerDbr, StringComparison.OrdinalIgnoreCase)) return s.SkillName;
            return null;
        }

        // The power that already hangs on this skill, or null.
        public static string PowerOn(Character c, string skillDbr)
        {
            if (c == null) return null;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, skillDbr, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(s.AutoCastSkill)) return s.AutoCastSkill;
            foreach (ItemSkill s in c.ItemSkills)
                if (string.Equals(s.SkillName, skillDbr, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(s.AutoCastSkill)) return s.AutoCastSkill;
            return null;
        }

        // EVERY SKILL THIS POWER MAY HANG ON, in the order the trees list them: the
        // skills of both masteries the character took, then the ones worn gear
        // grants. A skill the character has NOT learned is still offered — the game
        // offers it too, in red — because a tie made now keeps working once the
        // points go in.
        public static List<PowerTarget> Choices(GameData game, Character c, string powerDbr)
        {
            List<PowerTarget> made = new List<PowerTarget>();
            if (game == null || c == null) return made;
            // "No skill assigned" — the line and the icon are the GAME's, not ours:
            // `devotion_skillselect.dbr` → `pickTemplate` names both (`displayTag`,
            // `displayBitmap`).
            made.Add(new PowerTarget { None = true, Name = UnassignedName(game) });

            List<string> seen = new List<string>();
            foreach (int number in MasteryEdit.Taken(c))
                foreach (string dbr in SkillsOf(game, number))
                {
                    if (seen.Contains(dbr) || !Fits(game, powerDbr, dbr)) continue;
                    seen.Add(dbr);
                    made.Add(One(game, c, powerDbr, dbr, false));
                }
            foreach (ItemSkill s in c.ItemSkills)
            {
                if (string.IsNullOrEmpty(s.SkillName) || seen.Contains(s.SkillName)) continue;
                if (!Fits(game, powerDbr, s.SkillName)) continue;
                seen.Add(s.SkillName);
                made.Add(One(game, c, powerDbr, s.SkillName, true));
            }
            return made;
        }

        // The record that dresses the chooser: the game's own "no skill" line, its
        // icon, and how many rows it puts in a column.
        public static DbRecord PickTemplate(GameData game)
        {
            DbRecord window = game == null ? null : game.Record("records/ui/skills/devotion/devotion_skillselect.dbr");
            return window == null ? null : game.Record(window.Get("pickTemplate") ?? "");
        }

        public static string UnassignedIcon(GameData game)
        {
            DbRecord pick = PickTemplate(game);
            return pick == null ? null : pick.Get("displayBitmap");
        }

        public static string UnassignedName(GameData game)
        {
            DbRecord pick = PickTemplate(game);
            string text = pick == null ? null : game.Tag(pick.Get("displayTag"));
            return string.IsNullOrEmpty(text) ? Lang.T("skills.bind.none") : GameData.Form(text, null);
        }

        // EVERY SKILL OF A MASTERY, INCLUDING THE ONES A SHAPESHIFT BRINGS WITH IT.
        //
        // The tree's own buttons are not the whole list: some skills are granted by
        // Berserker grants two skills of its own, and the game offers both of them
        // for a celestial power. They are not in any `classNN/skillNN.dbr` — the
        // shapeshift names them itself:
        //
        //     records/skills/playerclass10/werewolf1.dbr   class=Skill_Shapeshift
        //         grantedSkills  …/werewolf1_skill01_claws.dbr;…/werewolf1_skill02_charge.dbr
        //
        // Noticed by the user against the game's own chooser, which listed four
        // skills ours did not.
        public static List<string> SkillsOf(GameData game, int mastery)
        {
            List<string> made = new List<string>();
            foreach (string dbr in AttackSkills.Attach.SkillsOfFolder(game, "playerclass" + mastery.ToString("00")))
            {
                if (!made.Contains(dbr)) made.Add(dbr);
                DbRecord shape = Holding(game, dbr, "grantedSkills");
                if (shape == null) continue;
                foreach (string one in (shape.Get("grantedSkills") ?? "").Split(';'))
                    if (!string.IsNullOrEmpty(one) && !made.Contains(one.Trim())) made.Add(one.Trim());
            }
            return made;
        }

        // THE LEVEL OF THE SHAPESHIFT THAT GRANTS THIS SKILL, or −1 when no shapeshift
        // does. In `_FenrisWerwolf` the shapeshift stands at 16 and both skills it
        // grants carry 16 in their own entries, so the game keeps the two in step.
        public static int GrantedLevel(GameData game, Character c, string skillDbr)
        {
            if (game == null || c == null || string.IsNullOrEmpty(skillDbr)) return -1;
            foreach (int number in MasteryEdit.Taken(c))
                foreach (string tree in AttackSkills.Attach.SkillsOfFolder(game, "playerclass" + number.ToString("00")))
                {
                    DbRecord shape = Holding(game, tree, "grantedSkills");
                    if (shape == null) continue;
                    foreach (string one in (shape.Get("grantedSkills") ?? "").Split(';'))
                        if (string.Equals((one ?? "").Trim(), skillDbr, StringComparison.OrdinalIgnoreCase))
                            return SkillEdit.LevelOf(c, tree);
                }
            return -1;
        }

        static PowerTarget One(GameData game, Character c, string powerDbr, string dbr, bool fromItem)
        {
            PowerTarget t = new PowerTarget();
            t.Dbr = dbr;
            t.Name = SkillText.Name(game, dbr) ?? dbr;
            t.Learned = fromItem || SkillEdit.LevelOf(c, dbr) > 0;
            string power = PowerOn(c, dbr);
            t.Current = string.Equals(power, powerDbr, StringComparison.OrdinalIgnoreCase);
            t.FromItem = fromItem;
            if (power != null && !t.Current) t.TakenBy = SkillText.Name(game, power) ?? power;
            return t;
        }

        // TIE THE POWER TO THIS SKILL — or to nothing, when `skillDbr` is null.
        //
        // Three things move at once, and all three are what the game's own saves
        // hold: the power leaves whatever it hung on before, whatever power the new
        // skill was carrying leaves it, and the new skill gets the power together
        // with the controller the power itself names. A skill with no points still
        // gets an entry: that is how the game keeps a tie made before the points
        // went in, and it is why `SkillEdit.Remove` leaves such an entry alone.
        public static bool Bind(GameData game, Character c, string powerDbr, string skillDbr)
        {
            if (game == null || c == null || string.IsNullOrEmpty(powerDbr)) return false;
            if (string.IsNullOrEmpty(skillDbr)) skillDbr = null;
            // Nothing to do when it already hangs there — "nothing" included.
            if (string.Equals(BoundTo(c, powerDbr), skillDbr, StringComparison.OrdinalIgnoreCase)) return false;

            Clear(c, powerDbr, null);
            if (string.IsNullOrEmpty(skillDbr)) return true;
            Clear(c, null, skillDbr);

            string controller = ControllerOf(game, powerDbr) ?? "";
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, skillDbr, StringComparison.OrdinalIgnoreCase))
                { s.AutoCastSkill = powerDbr; s.AutoCastController = controller; return true; }
            foreach (ItemSkill s in c.ItemSkills)
                if (string.Equals(s.SkillName, skillDbr, StringComparison.OrdinalIgnoreCase))
                { s.AutoCastSkill = powerDbr; s.AutoCastController = controller; return true; }

            Skill made = new Skill();
            made.Name = skillDbr;
            made.AutoCastSkill = powerDbr;
            made.AutoCastController = controller;
            // A SKILL A SHAPESHIFT GRANTS IS WRITTEN DIFFERENTLY, and this is measured
            // on the game's own save (`_FenrisWerwolf`, MECHANICS 107d): the entry
            // carries the LEVEL OF THE SHAPESHIFT and `Enabled 0`, while an ordinary
            // skill with a tie made before its points carries level 0 and `Enabled 1`.
            int granted = BreakShapeshift ? -1 : GrantedLevel(game, c, skillDbr);
            if (granted >= 0) { made.Level = granted; made.Enabled = 0; }
            else { made.Level = 0; made.Enabled = 1; }
            c.Skills.Add(made);
            return true;
        }

        // Undo a tie: by the power, by the skill, or by both.
        static void Clear(Character c, string powerDbr, string skillDbr)
        {
            foreach (Skill s in c.Skills)
            {
                if (powerDbr != null && !string.Equals(s.AutoCastSkill, powerDbr, StringComparison.OrdinalIgnoreCase)) continue;
                if (skillDbr != null && !string.Equals(s.Name, skillDbr, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(s.AutoCastSkill)) continue;
                s.AutoCastSkill = "";
                s.AutoCastController = "";
            }
            foreach (ItemSkill s in c.ItemSkills)
            {
                if (powerDbr != null && !string.Equals(s.AutoCastSkill, powerDbr, StringComparison.OrdinalIgnoreCase)) continue;
                if (skillDbr != null && !string.Equals(s.SkillName, skillDbr, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(s.AutoCastSkill)) continue;
                s.AutoCastSkill = "";
                s.AutoCastController = "";
            }
        }
    }
}
