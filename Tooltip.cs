// The floating item description, shaped after the one the game shows on hover.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public enum TipKind
    {
        Name,       // item name, coloured by rarity
        Subtle,     // bound / type / flavour text
        Headline,   // armour or damage summary
        Bonus,      // a stat the item grants
        Skill,      // granted or boosted skill
        Section,    // component / augment header
        Warning,    // something is missing
        Plain,
        Table       // armor table by slot; rendered in white like the game
    }

    // A picture and the number that stands beside it, drawn inside a line after its
    // text. The game shows affinity this way — a coloured ball and a figure, never
    // the name of the affinity — and there is no way to put a picture inside a
    // string, so the painter draws these itself.
    public sealed class TipOrb
    {
        public Image Icon;
        public string Text = "";
        public TipOrb() { }
        public TipOrb(Image icon, string text) { Icon = icon; Text = text; }
    }

    public class TipLine
    {
        public string Text = "";
        // The balls this line ends with, if any. Null for every ordinary line.
        public List<TipOrb> Orbs;
        public TipKind Kind = TipKind.Plain;
        public int Indent;
        public Color Override = Color.Empty;
        // Optional font size and weight; zero uses the default for this row type.
        // Timeline DPS is shown larger and brighter than other values.
        public float Size;

        // Which record fields this line was built from. A summed line carries them
        // so the view can show where the number came from; an ordinary line leaves
        // it null and stays unremarkable under the mouse.
        public string[] Explains;

        // True when the line is a "min - max" pair for one bonus, so the first two
        // entries of Explains are its two ends. Both of them roll, and showing
        // the spread of only the first read as if the second were outside its own.
        public bool Paired;

        // Number source: 0 base, 1 prefix, 2 suffix, 10+ pet bonus,
        // -1 not rolled (skill, set, or component). Used by item editing to
        // associate seed values with the correct item part. Set only when
        // rolled values are present; otherwise a set bonus could look like a
        // base field.
        public int Part = -1;

        // The field position in the roll order is inferred rather than measured,
        // so its value may differ from the game. Mark the row and tooltip border
        // in red; `ItemViewFactory` sets this through `ItemOrder.DoubtOf`.
        public bool Doubt;

        // Duration represented by the displayed value. A damage-over-time line
        // may show the total for several seconds while the record stores one
        // second; item editing uses this value to reconcile the two forms.
        public double PerTick = 1;

        public TipLine() { }
        public TipLine(string text, TipKind kind) { Text = text; Kind = kind; }
        public TipLine(string text, TipKind kind, int indent) { Text = text; Kind = kind; Indent = indent; }

        // How wide the balls of this line are, and how tall a line holding them has
        // to be. One place, so the measuring and the painting cannot drift apart.
        public const int OrbSize = 16, OrbGap = 4;

        public int OrbsWidth(Graphics g, Font f)
        {
            if (Orbs == null) return 0;
            int width = 0;
            foreach (TipOrb orb in Orbs)
                width += OrbGap + OrbSize + 2 + (int)Math.Ceiling(g.MeasureString(orb.Text, f).Width);
            return width;
        }

        // A headless run reads the lines as text, so the balls say their affinity in
        // words there — the picture cannot be written into a file.
        public override string ToString()
        {
            string text = new string(' ', Indent * 2) + Text;
            if (Orbs == null) return text;
            foreach (TipOrb orb in Orbs) text += " " + orb.Text;
            return text;
        }
    }

    public static class TipPalette
    {
        // Weapon damage share by hand uses its own color so the row can be found
        // among ordinary bonus rows and identify which hand performs the attack.
        // Yellow is lighter and cooler than the section header (220, 180, 110).
        public static readonly Color Hand = Color.FromArgb(240, 220, 130);

        public static Color Rarity(string classification)
        {
            switch (classification)
            {
                case "Legendary": return Color.FromArgb(190, 120, 235);
                case "Epic": return Color.FromArgb(105, 145, 235);
                case "Rare": return Color.FromArgb(120, 210, 120);
                case "Magical": return Color.FromArgb(225, 205, 110);
                case "Quest": return Color.FromArgb(230, 180, 90);
                default: return Color.FromArgb(230, 228, 222);
            }
        }

        public static Color Of(TipKind kind)
        {
            switch (kind)
            {
                case TipKind.Subtle: return Color.FromArgb(160, 155, 145);
                case TipKind.Headline: return Color.FromArgb(230, 228, 222);
                case TipKind.Bonus: return Color.FromArgb(150, 205, 140);
                case TipKind.Skill: return Color.FromArgb(140, 190, 235);
                case TipKind.Section: return Color.FromArgb(220, 180, 110);
                case TipKind.Warning: return Color.FromArgb(230, 110, 100);
                // Slot armor is white to distinguish it from ordinary green bonuses.
                case TipKind.Table: return Color.FromArgb(245, 245, 245);
                default: return Color.FromArgb(215, 212, 205);
            }
        }
    }

    // Sends the wheel to whatever tooltip is up.
    //
    // The tooltip is a window that never takes the focus — it must not, or the
    // title bar would grey out every time a description pops up — so the wheel
    // message goes to the control under the cursor instead, which is the item
    // itself. The game behaves the same way: the cursor stays on the item and the
    // description scrolls. So the message is caught before the control sees it.
    //
    // It is only taken when the tooltip has something to scroll to AND the scroll
    // actually moved. At either end the message is passed on, so a long
    // description in the catalogue does not trap the wheel: scroll past its
    // bottom and the list underneath carries on.
    static class Wheel
    {
        const int WM_MOUSEWHEEL = 0x020A;
        static readonly List<ItemTooltip> live = new List<ItemTooltip>();
        static Filter filter;

        class Filter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (delta == 0) return false;
                // Three text lines a notch, the same feel as a list box.
                int step = -Math.Sign(delta) * 54;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    ItemTooltip t = live[i];
                    if (!t.Scrollable) continue;
                    if (t.ScrollBy(step)) return true;
                }
                return false;
            }
        }

        public static void Watch(ItemTooltip t)
        {
            if (filter == null) { filter = new Filter(); Application.AddMessageFilter(filter); }
            if (!live.Contains(t)) live.Add(t);
        }

        public static void Forget(ItemTooltip t) { live.Remove(t); }
    }

    public class ItemTooltip : Form
    {
        readonly List<TipLine> lines = new List<TipLine>();
        Color nameColor = Color.White;
        Image icon;

        static readonly Font NameFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        static readonly Font BodyFont = new Font("Segoe UI", 9f);

        const int Pad = 10;
        const int MaxWidth = 460;

        public ItemTooltip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 16, 14);
            DoubleBuffered = true;
            TopMost = true;
        }

        // A tooltip must never take focus away from the window under the cursor.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE — never take the focus
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — stay out of alt-tab
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        // Where the content is scrolled to, in pixels, and how far it can go. A
        // description taller than the screen used to be cut off with a line saying
        // how much was left out; now the whole of it is kept and the wheel moves
        // it, the way the game does — without having to take the cursor off the
        // item, which would hide the tooltip on the way.
        int scroll;
        int contentHeight;
        int viewHeight;
        object shownFor;

        public int MaxScroll { get { return Math.Max(0, contentHeight - viewHeight); } }
        public bool Scrollable { get { return Visible && MaxScroll > 0; } }

        // Returns whether the content actually moved. False at either end, so the
        // caller can pass the wheel on to whatever is underneath instead of the
        // scroll dying silently against a stop.
        public bool ScrollBy(int pixels)
        {
            int want = Math.Max(0, Math.Min(MaxScroll, scroll + pixels));
            if (want == scroll) return false;
            scroll = want;
            Invalidate();
            return true;
        }

        public void ShowFor(ItemView view, Point screenPoint)
        {
            if (view == null) { if (Visible) Hide(); return; }
            // ShowFor runs on every mouse move, so the scroll may only be reset
            // when the description underneath has actually changed — otherwise it
            // would snap back to the top under the cursor's own jitter.
            object key = Key(view);
            if (!Equals(key, shownFor)) { scroll = 0; shownFor = key; }
            lines.Clear();
            lines.AddRange(view.Details);
            nameColor = TipPalette.Rarity(view.Classification);
            // No icon: the game does not put one in the description either, and
            // the item is already in plain sight in the slot the cursor is on.
            icon = null;

            Size size = Measure(Screen.FromPoint(screenPoint).WorkingArea.Height - 24);
            if (scroll > MaxScroll) scroll = MaxScroll;
            // Moving the window that is already up, instead of hiding and showing
            // it again, is what stops the title bar flickering while the cursor
            // runs across a row of items.
            Bounds = new Rectangle(Fit(screenPoint, size), size);
            if (!Visible) Show();
            Wheel.Watch(this);
            Invalidate();
        }

        // Two different views of the same item are built all over the place, so
        // identity cannot be the object itself; the text is what the reader sees
        // change, and that is what should reset the scroll.
        static object Key(ItemView v)
        {
            return (v.Name ?? "") + "" + v.Details.Count + "" +
                   (v.Details.Count > 1 ? v.Details[1].Text : "");
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { scroll = 0; shownFor = null; Wheel.Forget(this); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Wheel.Forget(this);
            base.Dispose(disposing);
        }

        static Point Fit(Point at, Size size)
        {
            Rectangle screen = Screen.FromPoint(at).WorkingArea;
            int x = at.X + 18, y = at.Y + 18;
            if (x + size.Width > screen.Right) x = at.X - size.Width - 8;
            if (y + size.Height > screen.Bottom) y = screen.Bottom - size.Height - 4;
            if (x < screen.Left) x = screen.Left + 4;
            if (y < screen.Top) y = screen.Top + 4;
            return new Point(x, y);
        }

        // Long descriptions — above all the full record dump — run off the bottom
        // of the screen. Nothing is dropped: the window is capped at the height of
        // the screen and the rest is reached with the wheel.
        Size Measure(int maxHeight)
        {
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                int iconHeight = icon != null ? icon.Height + 6 : 0;
                int budget = maxHeight - Pad * 2 - iconHeight;
                int width = 0, height = 0;
                foreach (TipLine line in lines)
                {
                    if (line.Text.Length == 0) { height += 7; continue; }
                    Font f = line.Kind == TipKind.Name ? NameFont : BodyFont;
                    SizeF s = g.MeasureString(line.Text, f, MaxWidth - Pad * 2 - line.Indent * 12);
                    int orbs = line.OrbsWidth(g, f);
                    width = Math.Max(width, (int)Math.Ceiling(s.Width) + orbs + line.Indent * 12);
                    height += Math.Max((int)Math.Ceiling(s.Height), orbs > 0 ? TipLine.OrbSize : 0) + 1;
                }

                contentHeight = height;
                viewHeight = Math.Min(height, Math.Max(80, budget));
                width = Math.Max(width, icon != null ? icon.Width : 0);
                return new Size(Math.Min(MaxWidth, width + Pad * 2), viewHeight + Pad * 2 + iconHeight);
            }
        }

        // The balls of a line, after its text: the game's own coloured picture and
        // the figure beside it. Shared by the floating description and the panel
        // that draws the same lines (`TipView`), so the two cannot drift apart.
        public static void PaintOrbs(Graphics g, TipLine line, Font f, Color colour, int x, int y)
        {
            if (line.Orbs == null) return;
            foreach (TipOrb orb in line.Orbs)
            {
                x += TipLine.OrbGap;
                if (Usable(orb.Icon))
                    g.DrawImage(orb.Icon, x, y + 1, TipLine.OrbSize, TipLine.OrbSize);
                x += TipLine.OrbSize + 2;
                using (SolidBrush b = new SolidBrush(colour))
                    g.DrawString(orb.Text, f, b, x, y + (TipLine.OrbSize - f.Height) / 2f + 1);
                x += (int)Math.Ceiling(g.MeasureString(orb.Text, f).Width);
            }
        }

        // A bitmap that has been disposed throws on every access; treat it as
        // missing rather than letting it take the window down.
        static bool Usable(Image img)
        {
            if (img == null) return false;
            try { return img.Width > 0 && img.Height > 0; }
            catch (Exception) { return false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // Use a red border when any item value comes from an inferred roll
            // position, making affected items visible without inspecting each row.
            bool suspect = false;
            foreach (TipLine one in lines) if (one.Doubt) { suspect = true; break; }
            using (Pen border = new Pen(suspect ? Color.FromArgb(200, 60, 55) : Color.FromArgb(90, 78, 58),
                                        suspect ? 2f : 1f))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            if (icon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(icon, Width - icon.Width - Pad, Pad, icon.Width, icon.Height);
            }

            // Everything below the icon scrolls, so it is drawn into a clipped
            // band with the origin pushed up by the scroll offset.
            int top = Pad + (icon != null ? icon.Height + 6 : 0);
            // Start the clipping strip five pixels left of the text so the red row
            // marker remains visible without cutting the fields.
            Rectangle band = new Rectangle(Pad - 5, top, Math.Max(1, Width - Pad * 2 + 5), Math.Max(1, viewHeight));
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.SetClip(band);
            int y = top - scroll;

            foreach (TipLine line in lines)
            {
                if (line.Text.Length == 0) { y += 7; continue; }
                Font f = line.Kind == TipKind.Name ? NameFont : BodyFont;
                int x = Pad + line.Indent * 12;
                int available = Width - x - Pad;
                RectangleF box = new RectangleF(x, y, Math.Max(40, available), contentHeight);
                int lineHeight = (int)Math.Ceiling(g.MeasureString(line.Text, f, (int)box.Width).Height) + 1;
                if (line.Orbs != null) lineHeight = Math.Max(lineHeight, TipLine.OrbSize + 2);
                // Off the top or off the bottom of the band: nothing to draw.
                if (y + lineHeight >= band.Top && y <= band.Bottom)
                {
                    Color c = line.Override != Color.Empty ? line.Override
                            : line.Kind == TipKind.Name ? nameColor
                            : TipPalette.Of(line.Kind);
                    // Draw the inferred-position marker as a strip rather than text,
                    // preserving wrapping and available width.
                    if (line.Doubt)
                        using (SolidBrush m = new SolidBrush(Color.FromArgb(200, 60, 55)))
                            g.FillRectangle(m, Pad - 4, y + 3, 3, Math.Max(6, lineHeight - 6));
                    using (SolidBrush b = new SolidBrush(c)) g.DrawString(line.Text, f, b, box);
                    PaintOrbs(g, line, f, c, x + (int)Math.Ceiling(g.MeasureString(line.Text, f).Width), y);
                }
                y += lineHeight;
            }
            g.Restore(state);

            // A hint that there is more, and where in it you are. The game draws
            // nothing at all here, but on a desktop nobody would guess the wheel
            // does something without it, so it is kept faint.
            if (MaxScroll > 0)
            {
                int trackTop = band.Top, trackHeight = band.Height;
                int thumb = Math.Max(18, trackHeight * viewHeight / Math.Max(1, contentHeight));
                int offset = (trackHeight - thumb) * scroll / MaxScroll;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 200, 180, 130)))
                    g.FillRectangle(b, Width - 5, trackTop + offset, 3, thumb);
            }
        }
    }
}
