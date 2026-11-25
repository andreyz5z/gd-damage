// The strip under the bags that destroys whatever is dropped on it. It is only
// live while an item is in hand, so a stray click on an idle window cannot lose
// anything.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class DropZone : Control
    {
        bool armed;
        bool hot;

        public string IdleText = "";
        public string ArmedText = "";

        public DropZone()
        {
            DoubleBuffered = true;
            Height = 34;
            BackColor = Color.FromArgb(20, 19, 18);
        }

        // True once the caller has an item in hand.
        public bool Armed
        {
            get { return armed; }
            set { if (armed == value) return; armed = value; if (!armed) hot = false; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!armed) return;
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!hot) return;
            hot = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle box = new Rectangle(4, 4, Math.Max(1, Width - 9), Math.Max(1, Height - 9));

            Color edge = !armed ? Color.FromArgb(60, 54, 48)
                       : hot ? Color.FromArgb(230, 90, 80)
                             : Color.FromArgb(150, 70, 64);
            Color text = !armed ? Color.FromArgb(96, 90, 82)
                       : hot ? Color.FromArgb(255, 210, 200)
                             : Color.FromArgb(210, 140, 130);

            if (armed)
                using (SolidBrush fill = new SolidBrush(hot
                           ? Color.FromArgb(90, 150, 45, 40)
                           : Color.FromArgb(40, 120, 50, 45)))
                    g.FillRectangle(fill, box);

            using (Pen p = new Pen(edge) { DashStyle = armed && !hot ? DashStyle.Dash : DashStyle.Solid })
                g.DrawRectangle(p, box);

            string caption = armed ? ArmedText : IdleText;
            using (SolidBrush b = new SolidBrush(text))
            using (StringFormat f = new StringFormat
                   { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(caption, Font, b, box, f);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }
}
