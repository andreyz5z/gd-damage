// Character attribute points and the derived save values.
//
// WHAT THE SAVE ACTUALLY HOLDS. `Physique`, `Cunning` and `Spirit` are the base plus
// what the player put in — gear is NOT in them. `Health` and `Energy` beside them are
// not the numbers the character sheet shows either: they are the base plus WHAT THE
// ATTRIBUTES GAVE, and nothing else.
//
// EVERY NUMBER BELOW COMES OUT OF THE GAME'S OWN RECORDS, none is written here:
//
//     records/creatures/pc/malepc01.dbr     characterStrength/Dexterity/Intelligence  50
//                                           characterLife, characterMana             250
//     records/creatures/pc/playerlevels.dbr strengthIncrement and related fields        8
//                                           lifeIncrement                             20
//                                           lifeIncrementDexterity                     8
//                                           lifeIncrementIntelligence                 12
//                                           manaIncrement                             16
//
// One attribute point adds 8 attribute units and grants 20 health from physique,
// 8 health from cunning, 12 health from spirit, or 16 energy from spirit.
//
// The formulas match the save values exactly:
//
//     attributes 426/218/298  →  250 + 47×20 + 21×8 + 31×12 = 1730
//                             →  250 + 31×16              = 746
//
// `--attrtest` validates these values across all saves.
//
// This does not calculate the in-game panel totals, which also include items, skills,
// and percentage bonuses.

using System;

namespace Gdc.Gui
{
    public static class PlayerAttributes
    {
        public enum Which { Physique, Cunning, Spirit }

        // `--attrbreak` treats one saved point as one attribute unit for diagnostics.
        public static bool BreakStep;

        // Base attribute value with no invested points.
        public static double Start(GameData game, Which what)
        {
            switch (what)
            {
                case Which.Cunning: return Combat.PcAttribute(game, "characterDexterity");
                case Which.Spirit: return Combat.PcAttribute(game, "characterIntelligence");
                default: return Combat.PcAttribute(game, "characterStrength");
            }
        }

        // Attribute units granted by one point.
        public static double Step(GameData game, Which what)
        {
            if (BreakStep) return 1;
            switch (what)
            {
                case Which.Cunning: return Combat.PlayerLevels(game, "dexterityIncrement", 8);
                case Which.Spirit: return Combat.PlayerLevels(game, "intelligenceIncrement", 8);
                default: return Combat.PlayerLevels(game, "strengthIncrement", 8);
            }
        }

        // Invested points. A fractional result indicates an invalid save value.
        public static int Invested(GameData game, Which what, double value)
        {
            double step = Step(game, what);
            if (step <= 0) return 0;
            double points = (value - Start(game, what)) / step;
            return (int)Math.Round(points);
        }

        // Whether the division was exact and the value is valid.
        public static bool Whole(GameData game, Which what, double value)
        {
            double step = Step(game, what);
            if (step <= 0) return false;
            double back = Start(game, what) + Invested(game, what, value) * step;
            return Math.Abs(back - value) < 0.01 && value >= Start(game, what) - 0.01;
        }

        // Attribute value for the given invested-point count.
        public static double ValueOf(GameData game, Which what, int points)
        {
            return Start(game, what) + points * Step(game, what);
        }

        // Health and energy as stored in the save: base value plus attribute-point gains.
        // Items, skills, and percentage bonuses are applied only when drawing the panel.
        public static double Health(GameData game, double physique, double cunning, double spirit)
        {
            return Combat.PcAttribute(game, "characterLife")
                 + Invested(game, Which.Physique, physique) * Combat.PlayerLevels(game, "lifeIncrement", 20)
                 + Invested(game, Which.Cunning, cunning) * Combat.PlayerLevels(game, "lifeIncrementDexterity", 8)
                 + Invested(game, Which.Spirit, spirit) * Combat.PlayerLevels(game, "lifeIncrementIntelligence", 12);
        }

        public static double Energy(GameData game, double spirit)
        {
            return Combat.PcAttribute(game, "characterMana")
                 + Invested(game, Which.Spirit, spirit) * Combat.PlayerLevels(game, "manaIncrement", 16);
        }

        // Health and energy must be updated after attributes change, otherwise the save
        // retains values derived from the previous points.
        public static void Follow(GameData game, Character c)
        {
            if (game == null || c == null) return;
            c.Health = (float)Health(game, c.Physique, c.Cunning, c.Spirit);
            c.Energy = (float)Energy(game, c.Spirit);
        }

        // Reset invested points to the base value and return the points to the pool.
        public static int ResetInvested(GameData game, Character c)
        {
            if (game == null || c == null) return 0;
            int back = Invested(game, Which.Physique, c.Physique)
                     + Invested(game, Which.Cunning, c.Cunning)
                     + Invested(game, Which.Spirit, c.Spirit);
            if (back <= 0) return 0;
            c.Physique = (float)Start(game, Which.Physique);
            c.Cunning = (float)Start(game, Which.Cunning);
            c.Spirit = (float)Start(game, Which.Spirit);
            c.AttributePoints += back;
            Follow(game, c);
            return back;
        }
    }
}
