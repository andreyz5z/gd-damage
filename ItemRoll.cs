// How item values are rolled from their seed.
//
// The model uses one rolled field per controlled test item; see `--seedtest`.
//
//     generator: x = 16807 · x mod (2³¹ − 1), using the Schrage method
//     seed:      x = seed, then discard one roll
//     roll:      j = low + (x mod (high − low + 1))
//     bounds:    low = ceil(record × 0.8), high = floor(record × 1.2)
//     damage:    result = j × (100 + attributeScalePercent) / 100, truncated
//
// The generator uses the signed modulus expected by the game, so equivalent seeds
// produce the same first value.
//
// Additional rules besides the base roll:
// Field order is defined by [ItemOrder.cs](ItemOrder.cs). Pair maxima roll the range
// width, conversions use a separate stream rule, and base weapon damage is not rolled.

using System;

namespace Gdc.Gui
{
    public static class ItemRoll
    {
        const long M = 2147483647;   // 2³¹ − 1

        // One Schrage generator step without 32-bit overflow.
        public static long Next(long prev)
        {
            int hi = (int)(prev / 127773);
            int lo = (int)(prev - 127773L * hi);
            int t = 16807 * lo - 2836 * hi;
            return t >= 0 ? t : (long)t + M;
        }

        // Item roll stream. The unsigned seed is stored as-is; the first roll is
        // discarded as part of seeding.
        public static long Seed(uint seed) { return Next(seed); }

        // Bounds of the raw roll. The tooltip shows clamped bounds; the raw roll is
        // clamped only after scaling by the item.
        public static bool Bounds(string field, double value, int scale, out long lo, out long hi)
        {
            double dlo, dhi;
            lo = hi = (long)value;
            if (!ItemViewFactory.RollBounds(field, value, 0, ItemViewFactory.RollJitter, false,
                                            out dlo, out dhi)) return false;
            lo = (long)dlo; hi = (long)dhi;
            return true;
        }

        // Clamp the raw roll before applying the item multiplier; values do not fall below one.
        static long Clamp(double value, long j)
        {
            if (value >= 1 && j < 1) return 1;
            if (value <= -1 && j > -1) return -1;
            return j;
        }

        // The upper end of a min/max pair is rolled as a range width:
        //
        //     d = record(max) − record(min)
        //     j = low + (x mod (high − low + 1)), with ±20% bounds around d
        //     max = rolled min + j; the item multiplier is not applied to j
        //
        // `attributeScalePercent` is already included in the rolled minimum and is
        // not applied to the width.
        //
        // This matches the independent pair checks used by the seed audit.
        //
        // When min equals max, no width is rolled, but the stream position is still consumed.
        public static long PairMax(double recMin, double recMax, long rolledMin, long word)
        {
            long lo, hi;
            if (!PairBounds(recMin, recMax, out lo, out hi)) return rolledMin;
            return rolledMin + lo + word % (hi - lo + 1);
        }

        // Pair-width display bounds use the same ±20% rule and minimum distance of one.
        public static bool PairBounds(double recMin, double recMax, out long lo, out long hi)
        {
            lo = hi = 0;
            double d = recMax - recMin;
            if (d <= 0) return false;
            // Pair width uses the jitter of the item part: 20% for the base or
            // `lootRandomizerJitter` for an affix.
            double s = ItemViewFactory.RollJitter / 100.0;
            lo = (long)Math.Min(Math.Ceiling(d * (1 - s)), d - 1);
            hi = (long)Math.Max(Math.Floor(d * (1 + s)), d + 1);
            if (lo < 0) lo = 0;
            return true;
        }

        // Whether the rule can produce the requested upper end for a lower end.
        public static bool PairCanGive(double recMin, double recMax, long rolledMin, long rolledMax)
        {
            long lo, hi;
            if (!PairBounds(recMin, recMax, out lo, out hi)) return rolledMax == rolledMin;
            long d = rolledMax - rolledMin;
            return d >= lo && d <= hi;
        }

        // ==================== CONVERSION: SEPARATE RULES ====================
        //
        // Conversion uses its own stream position and bounds. Its position depends
        // on the number of preceding item fields.
        //
        //   POSITION  after `defensive*` and before `skill*`, as defined by `ItemOrder`;
        //   DISPLAY   `v ± round(v × jitter / 100)`;
        //   ROLL      raw bounds remain fractional, the final result is rounded, and
        //             the stream value is normalized by division rather than remainder:
        //
        //               d      = v × jitter / 100
        //               result = round((v − d) + x × 2d / (2³¹ − 1))
        //
        // Ordinary fields use a remainder; conversion uses the normalized fraction.
        //
        // Keep fractional bounds before rounding: pre-rounding them changes the result
        // for small jitter values.
        //
        // The rule is covered by seed, field-order, and exact-value audits.
        public static bool IsConversion(string field)
        {
            return field != null && field.StartsWith("conversionPercentage", StringComparison.Ordinal);
        }

        // Conversion is capped at 100 after the roll; the display bounds are not
        // narrowed before rolling.
        const double ConvCap = 100;

        // Displayed conversion bounds use rounded endpoints and a 100 cap.
        public static bool ConvBounds(double value, int jitter, out double lo, out double hi)
        {
            double d = Math.Round(Math.Abs(value) * jitter / 100.0, MidpointRounding.AwayFromZero);
            lo = value - d; hi = Math.Min(value + d, ConvCap);
            return true;
        }

        // Roll conversion from one generator value. Raw bounds remain fractional;
        // apply the cap to the final result.
        public static long ConvValue(double value, int jitter, long word)
        {
            return (long)Math.Min(Math.Round(ConvExact(value, jitter, word), MidpointRounding.AwayFromZero), ConvCap);
        }

        // Same calculation before display rounding. Damage verification determines
        // whether the game uses the fractional value internally.
        public static double ConvExact(double value, int jitter, long word)
        {
            double d = Math.Abs(value) * jitter / 100.0;
            double v = value - d + word * (2.0 * d) / M;
            return Math.Min(v, ConvCap);
        }

        // Whether conversion can produce an exact value. The upper endpoint uses the
        // last reachable generator value (`x = M - 1`).
        public static bool ConvCanGive(double value, int jitter, double got)
        {
            double d = Math.Abs(value) * jitter / 100.0;
            long a = (long)Math.Min(Math.Round(value - d, MidpointRounding.AwayFromZero), ConvCap);
            long b = (long)Math.Min(Math.Round(value + d - 2.0 * d / M, MidpointRounding.AwayFromZero), ConvCap);
            long g = (long)Math.Round(got, MidpointRounding.AwayFromZero);
            return g >= Math.Min(a, b) && g <= Math.Max(a, b);
        }

        // Return the raw roll before `attributeScalePercent`; the game sums raw rolls
        // from item parts and applies the multiplier once.
        public static long Raw(string field, double value, long word)
        {
            // Conversion uses its own rule and has no item multiplier.
            if (IsConversion(field)) return ConvValue(value, ItemViewFactory.RollJitter, word);
            long lo, hi;
            if (!Bounds(field, value, 0, out lo, out hi)) return (long)value;
            double sign = value < 0 ? -1 : 1;
            long a = Math.Min(Math.Abs(lo), Math.Abs(hi)), b = Math.Max(Math.Abs(lo), Math.Abs(hi));
            return Clamp(value, (long)(sign * (a + word % (b - a + 1))));
        }

        // Apply the multiplier to the combined integer using integer arithmetic.
        public static long Scale(string field, long raw, int scale)
        {
            double sign = raw < 0 ? -1 : 1;
            return (long)(sign * ItemViewFactory.RollScaled(field, Math.Abs(raw), scale));
        }

        // Rolled field value from one generator word.
        public static long Value(string field, double value, int scale, long word)
        {
            // Conversion has separate bounds and display logic. Jitter comes from the
            // same record as the field; callers use 20% for the base by default.
            if (IsConversion(field)) return ConvValue(value, ItemViewFactory.RollJitter, word);
            long lo, hi;
            if (!Bounds(field, value, scale, out lo, out hi)) return (long)value;
            double sign = value < 0 ? -1 : 1;
            long a = Math.Min(Math.Abs(lo), Math.Abs(hi)), b = Math.Max(Math.Abs(lo), Math.Abs(hi));
            // Clamp before applying the item multiplier.
            long j = Math.Abs(Clamp(value, (long)(sign * (a + word % (b - a + 1)))));
            return (long)(sign * (ItemViewFactory.RollScaled(field, j, scale)));
        }

        // Same calculation before truncating the scaled result; see `RollScaledExact`.
        public static double ValueExact(string field, double value, int scale, long word)
        {
            if (IsConversion(field)) return ConvExact(value, ItemViewFactory.RollJitter, word);
            long lo, hi;
            if (!Bounds(field, value, scale, out lo, out hi)) return value;
            double sign = value < 0 ? -1 : 1;
            long a = Math.Min(Math.Abs(lo), Math.Abs(hi)), b = Math.Max(Math.Abs(lo), Math.Abs(hi));
            long j = Math.Abs(Clamp(value, (long)(sign * (a + word % (b - a + 1)))));
            return sign * ItemViewFactory.RollScaledExact(field, j, scale);
        }
    }
}
