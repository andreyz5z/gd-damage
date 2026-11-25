// Maps each skill field to its use in the calculation, analogous to ItemStatUse.
//
// Skill fields have three distinct roles and must not be treated as item bonuses:
//
//   1. character bonuses;
//   2. strike properties such as `weaponDamagePct`, target count, angle, and projectiles;
//   3. effects applied to the target, which are displayed but excluded from character totals.
//
// "nowhere" is an audit category: `--skillstats` reports fields that remain unmapped.

using System;

namespace Gdc.Gui
{
    public static class SkillStatUse
    {
        static readonly string[] Types = {
            "Physical", "Pierce", "Fire", "Cold", "Lightning",
            "Poison", "Life", "Aether", "Chaos", "Bleeding", "Elemental",
        };

        // Not a bonus: visual, tree, targeting, and projectile configuration.
        static readonly string[] NotABonus = {
            "bitmap", "Bitmap", "texture", "Texture", "mesh", "Mesh", "sound", "Sound",
            "fx", "Fx", "FX", "anim", "Anim", "particle", "Particle", "camera", "Camera",
            "skillConnection", "skillDependancy", "skillTemplates", "skillBlackList",
            "skillDisplayName", "skillBaseDescription", "skillTier", "skillMaxLevel",
            "skillUltimateLevel", "skillExperienceLevels", "skillMasteryLevelRequired",
            "templateName", "Class", "FileDescription", "description", "itemText",
            "hideFromUI", "isCircular", "ragDoll", "spawnObjects", "spawnLimit",
            "petLimit", "petBurstSpawn", "waveDistance", "wave", "expansionTime",
            "drop", "Drop", "debufSkill", "debuffSkillName", "buffSkillName",
            "petSkillName", "petBonusName", "modifierSkill", "modifiedSkill",
            "skillProjectileName", "skillSpecialAnimation", "specialAttack",
            "targetFxFirstOnly", "skillProjectileTargetGroundOnly", "skillUp", "skillDown",
            "skillActivated", "skillWindup", "skillRelease", "skillStoredCharges",
            "skillChargeLevel", "triggerType", "triggerParam", "targetType", "chanceToRun",
            "refreshCooldown", "refreshDuration", "cooldownCharges", "skillAllowsWarmUp",
            "distanceProfile", "instantCast", "targetOffset", "autoTargetRadius",
            "projectileLaunch", "projectileFragment", "projectileExplosion", "projectileUse",
            "projectileSpeed", "projectilePierce", "skillLevel", "skillName", "soundName",
            // Engine behavior; not a character or target bonus.
            "useTargetDir", "lifeMonitorPercent", "spark", "contagion", "maxMoveRatio",
            "numProjectiles", "projectilePiercing", "launchAboveTarget", "explosionRadius",
            "skillLocked", "skillComboCharge", "potionModifier", "ignoreDisruption",
            "notDispelable", "overwriteBaseSkill", "skillProjectileMaximumNumber",
            "forceMovement", "skillTargetInterval", "timeBetweenAttacks", "projectilePeriod",
            "exclusiveSkill", "skillWeaponTypeRequirement", "chargeLevels", "chargeDuration",
            "skillRedirect", "skillMasteryLevel", "petObjects", "summonLimit", "lifeMonitor",
        };

        public static string Of(string field)
        {
            if (string.IsNullOrEmpty(field)) return null;
            foreach (string s in NotABonus)
                if (field.IndexOf(s, StringComparison.Ordinal) >= 0) return "not a bonus: looks, tree, projectile";

            // ---- 2. strike properties, read by AttackSkills/DamageModel ----
            if (field == "weaponDamagePct") return "strike: weapon damage share";
            if (field == "skillTargetNumber" || field == "skillTargetAngle" ||
                field == "skillTargetRadius" || field == "skillProjectileNumber" ||
                field == "projectileLaunchNumber" || field == "projectilePiercingChance" ||
                field == "skillChanceWeight")
                return "strike: shown in the skill's tooltip";
            if (field == "skillActiveDuration" || field == "skillCooldownTime" ||
                field == "skillManaCost" || field == "skillActiveManaCost" ||
                field == "skillActiveLifeCost" || field == "skillLifeCost" ||
                field == "characterManaLimitReserve" || field == "characterLifeLimitReserve")
                return "strike: cost and cooldown";
            if (field == "onHitActivationChance" || field == "itemSkillAutoController")
                return "marker: when the skill works";
            if (field.EndsWith("Only", StringComparison.Ordinal) ||
                field == "Sword" || field == "Axe" || field == "Mace" || field == "Dagger" ||
                field == "Scepter" || field == "Sword2h" || field == "Axe2h" || field == "Mace2h" ||
                field == "Spear2h" || field == "Ranged1h" || field == "Ranged2h" ||
                field == "Shield" || field == "Offhand")
                return "marker: weapon restriction";

            // ---- 1. character bonuses, using the same fields as items ----
            foreach (string t in Types)
            {
                if (field == "offensive" + t + "Min" || field == "offensive" + t + "Max" ||
                    field == "offensiveBonus" + t + "Min" || field == "offensiveBonus" + t + "Max" ||
                    field == "offensiveBase" + t + "Min" || field == "offensiveBase" + t + "Max")
                    return "damage: flat";
                if (field == "offensive" + t + "Modifier") return "damage: type percent";
                if (field == "offensive" + t + "Chance") return "damage: chance (expected value)";
                if (field == "offensiveSlow" + t + "Min" || field == "offensiveSlow" + t + "Max")
                    return "damage: dot";
                if (field == "offensiveSlow" + t + "Chance") return "damage: dot chance";
                if (field == "offensiveSlow" + t + "DurationMin" || field == "offensiveSlow" + t + "DurationMax")
                    return "damage: dot duration";
                if (field == "offensiveSlow" + t + "Modifier") return "damage: dot percent";
                if (field == "offensiveSlow" + t + "ModifierChance" || field == "offensive" + t + "ModifierChance")
                    return "damage: percent chance";
                if (field == "offensiveSlow" + t + "DurationModifier")
                    return "in combat but not in the panel: dot duration";
                // A shared record roll marker; it does not change the expected value.
                if (field == "offensive" + t + "Global" || field == "offensiveSlow" + t + "Global")
                    return "marker: the record's shared chance roll";
                // XOR means exactly one marked effect is selected by the shared roll.
                // Keep the marker for timeline simulation; it is not a separate bonus.
                if (field == "offensive" + t + "XOR" || field == "offensiveSlow" + t + "XOR")
                    return "marker: exactly one of the marked ones lands — AFFECTS THE EXPECTED VALUE, not worked out";
            }
            // Shared chance for the record's damage block; a local `...Chance` may be absent.
            if (field == "offensiveGlobalChance") return "damage: the record's shared chance (expected value)";
            if (field == "offensiveTotalDamageModifier") return "damage: overall percent";
            if (field == "offensiveTotalDamageModifierChance") return "damage: percent chance";

            // ---- Charges. Two fields affect the strike; the others are timeline data. ----
            if (field == "skillChargeMultipliers" || field == "skillChargeLevel")
                return "strike: charge multiplier";
            if (field == "skillChargeTime" || field == "skillChargeDuration" ||
                field == "skillChargeTimeRanged")
                return "timeline: gaining and spending charges — NOT COUNTED";

            // ---- Target effects: displayed to the player, excluded from our damage. ----
            if (field.StartsWith("offensiveFear", StringComparison.Ordinal))
                return "shown: an impairment on the target (fear)";

            // ---- Shield absorption qualifiers; named here but not calculated. ----
            if (field.EndsWith("DamageQualifier", StringComparison.Ordinal))
                return "shield: which damage type is absorbed — NOT COUNTED (task 2.3)";

            // ---- Projectile damage falloff: three distance ranges and shares. ----
            // The panel shows point-blank damage; these fields affect target damage only.
            if (field.StartsWith("projectileDamageRange", StringComparison.Ordinal))
                // The label starts with the damage family on purpose: `SkillAudit.Combat`
                // decides by that prefix whether the field is a combat one at all.
                return "damage: a projectile's damage falls off with distance — NOT COUNTED";
            if (field == "pointBlank")
                return "marker: the projectile hits point-blank — NOT COUNTED";

            // ---- Damage absorption, applied when converting panel values to target damage. ----
            if (field == "absorbShieldPercentHealth" || field == "damageAbsorptionReflectPercent")
                return "display only: damage absorption";

            // ---- Potions and potion modifiers use a separate mechanic. ----
            if (field.StartsWith("potion", StringComparison.Ordinal) ||
                field == "exclusivePotionSkill" || field == "modifierPotency" ||
                field == "purity" || field == "filterCaster")
                return "display only: potions and their boosts";

            // ---- Monster-race bonus exclusion flag. ----
            if (field == "excludeRacialDamage")
                return "marker: the bonus against a race does not apply to this damage";

            // ---- Five fields with game labels, describing strike shape rather than power. ----
            if (field == "beamNumber" || field == "beamAngle" || field == "targetRadius" ||
                field == "skillProjectileSpeedModifier")
                return "strike: shown in the skill's tooltip";
            // Alternate cooldown field used by summon skill modifiers.
            if (field == "cooldownTime") return "strike: cost and cooldown";

            // ---- Pet-related fields. ----
            if (field == "isPetBonusScaling" || field == "isPetDisplayable" ||
                field == "trackSpawns" || field == "modSpawnObjectsTimeToLive" ||
                field.StartsWith("spawnObjectWeights", StringComparison.Ordinal))
                return "display only: pets (task 2.2)";
            if (field == "offensivePierceRatioMin") return "damage: armour pierce";
            if (field.StartsWith("conversion", StringComparison.Ordinal)) return "damage: conversion";
            if (field == "characterAttackSpeedModifier" || field == "characterTotalSpeedModifier" ||
                field == "characterAttackSpeedMaxModifier")
                return "attack speed";
            if (field == "characterSpellCastSpeedModifier" || field == "characterSpellCastSpeedMaxModifier")
                return "cast speed";
            if (field == "characterRunSpeedModifier" || field == "characterRunSpeedMaxModifier")
                return "run speed";
            if (field.StartsWith("character", StringComparison.Ordinal)) return "character panel";
            if (field.StartsWith("defensive", StringComparison.Ordinal)) return "panel: defence";
            if (field.StartsWith("skillCooldownReduction", StringComparison.Ordinal) ||
                field.StartsWith("skillManaCostReduction", StringComparison.Ordinal) ||
                field.StartsWith("skillLife", StringComparison.Ordinal) ||
                field.StartsWith("skillMana", StringComparison.Ordinal) ||
                field.StartsWith("skillPoints", StringComparison.Ordinal) ||
                field.StartsWith("augment", StringComparison.Ordinal))
                return "skills: levels, cost, cooldown";

            // ---- 3. effects applied to the target ----
            // Includes target armor reduction.
            if (field.StartsWith("offensiveSlowDefensiveReduction", StringComparison.Ordinal))
                return "target: armour cut (timeline)";
            if (field.StartsWith("offensiveSlowOffensiveAbility", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowDefensiveAbility", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowRunSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowAttackSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowSpellCastSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowTotalSpeed", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowOffensiveReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowLifeLeach", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSlowManaLeach", StringComparison.Ordinal))
                return "target: debuff";
            if (field.IndexOf("ResistanceReduction", StringComparison.Ordinal) >= 0)
                return "target: resistance reduction";
            if (field.StartsWith("offensiveStun", StringComparison.Ordinal) ||
                field.StartsWith("offensiveFreeze", StringComparison.Ordinal) ||
                field.StartsWith("offensiveSleep", StringComparison.Ordinal) ||
                field.StartsWith("offensiveTrap", StringComparison.Ordinal) ||
                field.StartsWith("offensiveFumble", StringComparison.Ordinal) ||
                field.StartsWith("offensiveProjectileFumble", StringComparison.Ordinal) ||
                field.StartsWith("offensiveConfusion", StringComparison.Ordinal) ||
                field.StartsWith("offensiveConvert", StringComparison.Ordinal) ||
                field.StartsWith("offensiveDisruption", StringComparison.Ordinal) ||
                field.StartsWith("offensivePetrify", StringComparison.Ordinal) ||
                field.StartsWith("offensiveKnockdown", StringComparison.Ordinal) ||
                field.StartsWith("offensiveTotalDamageReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePhysicalReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensiveElementalReduction", StringComparison.Ordinal) ||
                field.StartsWith("offensivePercentCurrentLife", StringComparison.Ordinal) ||
                field.StartsWith("offensiveManaBurn", StringComparison.Ordinal))
                return "target: impairments";
            if (field.StartsWith("offensiveLifeLeech", StringComparison.Ordinal) ||
                field.StartsWith("offensiveManaLeech", StringComparison.Ordinal))
                return "display only: leech";
            // `retaliationDamagePct` contributes a share of retaliation damage to the strike.
            // The exact damage-type and ordering rules are not modeled here yet.
            if (field.Equals("retaliationDamagePct", StringComparison.Ordinal))
            // The contribution is kept per damage type, but the final value is not yet
            // included because the remaining ordering rules are unresolved.
                return "enters a strike: a share of retaliation is added to the attack — NOT COUNTED";
            if (field.StartsWith("retaliation", StringComparison.Ordinal)) return "display only: retaliation";
            if (field.StartsWith("pet", StringComparison.Ordinal)) return "display only: pets";
            if (field.StartsWith("racialBonus", StringComparison.Ordinal)) return "display only: against a race";
            if (field == "offensiveCritDamageModifier" || field == "offensiveDamageMultModifier")
                return "display only: crits (they do not enter a strike)";
            if (field == "damageAbsorption" || field == "damageAbsorptionPercent")
                return "display only: damage absorption";
            if (field.StartsWith("offensiveTaunt", StringComparison.Ordinal)) return "target: impairments";

            return null;
        }
    }
}
