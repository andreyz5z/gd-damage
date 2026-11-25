// Draws one inventory bag as the game does: a grid of 32 pixel cells with each
// item occupying as many cells as its icon is wide and tall.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class InventoryGrid : Control
    {
        // The main bag is 12x8, the five side bags are 8x8. These are fixed by
        // the game and are not recorded in the save.
        public const int MainColumns = 12;
        public const int SideColumns = 8;
        public const int BagRows = 8;

        public static int ColumnsFor(int bagIndex) { return bagIndex == 0 ? MainColumns : SideColumns; }

        readonly List<ItemView> items = new List<ItemView>();
        ItemView hovered, selected;
        const int zoom = 1;   // the game draws one cell per 32 pixel icon block

        public event EventHandler SelectionChanged;
        public event EventHandler<ItemHoverEventArgs> ItemHovered;
        public ItemView Selected { get { return selected; } }

        public int Columns { get; private set; }
        public int Rows { get; private set; }

        public InventoryGrid()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 26, 24);
            Columns = MainColumns;
            Rows = BagRows;
        }

        public void SetGridSize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            Relayout();
        }

        public void SetItems(IEnumerable<ItemView> source)
        {
            items.Clear();
            selected = null;
            hovered = null;
            if (source != null) items.AddRange(source);
            Relayout();
            OnSelectionChanged();
        }

        public IList<ItemView> Items { get { return items; } }

        // True when every cell the rectangle covers is inside the bag and free,
        // ignoring one item (the one being moved).
        public bool IsFree(Rectangle area, ItemView ignore)
        {
            if (area.X < 0 || area.Y < 0 || area.Right > Columns || area.Bottom > Rows) return false;
            foreach (ItemView v in items)
            {
                if (v == ignore) continue;
                if (v.Cells.IntersectsWith(area)) return false;
            }
            return true;
        }

        // First free spot for an item of this size, scanning left to right and
        // top to bottom the way the game fills a bag.
        public Point? FindFreeSpot(int width, int height, ItemView ignore)
        {
            for (int y = 0; y + height <= Rows; y++)
                for (int x = 0; x + width <= Columns; x++)
                    if (IsFree(new Rectangle(x, y, width, height), ignore)) return new Point(x, y);
            return null;
        }

        int Cell { get { return GameData.CellPixels * zoom; } }

        void Relayout()
        {
            // The bag has a fixed size, but an item edited in by another tool can
            // sit outside it; the grid grows rather than hiding it.
            int cols = Columns, rows = Rows;
            foreach (ItemView v in items)
            {
                cols = Math.Max(cols, v.X + v.CellsWide);
                rows = Math.Max(rows, v.Y + v.CellsHigh);
            }
            Size = new Size(cols * Cell + 1, rows * Cell + 1);
            drawColumns = cols;
            drawRows = rows;
            Invalidate();
        }

        int drawColumns = MainColumns, drawRows = BagRows;

        // What is actually drawn. Equal to Columns/Rows unless an item sits
        // outside the grid and it had to grow to show it, so a test can tell a
        // stretched grid from a correct one.
        public int DrawnColumns { get { return drawColumns; } }
        public int DrawnRows { get { return drawRows; } }

        public ItemView HitTest(Point p)
        {
            Point c = CellAt(p);
            // Later items are drawn on top, so search backwards.
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Cells.Contains(c)) return items[i];
            return null;
        }

        public Point CellAt(Point p) { return new Point(p.X / Cell, p.Y / Cell); }

        // The item currently "in hand". It stays in its own bag until it is put
        // down, so nothing can be lost by closing the window mid-move.
        ItemView held;
        Point hoverCell = new Point(-1, -1);

        public ItemView Held
        {
            get { return held; }
            set { held = value; Invalidate(); }
        }

        // Items compatible with the held part. The grid receives this result from
        // the caller and does not inspect game records; zero means highlight none.
        Func<ItemView, bool> fits;

        public Func<ItemView, bool> Fits
        {
            get { return fits; }
            set { fits = value; Invalidate(); }
        }

        public Point HoverCell
        {
            get { return hoverCell; }
            set { hoverCell = value; Invalidate(); }
        }

        // Where an item of this size would land if dropped at the cursor: the
        // grab happens at the item's top-left corner, like the game does it.
        public Rectangle GhostArea
        {
            get
            {
                if (held == null || hoverCell.X < 0) return Rectangle.Empty;
                return new Rectangle(hoverCell.X, hoverCell.Y, held.CellsWide, held.CellsHigh);
            }
        }

        public void Select(ItemView v)
        {
            selected = v;
            Invalidate();
            OnSelectionChanged();
        }

        void OnSelectionChanged()
        {
            EventHandler h = SelectionChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (held != null)
            {
                Point c = CellAt(e.Location);
                if (c != hoverCell) { hoverCell = c; Invalidate(); }
                return;
            }
            ItemView v = HitTest(e.Location);
            if (v == hovered) return;
            hovered = v;
            Invalidate();
            RaiseHover(v, PointToScreen(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered == null) return;
            hovered = null;
            Invalidate();
            RaiseHover(null, Point.Empty);
        }

        void RaiseHover(ItemView v, Point screenPoint)
        {
            EventHandler<ItemHoverEventArgs> h = ItemHovered;
            if (h != null) h(this, new ItemHoverEventArgs(v, screenPoint));
        }

        // How many items sit under the same cell, so the tooltip can say so when
        // several have been stacked into one place.
        public int CountAt(Point cell)
        {
            int n = 0;
            foreach (ItemView v in items) if (v.Cells.Contains(cell)) n++;
            return n;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Select(HitTest(e.Location));
        }

        public static Color QualityColor(string classification)
        {
            switch (classification)
            {
                case "Legendary": return Color.FromArgb(150, 110, 60, 150);
                case "Epic": return Color.FromArgb(150, 45, 70, 150);
                case "Rare": return Color.FromArgb(150, 40, 110, 60);
                case "Magical": return Color.FromArgb(150, 120, 105, 40);
                case "Quest": return Color.FromArgb(150, 130, 90, 40);
                default: return Color.FromArgb(90, 70, 70, 70);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics, items, drawColumns, drawRows, zoom, BackColor,
                   held == null ? hovered : null, held == null ? selected : held, fits);
            if (held == null) return;

            Rectangle area = GhostArea;
            if (area == Rectangle.Empty) return;
            bool roomHere = IsFree(area, held);
            Rectangle px = new Rectangle(area.X * Cell, area.Y * Cell, area.Width * Cell, area.Height * Cell);
            using (SolidBrush fill = new SolidBrush(roomHere
                       ? Color.FromArgb(90, 120, 220, 120)
                       : Color.FromArgb(90, 220, 100, 100)))
                e.Graphics.FillRectangle(fill, px);
            if (held.Icon != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.DrawImage(held.Icon, px);
            }
            using (Pen p = new Pen(roomHere ? Color.LimeGreen : Color.OrangeRed, 2))
                e.Graphics.DrawRectangle(p, px.X + 1, px.Y + 1, px.Width - 2, px.Height - 2);
        }

        // Drawing lives in a static method so it can also render straight to a
        // bitmap without a window, which is how the self test checks it.
        public static void Render(Graphics g, List<ItemView> items, int columns, int rows,
                                  int zoom, Color background, ItemView hovered, ItemView selected)
        {
            Render(g, items, columns, rows, zoom, background, hovered, selected, null);
        }

        public static void Render(Graphics g, List<ItemView> items, int columns, int rows,
                                  int zoom, Color background, ItemView hovered, ItemView selected,
                                  Func<ItemView, bool> fits)
        {
            g.Clear(background);
            int cell = GameData.CellPixels * zoom;
            int Columns = columns, Rows = rows;

            using (Pen grid = new Pen(Color.FromArgb(52, 48, 44)))
            {
                for (int x = 0; x <= Columns; x++) g.DrawLine(grid, x * cell, 0, x * cell, Rows * cell);
                for (int y = 0; y <= Rows; y++) g.DrawLine(grid, 0, y * cell, Columns * cell, y * cell);
            }

            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (ItemView v in items)
            {
                Rectangle r = new Rectangle(v.X * cell, v.Y * cell, v.CellsWide * cell, v.CellsHigh * cell);
                using (SolidBrush b = new SolidBrush(QualityColor(v.Classification))) g.FillRectangle(b, r);
                if (v.Icon != null) g.DrawImage(v.Icon, r);
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(160, 200, 60, 60)))
                        g.FillRectangle(b, Rectangle.Inflate(r, -4, -4));
                }

                // Highlight compatible items and dim incompatible ones.
                if (fits != null)
                {
                    if (fits(v))
                    {
                        using (SolidBrush wash = new SolidBrush(Color.FromArgb(55, 120, 220, 120)))
                            g.FillRectangle(wash, r);
                        using (Pen p = new Pen(Color.FromArgb(235, 130, 235, 130), 2))
                            g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                    }
                    else
                    {
                        using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 16, 15, 14)))
                            g.FillRectangle(dim, r);
                    }
                }

                if (v.Stack > 1)
                {
                    string text = v.Stack.ToString();
                    using (Font f = new Font("Segoe UI", 7f * zoom, FontStyle.Bold))
                    {
                        SizeF size = g.MeasureString(text, f);
                        PointF at = new PointF(r.Right - size.Width - 1, r.Bottom - size.Height - 1);
                        g.DrawString(text, f, Brushes.Black, at.X + 1, at.Y + 1);
                        g.DrawString(text, f, Brushes.White, at.X, at.Y);
                    }
                }
            }

            if (hovered != null) Outline(g, hovered, Color.FromArgb(180, 220, 200, 120), cell);
            if (selected != null) Outline(g, selected, Color.FromArgb(255, 240, 190, 90), cell);
        }

        // Paints the grid, including the drop highlight, onto an arbitrary
        // surface — used by the self test, which has no window to paint into.
        public void DrawToBitmapUnbound(Graphics g, int offsetX, int offsetY)
        {
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.TranslateTransform(offsetX, offsetY);
            using (Bitmap surface = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
            {
                using (Graphics sg = Graphics.FromImage(surface))
                    OnPaint(new PaintEventArgs(sg, new Rectangle(0, 0, Width, Height)));
                g.DrawImageUnscaled(surface, 0, 0);
            }
            g.Restore(state);
        }

        public static Bitmap RenderToBitmap(List<ItemView> items, int zoom)
        {
            int columns = MainColumns, rows = BagRows;
            foreach (ItemView v in items)
            {
                columns = Math.Max(columns, v.X + v.CellsWide);
                rows = Math.Max(rows, v.Y + v.CellsHigh);
            }
            int cell = GameData.CellPixels * zoom;
            Bitmap bmp = new Bitmap(columns * cell + 1, rows * cell + 1);
            using (Graphics g = Graphics.FromImage(bmp))
                Render(g, items, columns, rows, zoom, Color.FromArgb(28, 26, 24), null, null);
            return bmp;
        }

        static void Outline(Graphics g, ItemView v, Color c, int cell)
        {
            Rectangle r = new Rectangle(v.X * cell, v.Y * cell, v.CellsWide * cell, v.CellsHigh * cell);
            using (Pen p = new Pen(c, 2)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        }
    }

    // Icon preview that scales by whole pixels only, so a 32x32 icon stays sharp
    // instead of being smeared by the default interpolation.
    public class IconBox : Control
    {
        Image image;

        public IconBox() { DoubleBuffered = true; BackColor = Color.FromArgb(28, 26, 24); }

        public Image Image
        {
            get { return image; }
            set { image = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (image == null) return;
            int scale = Math.Max(1, Math.Min((Width - 8) / image.Width, (Height - 8) / image.Height));
            int w = image.Width * scale, h = image.Height * scale;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, (Width - w) / 2, (Height - h) / 2, w, h);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }
}
