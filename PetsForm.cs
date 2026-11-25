// Active pets: a checkable pool opened from the damage tab.
//
// Pool construction is handled by [PetPool.cs](PetPool.cs). This form groups pets
// by source and displays their count, lifetime, and skills.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class PetsForm : Form
    {
        readonly List<CheckBox> boxes = new List<CheckBox>();
        readonly ActiveSkills chosen;
        readonly GameData game;

        public bool Changed { get; private set; }

        public PetsForm(GameData game, Character c, ActiveSkills chosen)
        {
            this.chosen = chosen;
            this.game = game;
            List<PetPool.Pet> list = PetPool.Of(game, c, chosen);
            // The total is needed for conversion values stored in pet-bonus records.
            // If unavailable, display the pet without conversion.
            CharacterTotals totals = null;
            try { totals = CharacterTotals.Of(game, c, null, chosen); } catch (Exception) { }

            AppIcon.Apply(this);
            // UI text comes from `lang/<CODE>/pets.txt` and `common.txt`.
            Text = Lang.T("pets.title", c == null ? "" : c.Name);
            Width = 720;
            Height = 700;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 22, 20);

            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12, 8, 12, 6),
                BackColor = Color.FromArgb(20, 19, 18),
            };

            string group = null;
            foreach (PetPool.Pet p in list)
            {
                if (!string.Equals(group, p.Source, StringComparison.Ordinal))
                {
                    group = p.Source;
                    flow.Controls.Add(new Label
                    {
                        Text = group, AutoSize = true,
                        ForeColor = Color.FromArgb(190, 160, 90),
                        Margin = new Padding(0, 10, 0, 2),
                    });
                }
                CheckBox b = new CheckBox
                {
                    Text = Lang.T("pets.checkbox", p.Name, p.Title, p.Level),
                    Checked = p.Chosen,
                    AutoSize = true,
                    ForeColor = Color.Gainsboro,
                    Margin = new Padding(16, 2, 0, 0),
                    Tag = p,
                };
                boxes.Add(b);
                flow.Controls.Add(b);

                List<string> facts = new List<string>();
                if (p.Limit > 1) facts.Add(Lang.T("pets.limit", p.Limit));
                if (p.Seconds > 0) facts.Add(Lang.T("pets.lifetime", p.Seconds));
                // Display base and bonus health separately because some percentage
                // modifiers are not known.
                if (p.BaseLife > 0)
                    facts.Add(p.AuraLife != 0
                        ? Lang.T("pets.lifeAura", Math.Round(p.BaseLife), Math.Round(p.OwnLife), Math.Round(p.LifeBonus),
                                 Math.Round(p.AuraLife), p.Life)
                        : Lang.T("pets.life", Math.Round(p.BaseLife), Math.Round(p.OwnLife), Math.Round(p.LifeBonus), p.Life));
                // Display pet armor from its passive record; total modifiers are not
                // fully modeled by `PetPool.Pet.Armour`.
                if (p.BaseArmour > 0.5)
                    facts.Add(Lang.T("pets.armour", Math.Round(p.BaseArmour), Math.Round(p.OwnArmour),
                                     Math.Round(p.ArmourBonus), p.Armour));
                // Character auras are shown separately and are not applied to pet health.
                if (p.Auras.Count > 0) facts.Add(Lang.T("pets.auras", string.Join(", ", p.Auras.ToArray())));
                // This pet's conversion is shown separately; skill values are displayed
                // before conversion, as in the game tooltip.
                foreach (TipLine one in PetPool.ConversionLines(game, p.AllConv, 0))
                    facts.Add(one.Text);
                if (facts.Count > 0)
                    flow.Controls.Add(new Label
                    {
                        Text = string.Join(";  ", facts.ToArray()),
                        AutoSize = true,
                        MaximumSize = new Size(640, 0),
                        ForeColor = Color.DimGray,
                        Margin = new Padding(36, 0, 0, 6),
                    });

                // Show pet skills with values after pet bonuses and conversion. The
                // separate damage tab shows the game's pre-conversion presentation.
                List<Move> petWide = p.ConvMoves;
                for (int i = 0; i < p.SkillDbrs.Count; i++)
                {
                    flow.Controls.Add(new Label
                    {
                        Text = p.Skills[i],
                        AutoSize = true,
                        ForeColor = Color.FromArgb(120, 175, 235),
                        Margin = new Padding(36, 4, 0, 0),
                    });
                    foreach (TipLine one in PetPool.SkillLines(game, p.SkillDbrs[i], p.SkillLevels[i], p.Bonus, 0, petWide, p.Scale, p.OwnConv))
                    {
                        if (string.IsNullOrEmpty(one.Text)) continue;
                        flow.Controls.Add(new Label
                        {
                            Text = one.Text,
                            AutoSize = true,
                            MaximumSize = new Size(620, 0),
                            ForeColor = Color.FromArgb(150, 200, 150),
                            Margin = new Padding(52, 0, 0, 0),
                        });
                    }
                }
            }
            if (list.Count == 0)
                flow.Controls.Add(new Label
                {
                    Text = Lang.T("pets.none"),
                    AutoSize = true, ForeColor = Color.DimGray,
                });

            Label note = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                Padding = new Padding(12, 4, 12, 0),
                ForeColor = Color.DimGray,
                Text = Lang.T("pets.note"),
            };

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = BackColor };
            Button ok = Make(Lang.T("common.apply"), 8);
            Button cancel = Make(Lang.T("common.cancel"), 130);
            bottom.Controls.AddRange(new Control[] { ok, cancel });

            Controls.Add(flow);
            Controls.Add(note);
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

        // Use the summon-skill record as the selection key, matching the active-skills
        // view so the same summoned pet is selected in both forms.
        void Apply()
        {
            foreach (CheckBox b in boxes)
            {
                PetPool.Pet p = (PetPool.Pet)b.Tag;
                if (b.Checked == p.Default) chosen.Forget(p.Skill);
                else chosen.Set(p.Skill, b.Checked);
            }
            Changed = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
