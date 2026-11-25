// Active-skill selection window opened from the damage tab.
//
// The window intentionally shows only grouped skill names and checkboxes.
// Inferred state remains the initial checkbox state and is reported by
// `--activeshot`.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class ActiveSkillsForm : Form
    {
        readonly List<ActiveSkills.Candidate> list;
        readonly List<CheckBox> boxes = new List<CheckBox>();
        readonly ActiveSkills chosen;

        public bool Changed { get; private set; }

        public ActiveSkillsForm(GameData game, Character c, ActiveSkills chosen)
        {
            this.chosen = chosen;
            this.list = ActiveSkills.Of(game, c, chosen);

            AppIcon.Apply(this);
            // Window text comes from `lang/<CODE>/active.txt` and `common.txt`.
            Text = Lang.T("active.title", c == null ? "" : c.Name);
            Width = 620;
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
            foreach (ActiveSkills.Candidate one in list)
            {
                if (!string.Equals(group, one.Source, StringComparison.Ordinal))
                {
                    group = one.Source;
                    flow.Controls.Add(new Label
                    {
                        Text = group,
                        AutoSize = true,
                        ForeColor = Color.FromArgb(190, 160, 90),
                        Margin = new Padding(0, 10, 0, 2),
                    });
                }
                flow.Controls.Add(new CheckBox
                {
                    Text = one.Title,
                    Checked = one.Chosen,
                    AutoSize = true,
                    ForeColor = Color.Gainsboro,
                    Margin = new Padding(16, 2, 0, 2),
                    Tag = one,
                });
                boxes.Add((CheckBox)flow.Controls[flow.Controls.Count - 1]);
            }
            if (list.Count == 0)
                flow.Controls.Add(new Label
                {
                    Text = Lang.T("active.none"),
                    AutoSize = true, ForeColor = Color.DimGray,
                });

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = BackColor };
            Button ok = Make(Lang.T("common.apply"), 8);
            Button cancel = Make(Lang.T("common.cancel"), 130);
            bottom.Controls.AddRange(new Control[] { ok, cancel });

            Controls.Add(flow);
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

        // Store only overrides. Matching inferred state is not persisted, so
        // future rule changes can update the default safely.
        void Apply()
        {
            foreach (CheckBox b in boxes)
            {
                ActiveSkills.Candidate one = (ActiveSkills.Candidate)b.Tag;
                if (b.Checked == one.Default) chosen.Forget(one.Dbr);
                else chosen.Set(one.Dbr, b.Checked);
            }
            Changed = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
