// MATCHING A GRIMTOOLS BUILD TO THE GAME'S RECORDS.
//
// `GrimTools` fetches the build and their database; here their numbers become the
// record paths a save is made of. The write-up is MECHANICS 106.
//
// THE MATCHING GOES BY THE GAME'S FIELDS, NOT BY NAMES. A name follows the language;
// these fields do not, and both sides have them:
//
//     item     `itemNameTag` (for components, relics and augments — `description`)
//              plus `bitmapName` plus `levelRequirement` plus the record's class
//     affix    `lootRandomizerName` plus `levelRequirement`, `lootRandomizerJitter`
//              and `itemClassification`
//     skill    `skillDisplayName` plus `skillUpBitmapName`
//
// ONE KEY IS NOT ENOUGH, AND THAT IS MEASURED: over our catalogue (9253 items) there
// are 3908 distinct pictures, and 1902 of them belong to more than one record —
// One displayed object may map to several records sharing a picture and level key.
// 20/40/55/70/82/94. So the keys are tried in turn, from the strictest to the
// weakest, and EVERY answer says which key produced it: the import report shows that
// line by line.
//
// WHAT THE BUILD DOES NOT HOLD IS ROLLS. GrimTools has no notion of a seed, so the
// item put in is the very same one, while the numbers inside its ranges are ours
// (the seed is zero).
//
// THE WORDS IN `Notes` AND `Problems` ARE THE REPORT'S, NOT A WINDOW'S. They go into
// the check's report and are written in Russian like every other report of ours. The
// window says the same things in the user's own language and never from here: what
// went wrong is COUNTED (`NoData`, `UnmatchedSkills`, `UnmatchedParts` and the rest)
// and the wording is looked up in the language files, where all twelve translations
// of it live. Nothing a user reads may be worded in code — the header of `Lang.cs`.
// And the numbers of either side (`it14666`, `sk1277`, a record's path) stay in the
// report: in a window they would say nothing to anybody.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Gdc.Gui
{
    // One equipment slot, already in the game's record paths.
    public sealed class GtItem
    {
        public string Slot = "";
        public string ItemDbr, PrefixDbr, SuffixDbr, ComponentDbr, AugmentDbr, AscensionDbr, RelicBonus;
        public string ItemName = "";
        public readonly List<string> Notes = new List<string>();
        // How many parts of this slot were not matched — the item itself, an affix, a
        // component, an augment, an ascension. The window has no business showing
        // their numbers (`it14666`, `suf10314`) or ours, so it shows the COUNT and the
        // report keeps the detail.
        public int Unmatched;
        // Count items that do not exist in the installed game's records.
        public int Absent;
    }

    public sealed class GtSkill
    {
        public string Dbr = "", Name = "";
        public int Level;
        public string AutoCastDbr;       // the celestial power hung on this skill
        // Which matching key was used: skill tag, tag plus constellation, or tag plus values.
        // The report tallies these, and that is how a key that stopped working shows
        // itself: tag plus icon was not sufficient to distinguish the records.
        public string How = "";
        public bool Devotion;
        // True for a skill a shapeshift grants: the save writes those differently —
        // the level of the shapeshift and `Enabled 0` (MECHANICS 107d).
        public bool Granted;
    }

    public sealed class GtCharacter
    {
        public int Level, AttributePoints, SkillPoints, DevotionPoints;
        public int Physique, Cunning, Spirit;
        public string GameVersion = "";
        public readonly List<string> Masteries = new List<string>();
        public readonly List<GtItem> Items = new List<GtItem>();
        public readonly List<GtSkill> Skills = new List<GtSkill>();
        public readonly List<string> Problems = new List<string>();

        // WHAT THE WINDOW IS ALLOWED TO SAY. `Problems` is the report's own wording,
        // in Russian like every other report of ours; a window must take its words
        // from the language files instead (the header of `Lang.cs`). So the kinds of
        // trouble are counted here, and the window says how many of each there were.
        public bool NoData, NoCatalog;
        public int UnmatchedSkills, UnmatchedPowers, UnmatchedShapes;
        // How many skills were taken on a guess — see `GrimToolsMap.Guessed`.
        public int GuessedSkills;
        // And how many times the numbers and the place in the constellation named
        // different stars — see `GrimToolsMap.Mixed`. Always zero, or the place is no
        // key at all and the check says so.
        public int MixedSkills;

        // Count build items absent from the installed game's records. This is reported
        // separately because there is no local record to repair.
        public int AbsentFromGame
        {
            get
            {
                int n = 0;
                foreach (GtItem one in Items) n += one.Absent;
                return n;
            }
        }

        // The parts of the gear that were not matched, summed over every slot.
        public int UnmatchedParts
        {
            get
            {
                int n = 0;
                foreach (GtItem one in Items) n += one.Unmatched;
                return n;
            }
        }
    }

    public static class GrimToolsMap
    {
        // -------------------------------------------------------- our indexes

        sealed class Index
        {
            public readonly Dictionary<string, List<CatalogEntry>> ByTag =
                new Dictionary<string, List<CatalogEntry>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<DbRecord>> Affix =
                new Dictionary<string, List<DbRecord>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> Skill =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // The constellation each devotion star belongs to, said the same way both
            // sides say it — see `GrimTools.HomeSignature`.
            public readonly Dictionary<string, string> Home =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // And which button of it — `devotionButtonN` of the game's own record.
            public readonly Dictionary<string, int> Button =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // The ascensions, by what they DO — see `AscensionKey`.
            public readonly Dictionary<string, List<DbRecord>> Ascended =
                new Dictionary<string, List<DbRecord>>(StringComparer.Ordinal);
            // The relic completion bonuses, likewise nameless, likewise by what they do.
            public readonly Dictionary<string, List<DbRecord>> Bonus =
                new Dictionary<string, List<DbRecord>>(StringComparer.Ordinal);
        }

        static void Add<T>(Dictionary<string, List<T>> into, string key, T value)
        {
            if (string.IsNullOrEmpty(key)) return;
            List<T> bag;
            if (!into.TryGetValue(key, out bag)) { bag = new List<T>(); into[key] = bag; }
            bag.Add(value);
        }

        // Both sides name the picture by the same path, only theirs ends in `.png`
        // and the game's in `.tex` — so they are compared without the extension.
        static string Art(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/').ToLowerInvariant();
            int dot = s.LastIndexOf('.');
            return dot > 0 ? s.Substring(0, dot) : s;
        }

        // -------------------------------------------------------------- items

        static CatalogEntry PickItem(Index index, GtEntry want, List<string> how)
        {
            List<CatalogEntry> bag;
            if (!index.ByTag.TryGetValue(want.NameTag ?? "", out bag) || bag.Count == 0) return null;
            // Try keys from strictest to weakest: tag, picture, level, and rarity;
            // then omit rarity, level, picture, and finally keep only the tag.
            //
            // Include rarity because records may share tag, picture, and level.
            // `itemClassification` distinguishes such records when both sides provide it.
            CatalogEntry got = Only(bag, want, true, true, true, how, "tag + icon + level + rarity");
            if (got == null) got = Only(bag, want, true, true, false, how, "tag + icon + level");
            if (got == null) got = Only(bag, want, true, false, true, how, "tag + icon + rarity");
            if (got == null) got = Only(bag, want, true, false, false, how, "tag + icon");
            if (got == null) got = Only(bag, want, false, true, true, how, "tag + level + rarity");
            if (got == null) got = Only(bag, want, false, true, false, how, "tag + level");
            if (got == null && bag.Count == 1) { how.Add("unique tag"); return bag[0]; }
            return got;
        }

        static CatalogEntry Only(List<CatalogEntry> bag, GtEntry want, bool byArt, bool byLevel,
                                 bool byRarity, List<string> how, string label)
        {
            List<CatalogEntry> fit = new List<CatalogEntry>();
            foreach (CatalogEntry e in bag)
            {
                if (byArt && Art(e.Bitmap) != Art(want.Bitmap)) continue;
                if (byLevel && e.LevelRequirement != want.Level) continue;
                // Use rarity only when both sides provide it; some game records omit it.
                if (byRarity && (string.IsNullOrEmpty(want.Rarity) || string.IsNullOrEmpty(e.Classification) ||
                                 !string.Equals(e.Classification, want.Rarity, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!string.IsNullOrEmpty(want.RecordClass) && !string.IsNullOrEmpty(e.RecordClass) &&
                    !string.Equals(e.RecordClass, want.RecordClass, StringComparison.OrdinalIgnoreCase)) continue;
                fit.Add(e);
            }
            if (fit.Count != 1) return null;
            how.Add(label);
            return fit[0];
        }

        // --------------------------------------------------------- ascensions

        // AN ASCENSION IS KNOWN BY WHAT IT DOES, BECAUSE IT HAS NOTHING ELSE. Over all
        // 1181 of the game's `lootaffixes/ascended` records not one carries a
        // `lootRandomizerName`, and their side has no name for them either. What both
        // sides do have is the numbers — so the key is the record's own numbers plus
        // the numbers of the `modifierSkillName1` record it points at, which is where
        // a mastery ascension keeps its work.
        //
        // WHY THE MODIFIER IS NEEDED AT ALL: of 330 groups of ours sharing a modified
        // skill, an augmented skill and a level, 291 still hold more than one record,
        // the worst of them sixteen. Nothing short of the numbers tells those apart.
        //
        // WHEN SEVERAL OF OURS STILL ANSWER, THEY ARE THE SAME THING. `ao301a`,
        // `ao301c` and `ao303a` are three records with identical contents — the same
        // affix offered to different kinds of item — so taking any of them gives the
        // player exactly what the build said. The report says when that happened.
        // THE NUMBERS ALONE ARE STILL NOT ENOUGH, AND THAT ONE SHOWED ITSELF: a
        // hundred relic bonuses all say "+1 to two skills" and differ only in WHICH
        // two. Their side names those skills by tag, ours by record path, so the path
        // is followed to the tag and both go into the key.
        static string AscensionKey(string ownSig, string modifierSig, string modified,
                                   string augment1, string augment2)
        {
            return ownSig + " | " + modifierSig + " | " + (modified ?? "") + " " +
                   (augment1 ?? "") + " " + (augment2 ?? "");
        }

        // The tag a skill record is named by, following the same hand-off chain
        // everything else does.
        static string TagOf(GameData game, string dbr)
        {
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return "";
                string tag = rec.Get("skillDisplayName");
                if (!string.IsNullOrEmpty(tag)) return tag;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return "";
        }

        static string SigOf(DbRecord rec)
        {
            if (rec == null) return "";
            SortedDictionary<string, string> fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                string number = GrimTools.Numbers(kv.Value);
                if (number == null || GrimTools.AllZero(number) || GrimTools.Structural(kv.Key)) continue;
                fields[kv.Key] = number;
            }
            return GrimTools.Signature(fields);
        }

        static DbRecord PickAscension(GameData game, GtDb db, Index index, GtEntry want, List<string> how)
        {
            GtEntry modifier = string.IsNullOrEmpty(want.ModifierSkill) ? null : db.Find(want.ModifierSkill);
            string key = AscensionKey(want.Sig, modifier == null ? "" : modifier.Sig,
                                      want.ModifiedSkill, want.AugmentSkill, want.AugmentSkill2);
            List<DbRecord> bag;
            if (!index.Ascended.TryGetValue(key, out bag) || bag.Count == 0) return null;
            how.Add(bag.Count == 1 ? "matched by stats" : "matched by stats (identical records: " + bag.Count + ")");
            return bag[0];
        }

        // A RELIC'S COMPLETION BONUS HAS NO NAME EITHER — 454 records under
        // `lootaffixes/completion`, none of them with a `lootRandomizerName`. The
        // numbers alone are too thin there (half of them are "+1 to two skills"), so
        // the three things that stand beside them are put into the key as well: the
        // rarity, the level and `lootRandomizerJitter`, which is the spread the game
        // rolls the bonus within and which their side keeps too.
        static string BonusKey(string sig, int jitter, string rarity, int level,
                               string augment1, string augment2)
        {
            return sig + " | j" + jitter + " " + (rarity ?? "") + " L" + level + " | " +
                   (augment1 ?? "") + " " + (augment2 ?? "");
        }

        static DbRecord PickBonus(Index index, GtEntry want, List<string> how)
        {
            List<DbRecord> bag;
            string key = BonusKey(want.Sig, want.Jitter, want.Rarity, want.Level,
                                  want.AugmentSkill, want.AugmentSkill2);
            if (!index.Bonus.TryGetValue(key, out bag) || bag.Count == 0) return null;
            how.Add(bag.Count == 1 ? "matched by stats" : "matched by stats (identical records: " + bag.Count + ")");
            return bag[0];
        }

        // ------------------------------------------------------------ affixes

        // AN AFFIX IS NOT ALWAYS TOLD APART BY ITS NAME. `tagSuffixA008` is a dozen
        // records — the same suffix at a dozen levels — and even at one level, with
        // one jitter and one rarity, two of them remain: +830 health and +1220. So the
        // numbers go into the test first, and the weaker keys are only fallen back to.
        // Some affixes have no name tag at all, and those are matched by the numbers
        // alone (`PickNameless`).
        static DbRecord PickAffix(Index index, GtEntry want, List<string> how)
        {
            List<DbRecord> bag;
            if (!index.Affix.TryGetValue(want.LootName ?? "", out bag) || bag.Count == 0)
                return PickNameless(index, want, how);
            List<DbRecord> fit = new List<DbRecord>();
            foreach (DbRecord r in bag)
            {
                if (want.Level > 0 && r.GetInt("levelRequirement") != want.Level) continue;
                if (want.Jitter > 0 && r.GetInt("lootRandomizerJitter") != want.Jitter) continue;
                if (!string.IsNullOrEmpty(want.Rarity) &&
                    !string.Equals(r.Get("itemClassification") ?? "", want.Rarity, StringComparison.OrdinalIgnoreCase))
                    continue;
                fit.Add(r);
            }
            foreach (DbRecord r in fit)
                if (SigOf(r) == want.Sig) { how.Add("tag + level + variance + stats"); return r; }
            if (fit.Count == 1) { how.Add("tag + level + variance"); return fit[0]; }
            foreach (DbRecord r in bag)
                if (SigOf(r) == want.Sig) { how.Add("tag + stats"); return r; }
            if (bag.Count == 1) { how.Add("unique tag"); return bag[0]; }
            return PickNameless(index, want, how);
        }

        static DbRecord PickNameless(Index index, GtEntry want, List<string> how)
        {
            List<DbRecord> bag;
            string key = BonusKey(want.Sig, want.Jitter, want.Rarity, want.Level,
                                  want.AugmentSkill, want.AugmentSkill2);
            if (!index.Bonus.TryGetValue(key, out bag) || bag.Count == 0) return null;
            how.Add(bag.Count == 1 ? "matched by stats" : "matched by stats (identical records: " + bag.Count + ")");
            return bag[0];
        }

        // ---------------------------------------------------- putting it together

        public static GtCharacter Resolve(GameData game, GtDb db, string buildJson, List<string> log)
        {
            GtCharacter made = new GtCharacter();
            JsonDocument doc = JsonDocument.Parse(buildJson);
            JsonElement root = doc.RootElement;
            JsonElement data;
            if (!root.TryGetProperty("data", out data))
            {
                made.Problems.Add("the page contains no build data");
                made.NoData = true;
                return made;
            }
            made.GameVersion = Str(root, "created_for_build");
            // THE MASTERIES COME IN TWO SHAPES, AND BOTH ARE REAL (MECHANICS 158).
            //
            // Older builds say `"masteries":["07","06"]`, newer ones
            // `"masteries":{"1":"06","0":"07"}` — the same two numbers, keyed by the slot.
            // We read only the array, so for such a build the list stayed EMPTY, the mastery
            // trees were never walked, and every class skill was looked up in the general
            // pass instead — where the name belongs to the END of the chain and not to the
            // tree's button. In the save that put `hunteraura1_buff` where `hunteraura1`
            // belongs, and in the skills window those skills simply were not there.
            //
            // The user found it on `grimtools.com/calc/ZyDdAnbV` and guessed the cause in the
            // A build may also target a different database version.
            JsonElement masteries;
            if (root.TryGetProperty("masteries", out masteries))
            {
                // `--gtmasterybreak` — read only the array shape, the way it was until
                // 19 October, so the guard over this can be seen going red.
                if (BreakMasteryObject && masteries.ValueKind != JsonValueKind.Array) { }
                else if (masteries.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement m in masteries.EnumerateArray()) made.Masteries.Add(m.GetString());
                else if (masteries.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty m in masteries.EnumerateObject())
                        if (m.Value.ValueKind == JsonValueKind.String) made.Masteries.Add(m.Value.GetString());
            }

            JsonElement bio;
            if (data.TryGetProperty("bio", out bio))
            {
                made.Level = Int(bio, "level");
                made.AttributePoints = Int(bio, "attributePoints");
                made.SkillPoints = Int(bio, "skillPoints");
                made.DevotionPoints = Int(bio, "devotionPoints");
                made.Physique = Int(bio, "physique");
                made.Cunning = Int(bio, "cunning");
                made.Spirit = Int(bio, "spirit");
            }

            Index index = BuildIndex(game, db, made, log);

            JsonElement equipment;
            if (data.TryGetProperty("equipment", out equipment))
                foreach (JsonProperty slot in equipment.EnumerateObject())
                {
                    if (slot.Value.ValueKind != JsonValueKind.Object) continue;
                    GtItem one = new GtItem();
                    one.Slot = slot.Name;
                    string itemId = Str(slot.Value, "item");
                    if (string.IsNullOrEmpty(itemId)) continue;
                    one.ItemDbr = Item(game, db, index, itemId, one, "item");
                    one.PrefixDbr = Affix(db, index, Str(slot.Value, "prefix"), one, "prefix");
                    one.SuffixDbr = Affix(db, index, Str(slot.Value, "suffix"), one, "suffix");
                    one.ComponentDbr = Item(game, db, index, Str(slot.Value, "component"), one, "component");
                    one.AugmentDbr = Item(game, db, index, Str(slot.Value, "augment"), one, "augment");
                    one.AscensionDbr = Ascension(game, db, index, Str(slot.Value, "ascendedAffix"), one);
                    one.RelicBonus = Bonus(db, index, Str(slot.Value, "relicBonus"), one);
                    if (one.ItemDbr != null)
                    {
                        CatalogEntry named = FindEntry(game, one.ItemDbr);
                        one.ItemName = named == null ? one.ItemDbr : named.Name;
                    }
                    made.Items.Add(one);
                }

            JsonElement skills;
            if (data.TryGetProperty("skills", out skills) && skills.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in skills.EnumerateArray())
                {
                    string id = Str(s, "name");
                    GtSkill sk = new GtSkill();
                    sk.Level = Int(s, "level");
                    List<string> how = new List<string>();
                    sk.Dbr = Skill(game, db, index, id, how);
                    if (sk.Dbr == null)
                    {
                        GtEntry raw = db.Find(id);
                        made.Problems.Add("could not match skill: " + id +
                                          (raw == null ? " (ID missing from the GrimTools database)"
                                                       : " (tag “" + raw.SkillTag + "”, delegates to “" +
                                                         raw.BuffSkill + "”, icon " + raw.SkillIcon + ")"));
                        made.UnmatchedSkills++;
                        continue;
                    }
                    // A skill taken on a guess is counted here and nowhere else: the
                    // report prints the number, so a fallback cannot sit unnoticed.
                    foreach (string line in how)
                    {
                        if (line.StartsWith(Guessed, StringComparison.Ordinal)) made.GuessedSkills++;
                        if (line.StartsWith(Mixed, StringComparison.Ordinal)) made.MixedSkills++;
                    }
                    sk.How = string.Join(", ", how.ToArray());
                    sk.Name = SkillText.Name(game, sk.Dbr) ?? sk.Dbr;
                    sk.Devotion = SkillsForm.IsDevotion(sk.Dbr);
                    string cast = Str(s, "autoCastSkill");
                    if (!string.IsNullOrEmpty(cast))
                    {
                        List<string> h2 = new List<string>();
                        sk.AutoCastDbr = Skill(game, db, index, cast, h2);
                        if (sk.AutoCastDbr == null)
                        {
                            made.Problems.Add("could not match celestial power: " + cast);
                            made.UnmatchedPowers++;
                        }
                    }
                    made.Skills.Add(sk);
                }

            // A POWER HUNG ON A SKILL A SHAPESHIFT GRANTS IS IN A LIST OF ITS OWN.
            // Their build writes `transformSkills` as
            // `{autoCastSkill, name, mastery, transformSkill}` — the power, the skill
            // it hangs on, and the shapeshift that brings it. Without reading this a
            // werewolf build lost exactly those ties, and those are the ones the user
            // asked after.
            JsonElement transforms;
            if (data.TryGetProperty("transformSkills", out transforms) && transforms.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in transforms.EnumerateArray())
                {
                    List<string> how = new List<string>();
                    string dbr = Skill(game, db, index, Str(s, "name"), how);
                    string cast = Skill(game, db, index, Str(s, "autoCastSkill"), how);
                    if (dbr == null || cast == null)
                    {
                        made.Problems.Add("could not match form binding: " + Str(s, "name"));
                        made.UnmatchedShapes++;
                        continue;
                    }
                    GtSkill sk = new GtSkill();
                    sk.Dbr = dbr;
                    sk.Name = SkillText.Name(game, dbr) ?? dbr;
                    sk.AutoCastDbr = cast;
                    sk.Granted = true;
                    // The level is the shapeshift's own, and that is read from the
                    // character we are dressing — the build does not say it here.
                    made.Skills.Add(sk);
                }
            return made;
        }

        static string Item(GameData game, GtDb db, Index index, string id, GtItem into, string what)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GtEntry want = db.Find(id);
            if (want == null)
            {
                into.Notes.Add(what + ": ID " + id + " is missing from the GrimTools database");
                into.Unmatched++;
                return null;
            }
            List<string> how = new List<string>();
            CatalogEntry got = PickItem(index, want, how);
            if (got == null)
            {
                // Distinguish ambiguous local matches from records absent in the
                // installed game; only the former can be fixed by changing keys.
                List<CatalogEntry> bag;
                bool known = index.ByTag.TryGetValue(want.NameTag ?? "", out bag) && bag.Count > 0;
                into.Notes.Add(what + (known ? " could not match: " : " is missing from the game records: ") +
                               id + " (" + want.NameTag + ", " + want.Bitmap + ", level " + want.Level +
                               ", " + want.Rarity + (known ? ", game records with this tag: " + bag.Count : "") + ")");
                into.Unmatched++;
                if (!known) into.Absent++;
                return null;
            }
            into.Notes.Add(what + ": " + got.Name + "   [" + string.Join(", ", how.ToArray()) + "]");
            return got.Dbr;
        }

        static string Affix(GtDb db, Index index, string id, GtItem into, string what)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GtEntry want = db.Find(id);
            if (want == null)
            {
                into.Notes.Add(what + ": ID " + id + " is missing from the GrimTools database");
                into.Unmatched++;
                return null;
            }
            List<string> how = new List<string>();
            DbRecord got = PickAffix(index, want, how);
            if (got == null)
            {
                into.Notes.Add(what + " could not match: " + id + " (" + want.LootName + ", level " + want.Level + ")");
                into.Unmatched++;
                return null;
            }
            into.Notes.Add(what + ": " + got.Path + "   [" + string.Join(", ", how.ToArray()) + "]");
            return got.Path;
        }

        static string Ascension(GameData game, GtDb db, Index index, string id, GtItem into)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GtEntry want = db.Find(id);
            if (want == null)
            {
                into.Notes.Add("ascension: ID " + id + " is missing from the GrimTools database");
                into.Unmatched++;
                return null;
            }
            List<string> how = new List<string>();
            DbRecord got = PickAscension(game, db, index, want, how);
            if (got == null)
            {
                GtEntry mod = string.IsNullOrEmpty(want.ModifierSkill) ? null : db.Find(want.ModifierSkill);
                into.Notes.Add("could not match ascension: " + id + "   signature [" + want.Sig + "]   modifier " +
                               want.ModifierSkill + " [" + (mod == null ? "record missing" : mod.Sig) + "]");
                into.Unmatched++;
                return null;
            }
            into.Notes.Add("ascension: " + got.Path + "   [" + string.Join(", ", how.ToArray()) + "]");
            return got.Path;
        }

        static string Bonus(GtDb db, Index index, string id, GtItem into)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GtEntry want = db.Find(id);
            if (want == null)
            {
                into.Notes.Add("relic bonus: ID " + id + " is missing from the GrimTools database");
                into.Unmatched++;
                return null;
            }
            List<string> how = new List<string>();
            DbRecord got = PickBonus(index, want, how);
            if (got == null)
            {
                into.Notes.Add("could not match relic bonus: " + id + "   [" + want.Sig + "]   variance " +
                               want.Jitter + ", " + want.Rarity + ", level " + want.Level);
                into.Unmatched++;
                return null;
            }
            into.Notes.Add("relic bonus: " + got.Path + "   [" + string.Join(", ", how.ToArray()) + "]");
            return got.Path;
        }

        // A SKILL IS NOT ALWAYS TOLD APART BY ITS TAG AND ITS PICTURE, and a devotion
        // star never is. An ordinary star is named by the tag of its CONSTELLATION —
        // `tier2_17a`, `b`, `d` and `e` all say `tagDevotion_B17` — and every star in
        // the game wears one and the same picture, `skillicon_devotionstar01_up`. So
        // the two strictest keys we had left four candidates standing, the first of
        // them was taken, and all four stars of a constellation became one: a build of
        // many stars may otherwise collapse to a small set of repeated records.
        //
        // WHAT DOES TELL THEM APART IS THE NUMBERS, the same answer the ascensions and
        // the affixes already use: one star is +25 physique and +15 health, the next is
        // +40 offensive ability. Both sides hold those under the game's own field
        // names, so the signatures are comparable as they stand.
        // THE MARK OF A SKILL TAKEN ON A GUESS: several of ours answered the tag and
        // the picture, their numbers agreed with none of them, and the first was
        // taken. The import counts these and the report names the number, because a
        // guess that says nothing is how fifty stars turned into thirteen.
        public const string Guessed = "tag and icon matched, stats differed. Used the first match";

        // THE MARK OF A DISAGREEMENT between the two ways of telling two stars of one
        // constellation apart — their numbers and their place in it. It must never
        // happen: it would mean their table numbers the buttons otherwise than the
        // game's own record does, and then the place is no key at all.
        public const string Mixed = "stats and constellation position point to different stars";

        // Which of these records sits on the constellation's button number `button`.
        static string Place(Index index, List<string> among, int button)
        {
            if (button <= 0) return null;
            foreach (string dbr in among)
            {
                int mine;
                if (index.Button.TryGetValue(dbr, out mine) && mine == button) return dbr;
            }
            return null;
        }

        // `--gtstarsbreak` — tell the stars apart by the tag and the picture ONLY, the
        // way it was done until 2 October, so that the check which counts DISTINCT
        // records can be seen going red (PLAN, rule 12).
        public static bool BreakStarNumbers;

        // `--gtmasterybreak` — read `masteries` only as an ARRAY, as the code did until
        // 19 October. A build that sends them as an OBJECT then leaves the list empty, the
        // mastery trees go unwalked, and class skills land on the end of their chain instead
        // of the tree's button (MECHANICS 158). Nothing else reads this.
        public static bool BreakMasteryObject;

        static string Skill(GameData game, GtDb db, Index index, string id, List<string> how)
        {
            GtEntry want = db.Find(id);
            if (want == null) return null;
            // A shell record hands off along the chain — they have the same step we
            // do (`NextSkillRecord`): it carries neither the tag nor the icon.
            for (int hop = 0; hop < 3 && want != null && string.IsNullOrEmpty(want.SkillTag); hop++)
                want = string.IsNullOrEmpty(want.BuffSkill) ? null : db.Find(want.BuffSkill);
            if (want == null) return null;
            List<string> bag;
            if (!index.Skill.TryGetValue(want.SkillTag, out bag) || bag.Count == 0) return null;
            if (bag.Count == 1) { how.Add("skill tag"); return bag[0]; }
            // The picture narrows the field but is not asked to settle it: when it
            // leaves one candidate that one is the answer, and when it leaves none at
            // all the whole bag is kept rather than the search given up.
            List<string> pool = new List<string>();
            foreach (string dbr in bag)
                if (Art(SkillText.IconOf(game, dbr)) == Art(want.SkillIcon)) pool.Add(dbr);
            if (pool.Count == 0) pool = bag;
            if (pool.Count == 1) { how.Add("tag + icon"); return pool[0]; }
            if (BreakStarNumbers) { how.Add("tag + icon (regression probe)"); return pool[0]; }
            // THE CONSTELLATION FIRST, where there is one: it is the only thing that
            // distinguishes same-tag constellations whose stars match each other
            // down to the numbers.
            if (!string.IsNullOrEmpty(want.Home))
            {
                List<string> home = new List<string>();
                foreach (string dbr in pool)
                {
                    string mine;
                    if (index.Home.TryGetValue(dbr, out mine) && mine == want.Home) home.Add(dbr);
                }
                if (home.Count == 1) { how.Add("tag + constellation"); return home[0]; }
                if (home.Count > 1) pool = home;
            }
            List<string> same = new List<string>();
            foreach (string dbr in pool)
                if (SigAtTag(game, dbr) == want.Sig) same.Add(dbr);
            // TWO STARS OF ONE CONSTELLATION CAN BE THE SAME RECORD WORD FOR WORD.
            // Two records may share the same displayed name and both grant +20 defensive
            // ability and +25 offensive, and nothing else about them differs — so a
            // build taking both put the first one in twice. What is left is WHERE THE
            // STAR SITS: the constellation's record numbers its buttons, and their
            // table is a transcription of that same record, so button N ought to be
            // the same star on both sides.
            //
            // THAT «OUGHT» IS NOT TAKEN ON TRUST. Wherever the numbers settle it on
            // their own, the place is asked too and the two answers are compared; a
            // disagreement is counted and the check goes red on it (`Mixed`).
            string byPlace = Place(index, same, want.HomeButton);
            if (same.Count == 1)
            {
                if (byPlace != null && byPlace != same[0]) { how.Add(Mixed); return same[0]; }
                how.Add("tag + constellation + stats");
                return same[0];
            }
            if (same.Count > 1)
            {
                if (byPlace != null) { how.Add("tag + constellation + stats + position"); return byPlace; }
                how.Add("matched by stats (identical records: " + same.Count + ")");
                return same[0];
            }
            // NOTHING TOLD THEM APART, and the first is taken as before — but it is
            // SAID, and counted, because silence is exactly what hid the collapse.
            how.Add(Guessed + " (records: " + pool.Count + ")");
            return pool[0];
        }

        // Our signature of the record THAT CARRIES THE TAG, not of the one the save
        // holds. Their entry was hopped along the hand-off chain to the tag just
        // above, so ours has to be read at the same place or the numbers would be
        // compared across two different records.
        static string SigAtTag(GameData game, string dbr)
        {
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return "";
                if (!string.IsNullOrEmpty(rec.Get("skillDisplayName"))) return SigOf(rec);
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return "";
        }

        // ------------------------------------------- indexes over the game

        static ItemCatalog catalog;
        static GameData catalogFor;

        // The same catalogue cache the main window uses: built — we read it; stale —
        // we build it again and put it back.
        static ItemCatalog Catalog(GameData game)
        {
            if (catalog != null && catalogFor == game) return catalog;
            ItemCatalog c = ItemCatalog.Load(ItemCatalog.CachePath);
            if (c == null || !c.MatchesGame(game.GameDir, game.Language))
            {
                c = ItemCatalog.Build(game.GameDir, game.Language, null);
                try { c.Save(ItemCatalog.CachePath); } catch (Exception) { }
            }
            catalogFor = game;
            catalog = c;
            return catalog;
        }

        static readonly Dictionary<string, CatalogEntry> byDbr =
            new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

        static CatalogEntry FindEntry(GameData game, string dbr)
        {
            if (byDbr.Count == 0)
            {
                ItemCatalog c = Catalog(game);
                if (c != null) foreach (CatalogEntry e in c.Entries) byDbr[e.Dbr] = e;
            }
            CatalogEntry got;
            return byDbr.TryGetValue(dbr ?? "", out got) ? got : null;
        }

        static Index BuildIndex(GameData game, GtDb db, GtCharacter into, List<string> log)
        {
            Index index = new Index();

            ItemCatalog c = Catalog(game);
            if (c == null)
            {
                into.Problems.Add("could not build the item catalog");
                into.NoCatalog = true;
                return index;
            }
            foreach (CatalogEntry e in c.Entries) Add(index.ByTag, e.NameTag, e);
            if (log != null) log.Add("items in the local catalog: " + c.Entries.Count);

            // The affixes come from a walk of the archives: the item catalogue does
            // not have them (they are `LootRandomizer` records, not items), and a
            // walk of the four databases filtered by path takes a couple of seconds.
            // A later archive wins, the same as everywhere.
            // ONE WALK FOR BOTH: the affixes and the skills a build can name. Two
            // walks of four databases would cost twice as much for nothing.
            Dictionary<string, DbRecord> records = new Dictionary<string, DbRecord>(StringComparer.OrdinalIgnoreCase);
            string[] bases = { @"database\database.arz", @"gdx1\database\GDX1.arz",
                               @"gdx2\database\GDX2.arz", @"gdx3\database\GDX3.arz" };
            foreach (string rel in bases)
            {
                string path = System.IO.Path.Combine(game.GameDir, rel);
                if (!System.IO.File.Exists(path)) continue;
                Arz.ForEachRecord(path,
                    delegate (string p)
                    {
                        return p.IndexOf("/lootaffixes/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               p.IndexOf("/skills/playerclass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               p.IndexOf("/skills/devotion/", StringComparison.OrdinalIgnoreCase) >= 0;
                    },
                    delegate (DbRecord r) { records[r.Path] = r; });
            }
            int named = 0, ascended = 0, bonuses = 0;
            foreach (KeyValuePair<string, DbRecord> kv in records)
            {
                if (kv.Value.Path.IndexOf("/lootaffixes/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                DbRecord r = kv.Value;
                string name = r.Get("lootRandomizerName");
                if (!string.Equals(r.Class, "LootRandomizer", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(name)) { Add(index.Affix, name, r); named++; }
                // An ascension carries its work in a record of its own, so the two
                // sets of numbers are keyed together.
                if (r.Path.IndexOf("/ascended/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DbRecord modifier = game.Record(r.Get("modifierSkillName1"));
                    Add(index.Ascended, AscensionKey(SigOf(r), SigOf(modifier),
                                                     TagOf(game, r.Get("modifiedSkillName1")),
                                                     TagOf(game, r.Get("augmentSkillName1")),
                                                     TagOf(game, r.Get("augmentSkillName2"))), r);
                    ascended++;
                    continue;
                }
                // EVERYTHING ELSE GOES INTO THE BY-NUMBERS INDEX TOO, named or not:
                // prefixes and suffixes whose tag is shared by a dozen records, relic
                // completion bonuses (two folders: `completion` and `completionrelics`),
                // and the affixes that carry no name at all.
                Add(index.Bonus, BonusKey(SigOf(r), r.GetInt("lootRandomizerJitter"),
                                          r.Get("itemClassification"), r.GetInt("levelRequirement"),
                                          TagOf(game, r.Get("augmentSkillName1")),
                                          TagOf(game, r.Get("augmentSkillName2"))), r);
                bonuses++;
            }
            if (log != null) log.Add("game affixes: " + named + " named, " + bonuses +
                                     " matched by stats, ascensions: " + ascended);

            // Skills: only the ones a build can possibly hold — both masteries, every
            // devotion star, and whatever those hand off to. There is no call to walk
            // every skill record the game ships.
            int skills = 0;
            foreach (string number in into.Masteries)
            {
                foreach (string dbr in AttackSkills.Attach.SkillsOfFolder(game, "playerclass" + number))
                    skills += Remember(game, index, dbr);
                // The mastery bar is not in the tree's list of buttons — it stands as
            // a record of its own, and without it the build did not match.
                int n;
                if (int.TryParse(number, out n)) skills += Remember(game, index, MasteryEdit.Bar(n));
            }
            foreach (Constellation k in DevotionTable.Of(game))
            {
                // THE CONSTELLATION IS REMEMBERED BESIDE THE STAR, because the star's
                // own record does not name it and five constellations of the game
                // share a tag, a picture AND their numbers — see the write-up above
                // `GrimTools.ReadConstellations`.
                string home = GrimTools.HomeSignature(
                    new SortedDictionary<string, int>(k.Gives, StringComparer.Ordinal),
                    new SortedDictionary<string, int>(k.Needs, StringComparer.Ordinal), k.Stars.Count);
                foreach (string star in k.Stars)
                {
                    skills += Remember(game, index, star);
                    index.Home[star] = home;
                }
                foreach (DevotionStar b in k.Buttons) index.Button[b.Dbr] = b.Button;
            }

            // AND THEN EVERYTHING ELSE UNDER `records/skills/`, BUT ONLY TO FILL GAPS.
            // The tree does not list everything a build can hold: the skills a
            // transform grants are one case: the form brings two of its own,
            // and they are in no `classNN/skillNN.dbr`. A tag the tree already claimed
            // is left alone, though — otherwise the buff record would be offered
            // beside the tree record it belongs to, and the save wants the tree one.
            int extra = 0;
            foreach (KeyValuePair<string, DbRecord> kv in records)
            {
                DbRecord r = kv.Value;
                if (r.Path.IndexOf("/skills/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string tag = r.Get("skillDisplayName");
                if (string.IsNullOrEmpty(tag) || index.Skill.ContainsKey(tag)) continue;
                Add(index.Skill, tag, r.Path);
                extra++;
            }
            if (log != null) log.Add("skill candidates: " + skills + " from skill trees and " +
                                     extra + " others");
            return index;
        }

        // A skill and the whole chain it hands off along.
        //
        // THE FIRST RECORD IS WHAT IS REMEMBERED, WHILE THE TAG IS TAKEN FROM
        // WHEREVER IT LIES. For half the mastery skills the tree's record is a shell
        // with nothing but a `buffSkillName`, and the name and the icon sit on the
        // buff (one record may be only a shell). The save, however, holds THE TREE'S
        // RECORD, and that is what the editor expects from us — so that is what goes
        // into the index, not the buff. GrimTools has the same pair and the same step.
        static int Remember(GameData game, Index index, string dbr)
        {
            string original = dbr;
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) break;
                string tag = rec.Get("skillDisplayName");
                if (!string.IsNullOrEmpty(tag))
                {
                    List<string> bag;
                    if (!index.Skill.TryGetValue(tag, out bag) || !bag.Contains(original))
                    { Add(index.Skill, tag, original); return 1; }
                    return 0;
                }
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return 0;
        }

        // ------------------------------------------------------ small change

        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        static int Int(JsonElement e, string name)
        {
            JsonElement v;
            if (!e.TryGetProperty(name, out v)) return 0;
            if (v.ValueKind == JsonValueKind.Number) return (int)Math.Round(v.GetDouble());
            int n;
            return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : 0;
        }
    }
}
