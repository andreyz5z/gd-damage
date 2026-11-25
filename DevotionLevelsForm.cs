// Devotion skill-level editor.
//
// A granted devotion skill derives its level from stored experience. The editor
// changes the level while keeping experience on a valid threshold so the game
// does not recalculate it unexpectedly.
//
// Data model:
//   active skill             `Level` on the skill record; inactive skills retain their saved level
//   experience thresholds    `skillExperienceLevels`, possibly through a controller chain
//   maximum level            number of thresholds on the skill's own record
//   level L                  `DevotionLevel = L`, with threshold[L-1] <= xp < threshold[L]

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public static partial class DevotionLevels
    {
        public sealed class Star
        {
            public Skill Entry;
            public string Name = "";
            public double[] Ladder;
            public int Level;
        }

        // `--devolevelsbreak` changes the level without changing experience;
        // the check must fail.
        public static bool BreakExperience;

        // Read `skillExperienceLevels` from the skill record or its controller,
        // using the same lookup as `--devotest`.
        public static double[] Ladder(GameData game, string dbr)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord r = game == null ? null : game.Record(dbr);
                if (r == null) break;
                string text = r.Get("skillExperienceLevels");
                if (!string.IsNullOrEmpty(text))
                {
                    List<double> steps = new List<double>();
                    foreach (string part in text.Split(';'))
                    {
                        double v;
                        if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) steps.Add(v);
                    }
                    return steps.Count > 0 ? steps.ToArray() : null;
                }
                dbr = ItemViewFactory.NextSkillRecord(r);
            }
            return null;
        }

        // Granted devotion skills: non-passive records with a point and thresholds.
        public static List<Star> Taken(GameData game, Character c)
        {
            List<Star> made = new List<Star>();
            if (game == null || c == null) return made;
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || !SkillsForm.IsDevotion(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || (rec.Class ?? "").StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)) continue;
                double[] ladder = Ladder(game, s.Name);
                if (ladder == null) continue;
                made.Add(new Star
                {
                    Entry = s,
                    Ladder = ladder,
                    Name = SkillText.Name(game, s.Name) ?? s.Name,
                    Level = Math.Max(1, Math.Min(ladder.Length, s.DevotionLevel)),
                });
            }
            return made;
        }

        // Check whether experience belongs to the current level threshold.
        public static bool InBand(Skill s, double[] ladder)
        {
            int level = s.DevotionLevel;
            if (ladder == null || level < 1 || level > ladder.Length) return false;
            double low = ladder[level - 1];
            double high = level < ladder.Length ? ladder[level] : double.MaxValue;
            return s.DevotionExperience >= low && s.DevotionExperience < high;
        }

        // Set a level. Keep experience inside its current threshold when possible;
        // otherwise use the lower bound of the target threshold.
        public static bool Apply(Star st, int level)
        {
            level = Math.Max(1, Math.Min(st.Ladder.Length, level));
            Skill s = st.Entry;
            bool changed = s.DevotionLevel != level;
            s.DevotionLevel = level;
            st.Level = level;
            if (!BreakExperience && !InBand(s, st.Ladder))
            {
                s.DevotionExperience = (int)st.Ladder[level - 1];
                changed = true;
            }
            return changed;
        }

        // Window test: `--devolevelstest <save> <folder>`. On a copy, set granted
        // skills to several levels, write and reload after each change, and verify
        // the level, threshold placement, untouched records, and file length.
        #region Self-check — not interface (the `--devolevelstest` report, not moved into the language files)
        #endregion
    }

    public class DevotionLevelsForm : Form
    {
        readonly List<DevotionLevels.Star> stars;
        readonly List<NumericUpDown> boxes = new List<NumericUpDown>();

        public bool Changed { get; private set; }

        public DevotionLevelsForm(GameData game, Character c)
        {
            stars = DevotionLevels.Taken(game, c);

            AppIcon.Apply(this);
            // Window text comes from `lang/<CODE>/skills.txt` and `common.txt`.
            Text = Lang.T("skills.devotionLevels.title", c == null ? "" : c.Name);
            Width = 560;
            Height = Math.Max(200, Math.Min(720, 150 + stars.Count * 34));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 22, 20);

            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 3,
                Padding = new Padding(12, 10, 12, 6),
                BackColor = Color.FromArgb(20, 19, 18),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            foreach (DevotionLevels.Star st in stars)
            {
                table.Controls.Add(new Label
                {
                    Text = st.Name,
                    AutoSize = true,
                    ForeColor = Color.Gainsboro,
                    Margin = new Padding(0, 7, 8, 3),
                });
                NumericUpDown box = new NumericUpDown
                {
                    Minimum = 1,
                    Maximum = st.Ladder.Length,
                    Value = st.Level,
                    Width = 60,
                    Margin = new Padding(0, 3, 6, 3),
                    ForeColor = Color.Gainsboro,
                    BackColor = Color.FromArgb(38, 35, 32),
                    Tag = st,
                };
                boxes.Add(box);
                table.Controls.Add(box);
                table.Controls.Add(new Label
                {
                    Text = Lang.T("skills.devotionLevels.of", st.Ladder.Length),
                    AutoSize = true,
                    ForeColor = Color.DimGray,
                    Margin = new Padding(0, 7, 0, 3),
                });
            }
            if (stars.Count == 0)
                table.Controls.Add(new Label
                {
                    Text = Lang.T("skills.devotionLevels.none"),
                    AutoSize = true,
                    ForeColor = Color.DimGray,
                });

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = BackColor };
            Button ok = Make(Lang.T("common.apply"), 8);
            Button cancel = Make(Lang.T("common.cancel"), ok.Right + 10);
            bottom.Controls.AddRange(new Control[] { ok, cancel });

            Controls.Add(table);
            Controls.Add(bottom);

            ok.Click += delegate { Apply(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        }

        Button Make(string text, int left)
        {
            return new Button
            {
                Text = text, Left = left, Top = 9, Width = TextRenderer.MeasureText(text, Font).Width + 30,
                Height = 28, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32),
                FlatStyle = FlatStyle.Flat,
            };
        }

        // Only changed skills are written; untouched records remain byte-for-byte identical.
        void Apply()
        {
            bool changed = false;
            foreach (NumericUpDown box in boxes)
            {
                DevotionLevels.Star st = (DevotionLevels.Star)box.Tag;
                int want = (int)box.Value;
                if (want != st.Level) changed |= DevotionLevels.Apply(st, want);
            }
            Changed = changed;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
