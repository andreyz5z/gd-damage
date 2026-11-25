// Browser over every item in the game, for putting a new one into a bag.
// Filters by category, by rarity and by a piece of the name.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class CatalogPanel : UserControl
    {
        public const int RowHeight = 36;

        ItemCatalog catalog;
        GameData game;
        readonly List<CatalogEntry> shown = new List<CatalogEntry>();

        readonly ComboBox categoryBox = new ComboBox();
        readonly TextBox searchBox = new TextBox();
        readonly TextBox bonusBox = new TextBox();
        readonly ListBox list = new ListBox();
        readonly Label countLabel = new Label();
        readonly Label bulkHint = new Label();
        readonly Dictionary<string, CheckBox> rarity = new Dictionary<string, CheckBox>();

        public event EventHandler<CatalogPickEventArgs> ItemPicked;
        public event EventHandler<CatalogHoverEventArgs> ItemHovered;

        // Rarities the game uses, in the order the filter shows them. Epic and
        // legendary are on by default: everything else together is thousands of
        // rows nobody wants to scroll through.
        static readonly string[] Rarities = { "Common", "Magical", "Rare", "Epic", "Legendary", "Quest" };
            // Caption keys come from `lang/<CODE>/catalog.txt`, in `Rarities` order.
        static readonly string[] RarityLabels = { "catalog.rarity.common", "catalog.rarity.magical", "catalog.rarity.rare",
                                                  "catalog.rarity.epic", "catalog.rarity.legendary", "catalog.rarity.quest" };
        static readonly bool[] RarityDefault = { false, false, false, true, true, false };

        public CatalogPanel()
        {
            BuildLayout();
            // List sections and search hints are not retained by `Lang.Bind`; rebuild
            // them when the language changes.
            Action relabel = delegate { ApplyTexts(); Refilter(); };
            Lang.Changed += relabel;
            Disposed += delegate { Lang.Changed -= relabel; };
        }

        void ApplyTexts()
        {
            // Filtering uses the item index, so the selected index remains stable.
            int keep = categoryBox.SelectedIndex;
            categoryBox.BeginUpdate();
            categoryBox.Items.Clear();
            categoryBox.Items.Add(Lang.T("catalog.wearable"));
            categoryBox.Items.Add(Lang.T("catalog.everything"));
            foreach (ItemCategory c in Enum.GetValues(typeof(ItemCategory)))
                categoryBox.Items.Add(ItemCatalog.CategoryName(c));
            categoryBox.EndUpdate();
            if (keep >= 0) categoryBox.SelectedIndex = keep;
            searchBox.PlaceholderText = Lang.T("catalog.search");
            bonusBox.PlaceholderText = Lang.T("catalog.bonus");
        }

        void BuildLayout()
        {
            Padding = new Padding(6);

            categoryBox.DropDownStyle = ComboBoxStyle.DropDownList;
            categoryBox.Dock = DockStyle.Top;
            ApplyTexts();
            // The default is the full catalog. Filtering uses the item index;
            // keep the category order unchanged.
            categoryBox.SelectedIndex = 1;
            categoryBox.SelectedIndexChanged += delegate { Refilter(); };

            searchBox.Dock = DockStyle.Top;
            searchBox.TextChanged += delegate { Refilter(); };

            // Use a separate box so name and statistic searches can run together.
            bonusBox.Dock = DockStyle.Top;
            bonusBox.TextChanged += delegate { Refilter(); };

            FlowLayoutPanel rarityFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0)
            };
            for (int i = 0; i < Rarities.Length; i++)
            {
                CheckBox b = new CheckBox
                {
                    Checked = RarityDefault[i],
                    AutoSize = true,
                    ForeColor = TipPalette.Rarity(Rarities[i])
                };
                Lang.Bind(b, RarityLabels[i]);
                b.CheckedChanged += delegate { Refilter(); };
                rarity[Rarities[i]] = b;
                rarityFlow.Controls.Add(b);
            }

            countLabel.Dock = DockStyle.Top;
            countLabel.Height = 18;
            countLabel.ForeColor = Color.DimGray;

            // Describe the combination on its own row. The catalog column is narrow,
            // so appending it to the count caused truncation.
            bulkHint.Dock = DockStyle.Top;
            bulkHint.Height = 16;
            bulkHint.ForeColor = Color.FromArgb(120, 150, 175);
            Lang.Bind(bulkHint, "catalog.bulkHint");

            list.Dock = DockStyle.Fill;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = RowHeight;
            list.IntegralHeight = false;
            list.BackColor = Color.FromArgb(26, 24, 22);
            list.ForeColor = Color.Gainsboro;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.DrawItem += DrawRow;
            list.MouseMove += ListMouseMove;
            list.MouseLeave += delegate { RaiseHover(null, Point.Empty); };
            list.MouseDown += ListMouseDown;

            Controls.Add(list);
            Controls.Add(bulkHint);
            Controls.Add(countLabel);
            Controls.Add(rarityFlow);
            Controls.Add(bonusBox);
            Controls.Add(searchBox);
            Controls.Add(categoryBox);
            Label title = new Label { Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            Lang.Bind(title, "catalog.title");
            Controls.Add(title);
        }

        public void Attach(ItemCatalog c, GameData g)
        {
            catalog = c;
            game = g;
            Refilter();
        }

        public bool HasCatalog { get { return catalog != null && catalog.Entries.Count > 0; } }

        void Refilter()
        {
            shown.Clear();
            // Items found by search but rejected by the two filters. Without this,
            // a zero count cannot distinguish a correct filter from an item that is
            // intentionally not searchable.
            int hidden = 0;
            if (catalog != null)
            {
                string needle = searchBox.Text.Trim();
                string bonus = bonusBox.Text.Trim();
                int mode = categoryBox.SelectedIndex;
                foreach (CatalogEntry e in catalog.Entries)
                {
                    if (needle.Length > 0 &&
                        e.Name.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                        e.Dbr.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (bonus.Length > 0 &&
                        e.Stats.IndexOf(bonus, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    if (!CategoryAllows(mode, e.Category)) { hidden++; continue; }
                    string cls = string.IsNullOrEmpty(e.Classification) ? "Common" : e.Classification;
                    CheckBox box;
                    if (!rarity.TryGetValue(cls, out box) || !box.Checked) { hidden++; continue; }
                    shown.Add(e);
                }
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (CatalogEntry e in shown) list.Items.Add(e);
            list.EndUpdate();

            countLabel.Text = Lang.T("catalog.count", shown.Count) +
                              (catalog == null ? "  " + Lang.T("catalog.notBuilt") : "") +
                              (hidden > 0 ? "  " + Lang.T("catalog.hidden", hidden) : "");
        }

        static bool CategoryAllows(int mode, ItemCategory c)
        {
            if (mode == 1) return true;
            if (mode == 0) return ItemCatalog.IsWearable(c);
            return (int)c == mode - 2;
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            CatalogEntry entry = shown[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;

            using (SolidBrush back = new SolidBrush(selected ? Color.FromArgb(56, 50, 42) : list.BackColor))
                g.FillRectangle(back, e.Bounds);

            Rectangle iconBox = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, 32, 32);
            try
            {
                Image icon = game == null ? null : game.Icon(entry.Bitmap);
                if (icon != null)
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    // Icons of tall items are shrunk to fit the row.
                    float scale = Math.Min(32f / icon.Width, 32f / icon.Height);
                    int w = Math.Max(1, (int)(icon.Width * scale)), h = Math.Max(1, (int)(icon.Height * scale));
                    g.DrawImage(icon, iconBox.X + (32 - w) / 2, iconBox.Y + (32 - h) / 2, w, h);
                }
            }
            catch (Exception)
            {
                // A row without its picture is far better than a crash inside the
                // list's paint handler.
            }

            Rectangle text = new Rectangle(e.Bounds.X + 38, e.Bounds.Y + 2, e.Bounds.Width - 42, e.Bounds.Height - 4);
            using (SolidBrush b = new SolidBrush(TipPalette.Rarity(entry.Classification)))
                g.DrawString(entry.Name, Font, b, text);
            if (entry.LevelRequirement > 0)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(130, 126, 118)))
                using (Font small = new Font(Font.FontFamily, 7f))
                    g.DrawString(Lang.T("catalog.level", entry.LevelRequirement), small, b,
                                 text.X, e.Bounds.Bottom - 14);
            }
        }

        int lastHover = -1;

        void ListMouseMove(object sender, MouseEventArgs e)
        {
            int index = list.IndexFromPoint(e.Location);
            if (index == lastHover) return;
            lastHover = index;
            if (index < 0 || index >= shown.Count) { RaiseHover(null, Point.Empty); return; }
            RaiseHover(shown[index], list.PointToScreen(e.Location));
        }

        void ListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = list.IndexFromPoint(e.Location);
            if (index < 0 || index >= shown.Count) return;
            EventHandler<CatalogPickEventArgs> h = ItemPicked;
            if (h != null) h(this, new CatalogPickEventArgs(shown[index]));
        }

        void RaiseHover(CatalogEntry entry, Point screenPoint)
        {
            EventHandler<CatalogHoverEventArgs> h = ItemHovered;
            if (h != null) h(this, new CatalogHoverEventArgs(entry, screenPoint));
        }
    }

    public class CatalogPickEventArgs : EventArgs
    {
        public readonly CatalogEntry Entry;
        public CatalogPickEventArgs(CatalogEntry e) { Entry = e; }
    }

    public class CatalogHoverEventArgs : EventArgs
    {
        public readonly CatalogEntry Entry;
        public readonly Point ScreenPoint;
        public CatalogHoverEventArgs(CatalogEntry e, Point p) { Entry = e; ScreenPoint = p; }
    }
}
