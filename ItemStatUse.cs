// WHERE EVERY ITEM FIELD GOES IN OUR OWN CALCULATION.
//
// This answers a question neither `--statcoverage` (which is about tooltip captions) nor
// `--skillcoverage` asked. A field can have a fine caption, be honestly drawn in the tooltip —
// and be added up nowhere. That is exactly how chance damage lived for a month and a half, and
// so did skill conversions and item modifiers to skills: visible to the eye, absent from the
// numbers.
//
// The marking was assembled by reading our own code, family by family, and it is kept next to
// the census (`--itemstats`) on purpose: you cannot add a reading of a field and forget the
// census — the census goes red on an unmarked field, and marking one without reading the code
// means lying to yourself.
//
// "NOWHERE" is not a verdict on a field but a list of work. The character panel and the tooltip
// show far more than takes part in the damage-per-hit calculation, and that is fine for exactly
// as long as we know about it.

using System;

namespace Gdc.Gui
{
    public static class ItemStatUse
    {
        // The eleven damage types that field names are assembled from.
        static readonly string[] Types = {
            "Physical", "Pierce", "Fire", "Cold", "Lightning",
            "Poison", "Life", "Aether", "Chaos", "Bleeding", "Elemental",
        };

        // Fields that are no bonus at all: how the item looks, what it costs, where it drops,
        // which slot and which weapon it fits. Sifted out first, or the real ones cannot be
        // seen behind them.
        static readonly string[] NotABonus = {
            "scale", "maxTransparency", "outlineThickness", "physics", "castsShadows", "actor",
            "marketAdjustment", "lootRandomizer", "randomizer", "bitmap", "Bitmap", "texture",
            "Texture", "mesh", "Mesh", "shader", "Shader", "sound", "Sound", "drop", "Drop",
            "armorMale", "armorFemale", "medalVisible", "itemVisual", "fx", "Fx", "FX",
            "attachTag", "hidePlayer", "useAnimation", "instanceCount", "itemStackable",
            "itemNameTag", "itemText", "description", "FileDescription", "templateName", "Class",
            "itemCostName", "bonusTable", "itemSkillModifierControl", "modifiedSkillName",
            "modifierSkillName", "augmentSkillName", "augmentMasteryName", "petSkillName",
            "buffSkillName", "skillDisplayName", "skillBaseDescription", "experienceBonus",
            "lootWeight", "ascendedAffixTable", "bellSlope", "cannotPickUp", "hidePrefixName",
            "hideSuffixName", "hideFeet", "hideHead", "itemCostScalePercent", "levels",
        };

        // The "fits which weapon or which slot" flags — the same names that on a SKILL mean a
        // weapon requirement, while on an ITEM they mean compatibility.
        static readonly string[] SlotFlags = {
            "amulet", "ring", "medal", "chest", "head", "shoulders", "hands", "legs", "feet",
            "waist", "offhand", "shield", "sword", "axe", "mace", "dagger", "scepter", "spear",
            "ranged1h", "ranged2h", "sword2h", "axe2h", "mace2h", "spear2h", "staff", "gun",
            "relic", "weapon",
        };

        public static string Of(string field)
        {
            if (string.IsNullOrEmpty(field)) return null;
            foreach (string s in SlotFlags)
                if (string.Equals(field, s, StringComparison.Ordinal)) return "not a bonus: what it fits";
            foreach (string s in NotABonus)
                if (field.IndexOf(s, StringComparison.Ordinal) >= 0) return "not a bonus: looks, price, drop";

            // ---- damage per hit: DamageModel ----
            foreach (string t in Types)
            {
                // The base and the flat additions: three families, minimum and maximum.
                if (field == "offensive" + t + "Min" || field == "offensive" + t + "Max" ||
                    field == "offensiveBonus" + t + "Min" || field == "offensiveBonus" + t + "Max" ||
                    field == "offensiveBase" + t + "Min" || field == "offensiveBase" + t + "Max")
                    return "damage: flat";
                if (field == "offensive" + t + "Modifier") return "damage: type percent";
                // The chance on flat damage and on a dot from items has been read since
                // 9 August 2026. Before that the rule "a chance enters as its expected value"
                // stood in three places and all three were incomplete: `SklFlat` and `SkillDot`
                // applied it to skills, and `CharacterTotals.Add` only to `Modifier` fields.
                // It was the census that found this hole.
                if (field == "offensive" + t + "Chance" || field == "offensiveBonus" + t + "Chance" ||
                    field == "offensiveBase" + t + "Chance")
                    return "damage: chance on flat (expected value)";
                if (field == "offensiveSlow" + t + "Min" || field == "offensiveSlow" + t + "Max")
                    return "damage: dot";
                if (field == "offensiveSlow" + t + "DurationMin" || field == "offensiveSlow" + t + "DurationMax")
                    return "damage: dot duration";
                // "+% to the duration of X damage" — 1302 records, and it is DELIBERATELY not
                // in our calculation. Checked record against game: one skill has
                // `offensiveSlowFireDurationMin 2`, and `_Iman`'s tooltip reads "4708 burn
                // over 2 seconds" although he carries +80% burn duration. So neither the panel
                // nor the tooltip shows this bonus; it lengthens the dot in combat and raises
                // the damage over the whole dot, but it touches none of the numbers we check.
                if (field == "offensiveSlow" + t + "DurationModifier")
                    return "in combat but not in the panel: dot duration";
                if (field == "offensiveSlow" + t + "Modifier") return "damage: dot percent";
                if (field == "offensiveSlow" + t + "Chance") return "damage: dot chance (expected value)";
                if (field == "offensiveSlow" + t + "ModifierChance") return "damage: dot percent chance";
                if (field == "offensive" + t + "ModifierChance") return "damage: percent chance";
                // `offensive{T}Global 1` marks "this field is rolled by the record's SHARED
                // die" rather than its own. That does not change the expected value (a mean
                // does not care whether the dice were thrown together or apart), only the
                // spread. We read it as a marker and do not take it into the calculation.
                if (field == "offensive" + t + "Global" || field == "offensiveSlow" + t + "Global")
                    return "marker: the record's shared chance roll";
            }
            // The shared chance over a record's whole damage block. Read since 9 August 2026:
            // one relic (`d011_relic`) has "1 – 36 chaos damage" and `offensiveGlobalChance 5`
            // with no `offensiveChaosChance` of its own at all — and such damage was counted in
            // full instead of 5%.
            if (field == "offensiveGlobalChance") return "damage: the record's shared chance (expected value)";
            if (field == "offensiveTotalDamageModifier") return "damage: overall percent";
            if (field == "offensivePierceRatioMin") return "damage: armour pierce";
            if (field.StartsWith("conversion", StringComparison.Ordinal)) return "damage: conversion";

            // ---- the attributes that enter the damage multiplier ----
            if (field == "characterDexterity" || field == "characterIntelligence" ||
                field == "characterStrength" ||
                field == "characterDexterityModifier" || field == "characterIntelligenceModifier" ||
                field == "characterStrengthModifier")
                return "damage: multiplier from an attribute";

            // ---- speed ----
            if (field == "characterAttackSpeedModifier" || field == "characterTotalSpeedModifier" ||
                field == "characterAttackSpeedMaxModifier" || field == "characterBaseAttackSpeed")
                return "attack speed";
            if (field == "characterSpellCastSpeedModifier" || field == "characterSpellCastSpeedMaxModifier")
                return "cast speed";
            if (field == "characterRunSpeedModifier" || field == "characterRunSpeedMaxModifier")
                return "run speed";

            // ---- the skills an item grants or modifies ----
            if (field.StartsWith("augmentSkillLevel", StringComparison.Ordinal) ||
                field.StartsWith("augmentMasteryLevel", StringComparison.Ordinal) ||
                field == "augmentAllLevel" || field == "itemSkillLevel" || field == "itemSkillLevelEq")
                return "skills: +level";
            if (field == "skillCooldownReduction" || field == "skillManaCostReduction" ||
                field == "skillCooldownTime" || field == "skillManaCost")
                return "skills: cooldown and cost";
            // The last two item fields that DO have a caption from the game and had no marking
            // (the census, 11 August 2026). Both are about the shape of a strike and its cost,
            // not about the amount of damage:
            //   skillProjectileSpeedModifier — "#% projectile speed"
            //   skillComboChargeSpendReduction — a discount on spending combo charges
            if (field == "skillProjectileSpeedModifier")
                return "skills: shown in the skill's tooltip";
            if (field == "skillComboChargeSpendReduction")
                return "skills: cooldown and cost";

            // ---- character panel: counted and shown, does not enter damage per hit ----
            if (field == "characterLife" || field == "characterMana" ||
                field == "characterLifeModifier" || field == "characterManaModifier" ||
                field == "characterLifeRegen" || field == "characterManaRegen" ||
                field == "characterLifeRegenModifier" || field == "characterManaRegenModifier" ||
                field == "characterOffensiveAbility" || field == "characterDefensiveAbility" ||
                field == "characterOffensiveAbilityModifier" || field == "characterDefensiveAbilityModifier" ||
                field == "characterDodgePercent" || field == "characterDeflectProjectile" ||
                field == "characterTotalSpeedResistance" || field == "characterConstitutionModifier" ||
                field == "characterIncreasedExperience" || field == "characterHuntingDexterityReqReduction" ||
                field == "characterArmorStrengthReqReduction" || field == "characterSpellCastSpeed" ||
                field == "characterAttackSpeed" || field == "characterRunSpeed" ||
                field == "characterManaLimitReserve" || field == "characterManaLimitReserveReduction" ||
                field == "characterLifeLimitReserve" || field == "characterLifeLimitReserveReduction" ||
                field == "characterWeaponDexterityReqReduction" || field == "characterWeaponStrengthReqReduction" ||
                field == "characterJewelryDexterityReqReduction" || field == "characterJewelryStrengthReqReduction" ||
                field == "characterJewelrySpiritReqReduction" || field == "characterWeaponSpiritReqReduction" ||
                field == "characterArmorDexterityReqReduction" || field == "characterArmorSpiritReqReduction" ||
                field == "characterGlobalReqReduction" || field == "characterDefensiveBlockModifier" ||
                field == "characterHealIncreasePercent" || field == "characterEnergyAbsorptionPercent" ||
                field == "characterLightRadius" || field == "characterDefensiveBlockRecoveryReduction" ||
                field.EndsWith("ReqReduction", StringComparison.Ordinal))
                return "character panel";
            if (field == "defensiveProtection" || field == "defensiveProtectionModifier" ||
                field == "defensiveAbsorptionModifier" || field == "defensiveBlock" ||
                field == "defensiveBlockAmountModifier" || field == "defensiveBlockChance" ||
                field == "defensiveBlockModifier" || field == "defensiveBlockRecoveryReduction" ||
                field == "blockAbsorption" || field == "blockRecoveryTime")
                return "panel: armour and block";
            if (field.StartsWith("defensive", StringComparison.Ordinal)) return "panel: defence";

            // ---- shown in the tooltip, takes no part in the calculation ----
            if (field.StartsWith("retaliation", StringComparison.Ordinal)) return "display only: retaliation";
            if (field.StartsWith("petBonus", StringComparison.Ordinal) ||
                field.StartsWith("pet", StringComparison.Ordinal)) return "display only: pets";
            // The bonus against a race. Since 10 September the PERCENT damage one goes into the
            // queue as a multiplier by the target's race (MECHANICS 72); the flat one is
            // collected but not applied; the defensive ones are display only (we do not compute
            // the enemy's damage to us). The "display only" label stood here even after that —
            // on ageing labels see MECHANICS 71b.
            if (field == "racialBonusPercentDamage") return "timeline: multiplier by the target's race";
            if (field == "racialBonusAbsoluteDamage") return "display only: flat against a race (not measured)";
            if (field.StartsWith("racialBonus", StringComparison.Ordinal)) return "display only: against a race";
            // The queue has counted the cut to the target's DEFENSIVE ABILITY since
            // 9 September, from any source (MECHANICS 62): it moves both the chance to hit and
            // the chance to crit. The target's ATTACK ability we do not need: the enemy's damage
            // to us is not modelled.
            if (field.StartsWith("offensiveSlowDefensiveAbility", StringComparison.Ordinal))
                return "timeline: cut to the target's defensive ability";
            // The cut to the target's ARMOUR has been counted since 15 September
            // (MECHANICS 89e): as a number, with its own chance and duration, strongest wins.
            // Before that it said "display only" here.
            if (field.StartsWith("offensiveSlowDefensiveReduction", StringComparison.Ordinal))
                return "timeline: cut to the target's armour";
            if (field.StartsWith("offensiveSlowOffensiveAbility", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowRunSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowAttackSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowSpellCastSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowTotalSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowOffensiveReduction", StringComparison.Ordinal))
                return "display only: debuff on the target";
            // The queue has counted the reduction of ALL resistances from items since
            // 9 September, all three kinds (`DebuffBook.PutGear`, MECHANICS 71). The families
            // below hardly occur on items: of them the whole database holds one record, a quest
            // carving with physical (checked against `item-stats.txt` on 10 September).
            if (field.StartsWith("offensiveTotalResistanceReduction", StringComparison.Ordinal))
                return "timeline: reduction of ALL the target's resistances";
            if (field.StartsWith("offensiveElementalResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePhysicalResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePoisonResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveBleedingResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePierceResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveFireResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveColdResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveLightningResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveAetherResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveChaosResistanceReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveLifeResistanceReduction", StringComparison.Ordinal))
                return "display only: reduction of the target's resistances";
            if (field.StartsWith("offensiveLifeLeech", StringComparison.Ordinal) ||
                field.StartsWith("offensiveManaLeech", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowLifeLeach", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowManaLeach", StringComparison.Ordinal) ||
                field.StartsWith("offensiveLifeMana", StringComparison.Ordinal) ||
                field.StartsWith("offensivePercentCurrentLife", StringComparison.Ordinal))
                return "display only: leech and a share of life";
            if (field.StartsWith("offensiveStun", StringComparison.Ordinal) ||
                // Knockdown is the same trap as stun: `…Min` here is not damage but a DURATION
                // in seconds. The game's own caption says so ("Knockdown target"), and one
                // skill's tooltip reads "31% chance of knockdown for 1 second" with
                // `offensiveKnockdownMin 1`.
                field.StartsWith("offensiveKnockdown", StringComparison.Ordinal) ||
                field.StartsWith("offensiveFreeze", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSleep", StringComparison.Ordinal) ||
                field.StartsWith("offensiveTrap", StringComparison.Ordinal) ||
                field.StartsWith("offensiveFumble", StringComparison.Ordinal) ||
                field.StartsWith("offensiveProjectileFumble", StringComparison.Ordinal) ||
                field.StartsWith("offensiveConfusion", StringComparison.Ordinal) ||
                field.StartsWith("offensiveConvert", StringComparison.Ordinal) ||
                field.StartsWith("offensiveDisruption", StringComparison.Ordinal) ||
                field.StartsWith("offensivePetrify", StringComparison.Ordinal) ||
                field.StartsWith("offensiveTotalDamageReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePhysicalReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveElementalReduction", StringComparison.Ordinal) ||
                // Energy burn: `…DrainMin` burns the target's energy, and
                // `DamageManaBurnRatio` ("#% of burnt energy deals damage") turns what was
                // burnt into damage. The damage is real, but it depends on the TARGET's ENERGY
                // rather than on our weapon, so it cannot enter "damage per hit" — there is no
                // target there yet. This can only be checked with an item carrying such an
                // affix and a shot of the panel.
                field.StartsWith("offensiveManaBurn", StringComparison.Ordinal))
                return "display only: impairments on the target";
            if (field == "offensiveCritDamageModifier")
                return "display only: crits (they do not enter a strike)";
            // "TOTAL DAMAGE MODIFIED BY N%" is a separate multiplier on top of everything, and
            // it does enter a strike. Measured 23 August on a skill of one of `_Iman`'s test
            // saves before and after a devotion star: exactly ×1.11 on each of the three lines
            // (MECHANICS 19k). Before the measurement the field sat here with the crits and
            // gave nothing.
            if (field == "offensiveDamageMultModifier")
                return "damage: multiplier on top of everything (on a skill)";

            // ---- housekeeping: no bonus at all ----
            if (field == "itemLevel" || field == "levelRequirement" || field == "attributeScalePercent" ||
                field == "itemCost" || field == "itemBaseCost" || field == "itemPrefixCost" ||
                field == "itemSuffixCost" || field == "itemSkillAutoController" ||
                field == "completedRelicLevel" || field == "craftingMaterial" || field == "soulbound" ||
                field == "untradeable" || field == "dlcRequirement" || field == "itemClassification" ||
                field == "itemSetName" || field == "itemSkillName" || field == "bonusTableName")
                return "housekeeping";

            return null;
        }
    }
}
