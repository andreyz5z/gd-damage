using System;

namespace Gdc.Gui
{
    // Single-target coverage for one volley. The damage model still describes one impact.
    internal readonly struct ProjectileVolley
    {
        public readonly int Count;
        public readonly double Coverage;
        public readonly double Interval, Duration;

        ProjectileVolley(int count, double coverage, double interval = 0, double duration = 0)
        {
            Count = Math.Max(1, count);
            Coverage = coverage;
            Interval = interval;
            Duration = duration;
        }

        public static ProjectileVolley Of(AttackSkill skill)
        {
            if (skill == null || skill.Parts.Count == 0) return new ProjectileVolley(1, 1);
            SkillPart root = skill.Parts[0];
            string kind = root.Rec.Class;
            bool rain = kind == "Skill_BuffAttackRadiusDrop";
            bool drop = rain || kind == "Skill_AttackProjectileDrop";
            bool burst = kind == "Skill_AttackProjectileBurst" || kind == "Skill_AttackProjectileFan" ||
                         kind == "Skill_WPAttack_ProjectileBurst";
            if (!drop && !burst && kind != "Skill_AttackWeapon") return new ProjectileVolley(1, 1);

            int count = 0;
            double angle = 0;
            double area = AttackSkill.Value(root.Rec, "skillTargetRadius", root.Slot);
            if (area <= 0) area = AttackSkill.Value(root.Rec, "dropRadius", root.Slot);
            double radius = AttackSkill.Value(root.Rec, "projectileExplosionRadius", root.Slot);
            foreach (SkillPart part in skill.Parts)
            {
                // A secondary attack can fire its own projectiles; those are not extra shots
                // of the initiating skill. Tree and equipment modifiers stay in this volley.
                if (part != root && part.Rec.Class.StartsWith("SkillSecondary", StringComparison.Ordinal)) continue;
                double shots = drop && part == root ? AttackSkill.Value(part.Rec, "numProjectiles", part.Slot) : 0;
                if (shots == 0) shots = AttackSkill.Value(part.Rec, "projectileLaunchNumber", part.Slot);
                if (shots == 0) shots = AttackSkill.Value(part.Rec, "skillProjectileNumber", part.Slot);
                if (shots == 0 && part == root) shots = 1;
                count += (int)Math.Round(shots);
                angle += AttackSkill.Value(part.Rec, "projectileLaunchRotation", part.Slot) +
                         AttackSkill.Value(part.Rec, "skillProjectileAngle", part.Slot);

                if (!drop || part == root) continue;
                double overrideArea = AttackSkill.Value(part.Rec, "skillTargetRadius", part.Slot);
                if (overrideArea > 0) area = overrideArea;
                radius += AttackSkill.Value(part.Rec, "explosionRadius", part.Slot);
            }
            if (Math.Abs(angle) >= 360) return new ProjectileVolley(1, 1);

            // Aimed bursts use point-blank overlap: the timeline has no distance or hitbox.
            // Ground drops sample a uniform disc centered on a point target. A centered
            // target is in every blast if the explosion radius covers the entire drop area.
            double coverage = drop && area > 0 && radius > 0 && radius < area
                            ? (radius / area) * (radius / area) : 1;
            if (drop && area > 0 && radius <= 0) return new ProjectileVolley(1, 1);
            return new ProjectileVolley(count, coverage,
                rain ? AttackSkill.Value(root.Rec, "skillTargetInterval", root.Slot) : 0,
                rain ? skill.Sum("skillActiveDuration") : 0);
        }
    }
}
