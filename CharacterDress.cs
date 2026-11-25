// DRESSING A CHARACTER FROM AN OUTSIDE BUILD.
//
// `GrimTools` fetches the build, `GrimToolsMap` turns its numbers into the game's
// record paths, and this is the step that writes them into the save. Nothing here
// knows where the build came from: it takes a `GtCharacter` and a `Character`.
//
// WHAT IS WRITTEN, AND WHAT IS NOT:
//
//   worn gear       all sixteen slots, each with everything on it — the item, the
//                   prefix, the suffix, the component, the augment, the ascension
//                   and, on a relic, its completion bonus
//   skills          the masteries, the level of every skill, and which skill each
//                   celestial power hangs on
//   devotion        every star taken
//   the character   level, physique, cunning, spirit and the three pools of points
//
//   NOT touched     the bags, the stash, the quests, the shrines, the name — the
//                   build says nothing about any of them
//
// A SLOT WHOSE ITEM DID NOT MATCH IS LEFT ALONE. That is the user's rule, and it is
// the safe one: half an item is worse than the old one. The slot is counted and
// named in the report instead.
//
// THE SEED IS ZERO. The build carries no rolls — GrimTools has no notion of a seed —
// so every item goes in at the bottom of its ranges and can be turned up afterwards
// in the edit window. MECHANICS 106b.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public sealed class DressResult
    {
        public int Slots, Skipped, Cleared, Skills, Stars, Bindings;
        // HOW MANY OFF HANDS WERE EMPTIED BECAUSE THE MAIN ONE HOLDS A TWO-HANDER. The
        // build can ask for the impossible — GrimTools names the same two-hander in both
        // weapon slots — and the report must say that we did not do as asked, and why
        // (see `TwoHandedFreesTheOffHand`).
        public int TwoHandedFreed;
        // Count non-empty slots named by the build but not recognized by the parser.
        public int Kept;
        // HOW MANY OF THE BUILD'S SKILLS LANDED ON A RECORD ALREADY TAKEN. It is zero
        // when the matching did its job, and it is not zero when several of the
        // build's entries were told apart by nothing and collapsed into one — which is
        // exactly what happened to the devotion stars (PLAN, item 2a). The second
        // entry is dropped rather than written: a save with one record twice is not
        // something the game ever writes.
        public int Collapsed;
        // Which slots were left as they were, and which records came twice. Two lists,
        // because the first is what the window tells the user about and the second is
        // what a check reads.
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Repeats = new List<string>();
    }

    public static class CharacterDress
    {
        // `--gtimportbreak` — put the gear on WITHOUT its ascensions, to see the
        // check that reads the save back go red (PLAN, rule 12).
        public static bool BreakAscension;

        // Which list and which place in it a build's slot name means. The twelve
        // worn slots are one list in the save, in this order, and the two weapon
        // sets are lists of their own.
        static readonly string[] WornOrder =
        {
            "head", "amulet", "chest", "legs", "feet", "hands",
            "ring1", "ring2", "waist", "shoulders", "medal", "relic"
        };

        static bool Place(Character c, string slot, out List<Item> list, out int index)
        {
            list = null;
            index = -1;
            switch (slot)
            {
                case "weapon1": list = c.Weapons1; index = 0; return true;
                case "weapon2": list = c.Weapons1; index = 1; return true;
                case "weapon1Alt": list = c.Weapons2; index = 0; return true;
                case "weapon2Alt": list = c.Weapons2; index = 1; return true;
            }
            int at = Array.IndexOf(WornOrder, slot);
            if (at < 0) return false;
            list = c.Equipment;
            index = at;
            return true;
        }

        public static DressResult Apply(GameData game, Character c, GtCharacter build)
        {
            DressResult made = new DressResult();
            if (game == null || c == null || build == null) return made;

            Wear(c, build, made);
            TwoHandedFreesTheOffHand(game, c, made);
            Learn(game, c, build, made);
            Bio(game, c, build, made);
            return made;
        }

        // ------------------------------------------------------------ the gear

        // A TWO-HANDER LEAVES THE OFF HAND EMPTY — the build may say otherwise, and
        // GrimTools does.
        //
        // MEASURED ON THE USER'S BUILD, 19 October (`grimtools.com/calc/ZDMDqXYN`). Its
        // `equipment` names THE SAME item in both weapon slots, down to the same prefix,
        // suffix, component and augment:
        //
        //     "weapon2":{"item":"it14911",…}      "weapon1":{"item":"it14911",…}
        //
        // and one ranged weapon record uses `WeaponHunting_Ranged2h` — a two-handed gun
        // out of `guns2h`. Placed slot by slot, exactly as the build says, it lands in BOTH
        // hands: a character the editor itself would not let anyone build with the mouse,
        // because `MainForm.CanEquip` refuses the off hand while a two-hander is in the main
        // one. The user spotted it: "a two-handed gun is copied into both slots, while it can
        // only be in the main one".
        //
        // So the window's rule is applied to the import as well, through the very same
        // `EquipRules.IsTwoHanded` — one rule, one place.
        //
        // THE OFF HAND IS THE ONE EMPTIED, not the main one: that is where the game keeps a
        // two-hander, and it is where the import had already put the first copy.
        // `--gt2hbreak` — leave the off hand as the build asked, i.e. the way the import
        // worked before 19 October: a two-hander in both hands at once. The frozen build
        // `build-ZDMDqXYN.json` then goes red on six lines.
        public static bool BreakTwoHandedFree;

        static void TwoHandedFreesTheOffHand(GameData game, Character c, DressResult made)
        {
            if (BreakTwoHandedFree) return;
            FreeOffHand(game, c, c.Weapons1, made);
            FreeOffHand(game, c, c.Weapons2, made);
        }

        static void FreeOffHand(GameData game, Character c, List<Item> hands, DressResult made)
        {
            if (game == null || hands == null || hands.Count < 2) return;
            Item main = hands[0], off = hands[1];
            if (main == null || off == null) return;
            if (string.IsNullOrEmpty(main.BaseName) || string.IsNullOrEmpty(off.BaseName)) return;
            DbRecord rec = game.Record(main.BaseName);
            if (rec == null || !EquipRules.IsTwoHanded(rec.Class ?? "")) return;
            hands[1] = Item.Create("", c.ItemTailWords, c.ItemHasAscension);
            made.Cleared++;
            made.TwoHandedFreed++;
        }

        static void Wear(Character c, GtCharacter build, DressResult made)
        {
            // Which slots the build speaks about at all. A slot it never mentions is
            // emptied: the build is the whole picture of what the character wears.
            Dictionary<string, GtItem> bySlot = new Dictionary<string, GtItem>(StringComparer.OrdinalIgnoreCase);
            foreach (GtItem one in build.Items) bySlot[one.Slot] = one;

            List<string> every = new List<string>(WornOrder);
            every.AddRange(new[] { "weapon1", "weapon2", "weapon1Alt", "weapon2Alt" });
            foreach (string slot in every)
            {
                List<Item> list;
                int index;
                if (!Place(c, slot, out list, out index)) continue;
                while (list.Count <= index) list.Add(Item.Create("", c.ItemTailWords, c.ItemHasAscension));

                GtItem want;
                bool spoken = bySlot.TryGetValue(slot, out want);
                // THE BUILD HAS NOTHING FOR THIS SLOT — so the character wears nothing
                // there either: a build is the whole picture of what is worn.
                if (!spoken)
                {
                    if (!string.IsNullOrEmpty(list[index].BaseName)) made.Cleared++;
                    list[index] = Item.Create("", c.ItemTailWords, c.ItemHasAscension);
                    continue;
                }
                // THE BUILD HAD AN ITEM AND IT DID NOT MATCH — the slot is left exactly
                // as it was. The user's rule, and the safe one: half an item is worse
                // than the old one.
                if (want.ItemDbr == null)
                {
                    made.Skipped++;
                    made.Notes.Add(slot);
                    // Also count non-empty unrecognized slots. Otherwise one
                    // unrecognized item would make the equipped-item check fail,
                    // even though preserving the slot is the correct behavior.
                    if (!string.IsNullOrEmpty(list[index].BaseName)) made.Kept++;
                    continue;
                }

                Item it = Item.Create(want.ItemDbr, c.ItemTailWords, c.ItemHasAscension);
                it.PrefixName = want.PrefixDbr ?? "";
                it.SuffixName = want.SuffixDbr ?? "";
                // The save calls the component `RelicName` and the relic's own
                // completion bonus `RelicBonus` — the two are not the same thing.
                it.RelicName = want.ComponentDbr ?? "";
                it.RelicBonus = want.RelicBonus ?? "";
                it.AugmentName = want.AugmentDbr ?? "";
                // `--gtimportbreak` — the ascension is left off on purpose, so the
                // check that compares every part of every item can be seen going red
                // (PLAN, rule 12).
                if (it.HasAscension && !BreakAscension) it.AscensionName = want.AscensionDbr ?? "";
                it.Seed = 0;
                it.RelicSeed = 0;
                it.AugmentSeed = 0;
                it.AscensionSeed = 0;
                list[index] = it;
                made.Slots++;
            }
        }

        // ---------------------------------------------------------- the skills

        // EVERY SKILL ENTRY THE BUILD SPEAKS FOR IS REWRITTEN, AND THE REST GO.
        //
        // What stays is only what the build has no opinion about: entries that are
        // neither a class skill nor a devotion star. Everything the character had of
        // its own — the masteries, the levels, the stars — is replaced, because that
        // is what an import is for and what the warning said it would do.
        static void Learn(GameData game, Character c, GtCharacter build, DressResult made)
        {
            List<Skill> keep = new List<Skill>();
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                if (SkillsForm.IsDevotion(s.Name)) continue;
                if (ItemViewFactory.MasteryFolder(s.Name) != null) continue;
                // An entry that is neither: leave it, but let go of a binding to a
                // star the imported character will not have.
                s.AutoCastSkill = "";
                s.AutoCastController = "";
                keep.Add(s);
            }

            // The skills a shapeshift grants, held back: their level is the
            // shapeshift's, and that is only known once the whole list is in place.
            List<Skill> granted = new List<Skill>();

            // The bindings first, so the skill entries can be made with them in place.
            Dictionary<string, string> hangs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GtSkill one in build.Skills)
                if (one.AutoCastDbr != null && one.Dbr != null) hangs[one.Dbr] = one.AutoCastDbr;

            // ONE RECORD, ONE ENTRY. A build names its skills by their own numbers,
            // and two of those numbers can land on one record of ours when nothing
            // told them apart. Writing both would put the same skill into the save
            // twice — and it would also make the counting lie, since fifty entries of
            // thirteen records count as fifty.
            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s0 in keep) if (!string.IsNullOrEmpty(s0.Name)) written.Add(s0.Name);

            foreach (GtSkill one in build.Skills)
            {
                if (string.IsNullOrEmpty(one.Dbr)) continue;
                // A skill a shapeshift grants comes with no level of its own — it
                // carries the shapeshift's, and the shapeshift is already in the list
                // above, so it is looked up after the whole list is in place.
                if (one.Level <= 0 && !one.Granted) continue;
                if (!written.Add(one.Dbr))
                {
                    made.Collapsed++;
                    made.Repeats.Add(one.Dbr);
                    continue;
                }
                Skill s = new Skill();
                s.Name = one.Dbr;
                s.Level = one.Level;
                s.Enabled = 1;
                if (one.Granted)
                {
                    // Measured on the game's own save: the entry holds the
                    // shapeshift's level and `Enabled 0` (MECHANICS 107d).
                    s.Enabled = 0;
                    granted.Add(s);
                    made.Skills++;
                }
                else if (one.Devotion)
                {
                    // A star is one point; what the game stores beside it is the
                    // star's own level, which starts at one (MECHANICS 104e).
                    s.Level = 1;
                    s.DevotionLevel = 1;
                    s.DevotionExperience = 0;
                    made.Stars++;
                }
                else made.Skills++;
                string star;
                if (hangs.TryGetValue(one.Dbr, out star))
                {
                    s.AutoCastSkill = star;
                    s.AutoCastController = DevotionBinding.ControllerOf(game, star) ?? "";
                    made.Bindings++;
                }
                keep.Add(s);
            }

            c.Skills.Clear();
            c.Skills.AddRange(keep);

            // THE CLASS NAME FOLLOWS THE MASTERIES, and it is kept by hand: the save
            // carries it as a tag of its own and nothing recomputes it (`MasteryEdit`).
            // The import used to leave it as it was, so `_GTImport` ended up an
            // Arcanist + Shaman with 248 points in the bars and an EMPTY class name —
            // caught by `--masterytest` on 7 October, two errors.
            MasteryEdit.Retag(c, game);

            // The shapeshifts are in place now, so the skills they grant can take
            // their level from them.
            foreach (Skill s in granted)
            {
                int level = DevotionBinding.GrantedLevel(game, c, s.Name);
                s.Level = level > 0 ? level : 1;
            }
        }

        // ------------------------------------------------------- the character

        static void Bio(GameData game, Character c, GtCharacter build, DressResult made)
        {
            if (build.Level > 0)
            {
                c.Level = build.Level;
                // The level and the experience have to agree, or the game recounts the
                // level from the experience and the import is undone. The same step
                // the character panel takes when the level is typed in: experience
                // already inside the band is left exactly as the game wrote it.
                long low = CharacterPanel.ExperienceFor(game, build.Level);
                long high = CharacterPanel.ExperienceFor(game, build.Level + 1) - 1;
                if (high < low || high > int.MaxValue) high = int.MaxValue;
                if (c.Experience < low || c.Experience > high) c.Experience = (int)low;
            }
            if (build.Physique > 0) c.Physique = build.Physique;
            if (build.Cunning > 0) c.Cunning = build.Cunning;
            if (build.Spirit > 0) c.Spirit = build.Spirit;
            // Apply health and energy after the other attributes. Saves store them
            // as base values plus invested-point contributions, not as final totals.
            PlayerAttributes.Follow(game, c);
            c.AttributePoints = build.AttributePoints;
            c.SkillPoints = build.SkillPoints;
            c.DevotionPoints = build.DevotionPoints;
            // `TotalDevotion` is what the character EARNED, and the build only says
            // how much is left over — so the earned total is what was spent plus
            // what is free.
            c.TotalDevotion = made.Stars + build.DevotionPoints;
        }
    }
}
