// Stack limits and compatibility rules for inventory items.
//
// Saves store `StackCount`; consumables, components, augments, and quest items may
// form stacks. The editor uses this value when moving and merging items.
//
// Limits come from the item record or the game-engine record:
//
//   1. `maxStackSize` in the item record, when present. `ItemUsableSkill` records
//      normally use 1 and therefore do not stack;
//   2. `records/game/gameengine.dbr` contains class-wide limits:
//          potionStackLimit    100
//          scrollStackLimit    100
//          questItemStackLimit 100
//      These limits are selected by record class: potions, scroll-like consumables,
//      and quest items.
//
// Potion stacks observed in unmodified saves confirm that `potionStackLimit` is 100.
//
// Component limits are unknown: neither `maxStackSize` nor the engine record defines
// one. They therefore use `NoLimit`; bulk transfer still uses `BulkWhenUnknown`.
//
// `--stacktest` validates stack classes and known limits across save files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    public static partial class Stacking
    {
        // No known limit.
        public const int NoLimit = int.MaxValue;

        // Classes that may stack. `--stacktest` rejects other classes with stacks.
        static readonly string[] StackClasses = {
            "ItemRelic",              // components
            "ItemEnchantment",        // augments
            "QuestItem",
            "OneShot_PotionHealth", "OneShot_PotionMana", "OneShot_Food",
            "OneShot_Scroll",         // scrolls and similar consumables
            "OneShot_SkillUnlock",
            "ItemUsableSkill",
            "ItemArtifactFormula", "ItemAscensionFormula", "ItemSetFormula",
            "ItemRandomSetFormula", "ItemRerollFormula",
            "ItemAttributeReset", "ItemDevotionReset",
        };

        // Maximum items per stack. 1 means non-stackable; `NoLimit` means unknown.
        public static int LimitOf(GameData game, DbRecord rec)
        {
            if (rec == null) return 1;
            // An explicit record value has priority.
            int own = rec.GetInt("maxStackSize");
            if (own > 0) return own;
            string cls = rec.Class ?? "";
            if (Index(StackClasses, cls) < 0) return 1;
            switch (cls)
            {
                case "OneShot_PotionHealth":
                case "OneShot_PotionMana":
                // Food uses the potion limit until a save provides contrary evidence.
                case "OneShot_Food":
                    return Engine(game, "potionStackLimit");
                case "OneShot_Scroll":
                    return Engine(game, "scrollStackLimit");
                // Quest-item engine limits do not describe the inventory stack limit;
                // keep quest items unrestricted here.
                case "QuestItem":
                    return NoLimit;
                default:
                    return NoLimit;
            }
        }

        public static bool Stacks(GameData game, DbRecord rec) { return LimitOf(game, rec) != 1; }

        // Bulk-transfer amount: use the known limit, or the fallback agreement for
        // unknown limits.
        public const int BulkWhenUnknown = 100;

        public static int Bulk(GameData game, DbRecord rec)
        {
            int limit = LimitOf(game, rec);
            return limit == NoLimit ? BulkWhenUnknown : limit;
        }

        // Items stack only when they share a base record and have no instance-specific
        // affix, component, augment, or ascension. Seeds are shared by the stack.
        public static bool SameKind(Item a, Item b)
        {
            if (a == null || b == null || a == b) return false;
            if (!string.Equals(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase)) return false;
            return Plain(a) && Plain(b);
        }

        static bool Plain(Item it)
        {
            return string.IsNullOrEmpty(it.PrefixName) && string.IsNullOrEmpty(it.SuffixName) &&
                   string.IsNullOrEmpty(it.RelicName) && string.IsNullOrEmpty(it.AugmentName) &&
                   string.IsNullOrEmpty(it.AscensionName) && string.IsNullOrEmpty(it.ModifierName);
        }

        // Remaining capacity of a stack.
        public static long Room(GameData game, DbRecord rec, Item target)
        {
            int limit = LimitOf(game, rec);
            if (limit == NoLimit) return long.MaxValue;
            long have = target == null ? 0 : target.StackCount;
            return limit - have;
        }

        // ============================== ENGINE DATA ==============================

        [ThreadStatic] static Dictionary<string, int> engine;

        static int Engine(GameData game, string field)
        {
            if (engine == null) engine = new Dictionary<string, int>(StringComparer.Ordinal);
            int value;
            if (engine.TryGetValue(field, out value)) return value;
            DbRecord rec = game == null ? null : game.Record("records/game/gameengine.dbr");
            value = rec == null ? NoLimit : rec.GetInt(field);
            if (value <= 0) value = NoLimit;
            engine[field] = value;
            return value;
        }

        static int Index(string[] set, string value)
        {
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

    }
}
