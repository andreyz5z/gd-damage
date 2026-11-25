// "WHICH SKILL TO HANG THE CELESTIAL POWER ON" — the chooser, as the game has it.
//
// Opened by clicking a taken star that carries a power (the same door GrimTools'
// calculator uses: a click the rules turn down opens the chooser on such a star).
//
// The list and the rules are `DevotionBinding`'s; this is only the showing of them —
// three columns of rows with the skill's own icon, the way the game lays it out. The
// colours follow the game too: a skill the character has not learned yet is written
// in red and is offered all the same.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class DevotionBindForm : Form
    {
        readonly GameData game;
        readonly Character character;
        readonly string power;

        // Null means "no skill": the power is taken off whatever it hung on.
        public string Chosen { get; private set; }
        public bool Changed { get; private set; }

        public DevotionBindForm(GameData game, Character character, DevotionStar star)
        {
            this.game = game;
            this.character = character;
            this.power = star.Dbr;

            AppIcon.Apply(this);
            Text = Lang.T("skills.bind.title", SkillText.Name(game, star.Dbr) ?? star.Home.Name);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(18, 17, 16);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            List<PowerTarget> choices = DevotionBinding.Choices(game, character, star.Dbr);

            // Three columns, the way the game lays this window out; the height
            // follows the list so a short one does not leave a hole. How many rows
            // fit in a column is the game's own number — `maxVerticalSkills` of
            // `devotion_skillselect.dbr` — and a longer list gets more columns.
            const int rowHeight = 30, rowWidth = 300;
            DbRecord window = game.Record("records/ui/skills/devotion/devotion_skillselect.dbr");
            int maxRows = window == null ? 0 : window.GetInt("maxVerticalSkills");
            if (maxRows <= 0) maxRows = 15;
            int columns = 3;
            while ((choices.Count + columns - 1) / columns > maxRows) columns++;
            int rows = (choices.Count + columns - 1) / columns;
            ClientSize = new Size(columns * rowWidth + 24, Math.Min(760, rows * rowHeight + 60));

            Panel list = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(14, 13, 12),
                Padding = new Padding(8),
            };
            for (int i = 0; i < choices.Count; i++)
            {
                PowerTarget t = choices[i];
                Control row = Row(t, rowWidth - 8, rowHeight - 2);
                row.Left = 8 + (i % columns) * rowWidth;
                row.Top = 8 + (i / columns) * rowHeight;
                list.Controls.Add(row);
            }
            Controls.Add(list);

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = BackColor };
            Label legend = new Label
            {
                AutoSize = true, Left = 10, Top = 13, ForeColor = Color.DimGray,
                Text = Lang.T("skills.bind.legend"),
            };
            Button cancel = new Button
            {
                Text = Lang.T("common.cancel"), Top = 8, Height = 28, Width = 110,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(legend);
            bottom.Controls.Add(cancel);
            bottom.Resize += delegate { cancel.Left = bottom.Width - cancel.Width - 12; };
            cancel.Left = bottom.Width - cancel.Width - 12;
            Controls.Add(bottom);
        }

        // One line: the skill's own icon, its name, and what is already true about
        // it — that it is the one the power hangs on now, that another power has
        // taken it, or that the character has not learned it yet.
        Control Row(PowerTarget t, int width, int height)
        {
            Panel row = new Panel
            {
                Width = width,
                Height = height,
                BackColor = t.Current ? Color.FromArgb(44, 41, 34) : Color.FromArgb(22, 21, 20),
                Cursor = Cursors.Hand,
            };
            PictureBox icon = new PictureBox
            {
                Left = 2, Top = (height - 26) / 2, Width = 26, Height = 26,
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
            };
            icon.Image = Texture(t.None ? DevotionBinding.UnassignedIcon(game) : SkillText.IconOf(game, t.Dbr));
            row.Controls.Add(icon);

            string text = t.Name;
            if (t.Current) text += "  " + Lang.T("skills.bind.chosen");
            else if (t.TakenBy != null) text += "  " + Lang.T("skills.bind.busy", t.TakenBy);
            else if (t.FromItem) text += "  " + Lang.T("skills.bind.fromItem");
            Label name = new Label
            {
                Left = 32, Top = 0, Width = width - 36, Height = height,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                // Not learned — the game writes such a skill in red and still offers
                // it, because a tie made now works as soon as the points go in.
                ForeColor = t.None ? Color.Gainsboro
                          : !t.Learned ? Color.FromArgb(196, 84, 76)
                          : t.Current ? Color.FromArgb(255, 226, 140)
                          : Color.Gainsboro,
                Text = text,
            };
            row.Controls.Add(name);

            EventHandler pick = delegate { Chosen = t.None ? null : t.Dbr; Apply(); };
            row.Click += pick;
            name.Click += pick;
            icon.Click += pick;
            EventHandler over = delegate { if (!t.Current) row.BackColor = Color.FromArgb(38, 36, 32); };
            EventHandler out_ = delegate { if (!t.Current) row.BackColor = Color.FromArgb(22, 21, 20); };
            row.MouseEnter += over; name.MouseEnter += over; icon.MouseEnter += over;
            row.MouseLeave += out_; name.MouseLeave += out_; icon.MouseLeave += out_;
            return row;
        }

        Image Texture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Image got = game.SkillIcon(path);
            if (got != null) return got;
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                 ? game.SkillIcon(path.Substring(0, path.Length - 4) + ".tex") : null;
        }

        void Apply()
        {
            Changed = DevotionBinding.Bind(game, character, power, Chosen);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
