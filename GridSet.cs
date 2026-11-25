// A book of item grids behind a tab strip. The bags and the stash are the same
// thing with different sizes and different coordinate storage, so both are one
// of these and the code that moves items does not care which it is holding.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class GridSet
    {
        public readonly TabControl Tabs = new TabControl();
        public readonly List<InventoryGrid> Grids = new List<InventoryGrid>();
        public readonly Dictionary<InventoryGrid, List<ItemView>> Items =
            new Dictionary<InventoryGrid, List<ItemView>>();

        // The stash stores grid positions as floats and the bags as plain ints,
        // and its page sizes come from the save instead of being fixed.
        public readonly bool IsStash;
        public readonly string PageName;

        public GridSet(bool isStash, string pageName)
        {
            IsStash = isStash;
            PageName = pageName;
            Tabs.Dock = DockStyle.Fill;
            // Eight stash pages do not fit across a column ten cells wide, and
            // scroll arrows on a tab strip are miserable to use.
            Tabs.Multiline = isStash;
        }

        public int Count { get { return Grids.Count; } }

        public List<ItemView> ViewsOf(int index) { return Items[Grids[index]]; }

        public void Clear()
        {
            Tabs.TabPages.Clear();
            Grids.Clear();
            Items.Clear();
        }

        public InventoryGrid Add(List<ItemView> views, int columns, int rows, string title)
        {
            InventoryGrid grid = new InventoryGrid { Location = new Point(8, 8) };
            grid.SetGridSize(columns, rows);
            grid.SetItems(views);
            Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(20, 19, 18) };
            scroller.Controls.Add(grid);
            TabPage page = new TabPage(title);
            page.Controls.Add(scroller);
            Tabs.TabPages.Add(page);
            Grids.Add(grid);
            Items[grid] = views;
            return grid;
        }

        public void RetitlePages(Func<int, string> title)
        {
            for (int i = 0; i < Tabs.TabPages.Count && i < Grids.Count; i++)
                Tabs.TabPages[i].Text = title(i);
        }

        public int TabAt(Point clientPoint)
        {
            for (int i = 0; i < Tabs.TabCount; i++)
                if (Tabs.GetTabRect(i).Contains(clientPoint)) return i;
            return -1;
        }

        public void InvalidateAll() { foreach (InventoryGrid g in Grids) g.Invalidate(); }

        public void SetHeld(ItemView v) { foreach (InventoryGrid g in Grids) g.Held = v; }

        // Items to highlight while a part is held; null means none.
        public void SetFits(Func<ItemView, bool> f) { foreach (InventoryGrid g in Grids) g.Fits = f; }
    }
}
