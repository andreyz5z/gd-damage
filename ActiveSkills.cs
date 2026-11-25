// Active skills are selected by the user, not inferred from saved state.
//
// The saved active flag can be stale or incomplete, while the user can see the
// actual game state. Store explicit decisions per character and skill record.
// Only explicit overrides are stored; untouched skills keep the existing rules.
// Choices live for the current process only. They are runtime game state, not
// editor settings, and must not be persisted in `active-skills.json`.
// Standalone tests build their own characters and use `#toggle`; they do not
// inherit choices from the damage window.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Gdc.Gui
{
    public class ActiveSkills
    {
        // One row in the active-skills window.
        public class Candidate
        {
            public string Dbr;        // skill record; decision key
            public string Title;      // display name
            public string Source;     // mastery, item, set, or devotion
            public string Why;        // reason for the inferred state
            public bool Default;      // inferred state
            public bool Chosen;       // user choice, or Default when unset
            public bool Decided;      // whether the user made a choice
        }

        // Explicit user decisions: skill record -> active state.
        readonly Dictionary<string, bool> choice = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool Has(string dbr)
        { return dbr != null && choice.ContainsKey(dbr); }

        // null means there is no override; use the previous inference.
        public bool? For(string dbr)
        {
            bool v;
            return dbr != null && choice.TryGetValue(dbr, out v) ? (bool?)v : null;
        }

        public void Set(string dbr, bool on)
        { if (!string.IsNullOrEmpty(dbr)) choice[dbr] = on; }

        // Return a copy with additional temporary selections.
        // The timeline uses this for timed buffs; the user's base choices must
        // remain unchanged.
        public ActiveSkills With(IEnumerable<string> alsoOn) { return With(alsoOn, null); }

        // `alsoOff` removes a persistent choice for the temporary simulation.
        // Otherwise a timed effect could be counted both as persistent and timed.
        public ActiveSkills With(IEnumerable<string> alsoOn, IEnumerable<string> alsoOff)
        {
            ActiveSkills made = new ActiveSkills();
            foreach (KeyValuePair<string, bool> kv in choice) made.choice[kv.Key] = kv.Value;
            if (alsoOff != null)
                foreach (string one in alsoOff)
                    if (!string.IsNullOrEmpty(one)) made.choice[one] = false;
            if (alsoOn != null)
                foreach (string one in alsoOn)
                    if (!string.IsNullOrEmpty(one)) made.choice[one] = true;
            return made;
        }

        public void Forget(string dbr)
        { if (dbr != null) choice.Remove(dbr); }

        public int Count { get { return choice.Count; } }

        // ------------------------------------------------------------ storage

        // In-memory storage for the current process. The key is the character;
        // multiple damage windows may share the same character.
        static readonly Dictionary<string, ActiveSkills> perCharacter =
            new Dictionary<string, ActiveSkills>(StringComparer.CurrentCultureIgnoreCase);

        public static ActiveSkills Of(string characterName)
        {
            string key = characterName ?? "";
            ActiveSkills made;
            if (!perCharacter.TryGetValue(key, out made))
            { made = new ActiveSkills(); perCharacter[key] = made; }
            return made;
        }

        // Enable skills by display name for command-line tests (`--active=...`).
        // Unknown names are reported instead of silently producing an empty set.
        public bool TurnOnByName(GameData game, Character c, string names, out string unknown)
        {
            unknown = null;
            if (string.IsNullOrEmpty(names)) return true;
            List<Candidate> all = Of(game, c, null);
            List<string> lost = new List<string>();
            foreach (string raw in names.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string selector = raw.Trim();
                string want = game.ResolveNameSelector(selector);
                bool found = false;
                foreach (Candidate one in all)
                {
                    if (!string.Equals(one.Dbr, selector, StringComparison.OrdinalIgnoreCase) &&
                        (want == null || !string.Equals(one.Title, want, StringComparison.CurrentCultureIgnoreCase))) continue;
                    Set(one.Dbr, true); found = true;
                }
                if (!found) lost.Add(selector);
            }
            if (lost.Count == 0) return true;
            unknown = string.Join(", ", lost.ToArray());
            return false;
        }

        // ------------------------------------------------------------ candidates

        // Collect all skills that may be active: mastery toggles and grants from
        // items, components, augments, sets, and devotion.
        // Procs are included so the user can override uncertain inference.
        // Conditions that the timeline cannot model use localized short labels;
        // internal condition identifiers from game records are not translated.
        public static string TriggerName(string t)
        {
            if (t == "AttackEnemy") return Lang.T("active.trigger.attack");
            if (t == "AttackEnemyCrit") return Lang.T("active.trigger.attackCrit");
            return Trigger(t);
        }

        static string Trigger(string t)
        {
            switch (t)
            {
                case "LowHealth":       return Lang.T("active.trigger.lowHealth");
                case "LowMana":         return Lang.T("active.trigger.lowMana");
                case "HitByEnemy":      return Lang.T("active.trigger.hitByEnemy");
                case "HitByMelee":      return Lang.T("active.trigger.hitByMelee");
                case "HitByProjectile": return Lang.T("active.trigger.hitByProjectile");
                case "HitByCrit":       return Lang.T("active.trigger.hitByCrit");
                case "Block":           return Lang.T("active.trigger.block");
                case "OnKill":          return Lang.T("active.trigger.onKill");
                case "CastBuff":        return Lang.T("active.trigger.castBuff");
                case "OnEquip":         return Lang.T("active.trigger.onEquip");
                default:                return t;
            }
        }

        public static List<Candidate> Of(GameData game, Character c, ActiveSkills chosen)
        {
            List<Candidate> list = new List<Candidate>();
            if (game == null || c == null) return list;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Mastery toggles.
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                if (SkillsForm.IsDevotion(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || !CharacterTotals.IsToggleClass(rec.Class)) continue;
                if (!seen.Add(s.Name)) continue;
                Candidate one = new Candidate();
                one.Dbr = s.Name;
                one.Title = SkillText.Name(game, s.Name) ?? s.Name;
                // Group labels come from the language files. `Why` is used only
                // by `--activeshot`, not displayed in this window.
                one.Source = Lang.T("common.source.mastery");
                // Toggle buffs are active by default; the saved `act` flag is
                // informational and does not override this rule.
                one.Default = true;
                one.Why = "Toggle buff, assumed active" +
                          (s.Active != 0 ? " (enabled in the save)" : " (disabled in the save)");
                list.Add(one);
            }

            // 1b. Clickable mastery buffs.
            // These are temporary and default to off. Modifiers attached to a
            // buff are counted only when its parent is active.
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                if (SkillsForm.IsDevotion(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;
                string cls = rec.Class ?? "";
                if (!cls.StartsWith("Skill_BuffSelf", StringComparison.OrdinalIgnoreCase)) continue;
                if (CharacterTotals.IsToggleClass(cls)) continue;      // handled above
                if (!seen.Add(s.Name)) continue;
                Candidate one = new Candidate();
                one.Dbr = s.Name;
                one.Title = SkillText.Name(game, s.Name) ?? s.Name;
                one.Source = Lang.T("common.source.mastery");
                one.Default = false;
                one.Why = "Activated buff with limited uptime, excluded from idle stats";
                list.Add(one);
            }

            // 1c. Devotion skills with conditions not modeled by the timeline.
            // The user decides whether these effects are treated as active.
            // `LowHealth` and `HitByEnemy` belong here; `AttackEnemy` is handled
            // by the timeline's normal roll.
            foreach (Timeline.Star st in Timeline.ByOtherCondition(game, c))
            {
                // An attacking devotion skill is not a persistent buff. It is
                // handled as a timeline action, not added to every hit.
                if (AttackSkills.One(game, st.Dbr, Timeline.DevotionLevelOf(c, st.Dbr), Lang.T("common.source.devotion")) != null)
                    continue;
                if (!seen.Add(st.Dbr)) continue;
                Candidate one = new Candidate();
                one.Dbr = st.Dbr;
                one.Title = st.Name;
                one.Source = Lang.T("active.source.devotionOn", st.ActivatorName);
                one.Default = false;
                one.Why = "Trigger: " + Trigger(st.Trigger) +
                          (st.Chance > 0.0001 ? ", chance " + st.Chance.ToString("0.#") + "%" : "") +
                          ". Enable it to have the simulation activate it on cooldown.";
                list.Add(one);
            }

            // 1a. Non-passive devotion skills.
            // Passive devotion skills are always active. Other devotion skills
            // may grant bonuses or pets and therefore require an explicit choice.
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                if (!SkillsForm.IsDevotion(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;
                if ((rec.Class ?? "").StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)) continue;
                // Ignore empty bindings; summon skills use the separate pet pool.
                DbRecord holder;
                if (ItemViewFactory.PetBonusOf(game, s.Name, out holder) == null &&
                    string.IsNullOrEmpty(PetPool.SpawnList(rec))) continue;
                if (!seen.Add(s.Name)) continue;
                Candidate one = new Candidate();
                one.Dbr = s.Name;
                one.Title = SkillText.Name(game, s.Name) ?? s.Name;
                one.Source = Lang.T("common.source.devotion");
                one.Default = false;
                one.Why = "Conditional devotion proc, assumed inactive";
                list.Add(one);
            }

            // 2. Skills granted by equipped items, components, augments, and
            // ascensions. Procs are included as described above.
            List<Item> worn = new List<Item>();
            // Only the equipped weapon set is active. The reserve set contributes
            // nothing until selected.
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            foreach (Item it in worn)
            {
                if (it == null || string.IsNullOrEmpty(it.BaseName)) continue;
                string itemName = ItemViewFactory.Build(game, it, false).Name ?? it.BaseName;
                string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                                   it.RelicName, it.AugmentName, it.AscensionName };
                string[] what = PetPool.ItemPartWords();
                for (int i = 0; i < parts.Length; i++)
                {
                    DbRecord pr = string.IsNullOrEmpty(parts[i]) ? null : game.Record(parts[i]);
                    if (pr == null) continue;
                    string granted = pr.Get("itemSkillName");
                    if (string.IsNullOrEmpty(granted)) continue;
                    DbRecord gr = game.Record(granted);
                    if (gr == null || !seen.Add(granted)) continue;
                    Candidate one = new Candidate();
                    one.Dbr = granted;
                    one.Title = SkillText.Name(game, granted) ?? granted;
                    one.Source = Lang.T("common.source.item", itemName + what[i]);
                    bool proc = !string.IsNullOrEmpty(pr.Get("itemSkillAutoController"));
                    bool buff = (gr.Class ?? "").IndexOf("BuffSelf", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Toggle buffs are active by default, identified by their
                    // class rather than by the generic self-buff check.
                    bool toggled = CharacterTotals.IsToggleClass(gr.Class);
                    int reserve = gr.GetInt("characterManaLimitReserve");
                    // A self-attacking aura remains active but grants no bonus to
                    // the character. Keep it unchecked to match the total.
                    bool selfHits = toggled && (gr.Class ?? "").IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;
                    one.Default = !proc && !selfHits && (toggled || (buff && reserve > 0));
                    one.Why = proc ? "Conditional proc, assumed inactive"
                            : selfHits ? "Aura hits on its own and is always active. The buff applies to the aura."
                            : toggled ? "Toggle buff, assumed active"
                            : !buff ? "Does not apply a self-buff, assumed inactive"
                            : reserve <= 0 ? "Reserves no energy, assumed inactive"
                            : "Reserves " + reserve + " energy, assumed active";
                    list.Add(one);
                }
            }

            // 2b. Skills granted by item sets. Set records use the same proc,
            // energy, and toggle rules; only the granted level depends on the
            // number of equipped pieces.
            foreach (KeyValuePair<string, int> kv in ItemSkillMods.SetSizes(game, worn))
            {
                DbRecord pr = game.Record(kv.Key);
                if (pr == null) continue;
                string granted = pr.Get("itemSkillName");
                if (string.IsNullOrEmpty(granted)) continue;
                if (ItemSkillMods.GrantedLevelOfSet(pr, kv.Value) <= 0) continue;
                DbRecord gr = game.Record(granted);
                if (gr == null || !seen.Add(granted)) continue;
                Candidate one = new Candidate();
                one.Dbr = granted;
                one.Title = SkillText.Name(game, granted) ?? granted;
                one.Source = Lang.T("common.source.set", ItemViewFactory.SetNameOf(game, pr) ?? kv.Key, kv.Value);
                bool proc = !string.IsNullOrEmpty(pr.Get("itemSkillAutoController"));
                bool buff = (gr.Class ?? "").IndexOf("BuffSelf", StringComparison.OrdinalIgnoreCase) >= 0;
                bool toggled = CharacterTotals.IsToggleClass(gr.Class);
                int reserve = gr.GetInt("characterManaLimitReserve");
                bool selfHits = toggled &&
                                (gr.Class ?? "").IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;
                one.Default = !proc && !selfHits && (toggled || (buff && reserve > 0));
                one.Why = proc ? "Conditional proc, assumed inactive"
                        : selfHits ? "Aura hits on its own and is always active. The buff applies to the aura."
                        : toggled ? "Toggle buff, assumed active"
                        : !buff ? "Does not apply a self-buff, assumed inactive"
                        : reserve <= 0 ? "Reserves no energy, assumed inactive"
                        : "Reserves " + reserve + " energy, assumed active";
                list.Add(one);
            }

            // Apply explicit user choices over the inferred state.
            foreach (Candidate one in list)
            {
                bool? said = chosen == null ? null : chosen.For(one.Dbr);
                one.Decided = said.HasValue;
                one.Chosen = said ?? one.Default;
            }
            list.Sort(delegate (Candidate a, Candidate b)
            {
                int by = string.Compare(a.Source, b.Source, StringComparison.CurrentCulture);
                return by != 0 ? by : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            return list;
        }
    }
}
