// The worn-equipment block. Unlike a bag this is not a grid: the game gives each
// slot a fixed place, so the panel draws named frames and puts each item inside
// its own frame at native size.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class EquipmentSlot
    {
        // The slot caption is a key in `lang/<CODE>/equipment.txt`; resolve it
        // when displayed so language changes appear on the next repaint.
        public string LabelKey;
        public string Label { get { return Lang.T(LabelKey); } }
        public int Column, Row;
        public ItemView Item;
        public Rectangle Bounds;     // filled in while laying out
        public string Group;         // "", "weapon1", "weapon2"
        public int Index;            // index inside its list in the save
        public string[] Accepts;     // record classes this slot takes
        public bool IsWeapon { get { return Group != ""; } }

        public bool Takes(string itemClass)
        {
            if (Accepts == null || string.IsNullOrEmpty(itemClass)) return false;
            for (int i = 0; i < Accepts.Length; i++) if (Accepts[i] == itemClass) return true;
            return false;
        }
    }

    // Which record class goes in which slot. The save says nothing about this —
    // the item's own record does, in its class name (ArmorProtective_Head,
    // WeaponMelee_Sword2h and so on).
    public static class EquipRules
    {
        public static readonly string[] OneHanded = {
            "WeaponMelee_Axe", "WeaponMelee_Dagger", "WeaponMelee_Mace",
            "WeaponMelee_Scepter", "WeaponMelee_Sword", "WeaponHunting_Ranged1h" };

        public static readonly string[] TwoHanded = {
            "WeaponMelee_Axe2h", "WeaponMelee_Mace2h", "WeaponMelee_Sword2h",
            "WeaponMelee_Spear2h", "WeaponHunting_Ranged2h" };

        public static readonly string[] OffHand = { "WeaponArmor_Shield", "WeaponArmor_Offhand" };

        public static bool IsTwoHanded(string itemClass) { return Has(TwoHanded, itemClass); }

        static bool Has(string[] set, string value)
        {
            for (int i = 0; i < set.Length; i++) if (set[i] == value) return true;
            return false;
        }

        public static string[] Join(params string[][] sets)
        {
            List<string> all = new List<string>();
            foreach (string[] s in sets) all.AddRange(s);
            return all.ToArray();
        }
    }

    public class EquipmentClickEventArgs : EventArgs
    {
        public readonly EquipmentSlot Slot;
        public readonly MouseButtons Button;
        public EquipmentClickEventArgs(EquipmentSlot slot, MouseButtons button) { Slot = slot; Button = button; }
    }

    public class EquipmentPanel : Control
    {
        public const int BlockWidth = 70;
        public const int BlockHeight = 104;
        public const int Columns = 5;
        public const int Rows = 4;

        readonly List<EquipmentSlot> slots = new List<EquipmentSlot>();
        EquipmentSlot hovered;

        public event EventHandler<ItemHoverEventArgs> ItemHovered;
        public event EventHandler<EquipmentClickEventArgs> SlotClicked;

        // The item in hand, and the owner's rule for whether a given slot would
        // take it. The panel only draws the answer; the rules live in MainForm,
        // which is the only place that can see both weapon hands at once.
        ItemView held;
        public Func<EquipmentSlot, bool> CanAccept;

        public ItemView Held
        {
            get { return held; }
            set { held = value; Invalidate(); }
        }

        public List<EquipmentSlot> Slots { get { return slots; } }

        public EquipmentPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 22, 20);
            Size = new Size(Columns * BlockWidth + 8, Rows * BlockHeight + 8);
            BuildSlots();
        }

        void BuildSlots()
        {
            // Column/row positions chosen to read like the in-game paper doll:
            // weapon sets on the outside, armour down the middle.
            // Two weapon presets the character swaps between in game. The main
            // hand takes anything one- or two-handed; the off hand takes shields,
            // focus items and a second one-handed weapon for dual wielding.
            string[] mainHand = EquipRules.Join(EquipRules.OneHanded, EquipRules.TwoHanded);
            string[] offHand = EquipRules.Join(EquipRules.OffHand, EquipRules.OneHanded);

            Add("equipment.weapon1", 0, 0, "weapon1", 0, mainHand);
            Add("equipment.offhand1", 0, 1, "weapon1", 1, offHand);
            Add("equipment.weapon2", 4, 0, "weapon2", 0, mainHand);
            Add("equipment.offhand2", 4, 1, "weapon2", 1, offHand);

            Add("equipment.head", 1, 0, "", 0, new[] { "ArmorProtective_Head" });
            Add("equipment.neck", 2, 0, "", 1, new[] { "ArmorJewelry_Amulet" });
            Add("equipment.medal", 3, 0, "", 10, new[] { "ArmorJewelry_Medal" });
            Add("equipment.shoulders", 1, 1, "", 9, new[] { "ArmorProtective_Shoulders" });
            Add("equipment.chest", 2, 1, "", 2, new[] { "ArmorProtective_Chest" });
            Add("equipment.hands", 3, 1, "", 5, new[] { "ArmorProtective_Hands" });
            Add("equipment.waist", 1, 2, "", 8, new[] { "ArmorProtective_Waist" });
            Add("equipment.legs", 2, 2, "", 3, new[] { "ArmorProtective_Legs" });
            Add("equipment.ring1", 3, 2, "", 6, new[] { "ArmorJewelry_Ring" });
            Add("equipment.feet", 1, 3, "", 4, new[] { "ArmorProtective_Feet" });
            Add("equipment.relic", 2, 3, "", 11, new[] { "ItemArtifact" });
            Add("equipment.ring2", 3, 3, "", 7, new[] { "ArmorJewelry_Ring" });
        }

        void Add(string labelKey, int column, int row, string group, int index, string[] accepts)
        {
            EquipmentSlot s = new EquipmentSlot();
            s.LabelKey = labelKey; s.Column = column; s.Row = row; s.Group = group; s.Index = index;
            s.Accepts = accepts;
            s.Bounds = new Rectangle(4 + column * BlockWidth, 4 + row * BlockHeight, BlockWidth - 4, BlockHeight - 4);
            slots.Add(s);
        }

        public void SetCharacter(Func<string, int, ItemView> lookup)
        {
            foreach (EquipmentSlot s in slots) s.Item = lookup == null ? null : lookup(s.Group, s.Index);
            hovered = null;
            Invalidate();
        }

        public EquipmentSlot HitTest(Point p)
        {
            foreach (EquipmentSlot s in slots) if (s.Bounds.Contains(p)) return s;
            return null;
        }

        public EquipmentSlot Find(string group, int index)
        {
            foreach (EquipmentSlot s in slots) if (s.Group == group && s.Index == index) return s;
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            EventHandler<EquipmentClickEventArgs> h = SlotClicked;
            if (h != null) h(this, new EquipmentClickEventArgs(HitTest(e.Location), e.Button));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            EquipmentSlot s = HitTest(e.Location);
            if (s == hovered) return;
            hovered = s;
            Invalidate();
            if (held != null) return;   // no descriptions while an item is in hand
            EventHandler<ItemHoverEventArgs> h = ItemHovered;
            if (h != null) h(this, new ItemHoverEventArgs(s == null ? null : s.Item, PointToScreen(e.Location)));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = null;
            Invalidate();
            EventHandler<ItemHoverEventArgs> h = ItemHovered;
            if (h != null) h(this, new ItemHoverEventArgs(null, Point.Empty));
        }

        // A two-handed sword's icon is 64x128, taller than the frame. Slots are
        // not a grid, so the picture is scaled down to fit instead of spilling
        // over its neighbours; anything that already fits is drawn pixel exact.
        static void DrawFitted(Graphics g, Image icon, Rectangle box)
        {
            if (icon == null) return;
            int w = icon.Width, h = icon.Height;
            if (w > box.Width || h > box.Height)
            {
                double k = Math.Min((double)box.Width / w, (double)box.Height / h);
                w = Math.Max(1, (int)(w * k));
                h = Math.Max(1, (int)(h * k));
            }
            g.DrawImage(icon, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            using (Font label = new Font("Segoe UI", 7f))
            using (SolidBrush labelBrush = new SolidBrush(Color.FromArgb(120, 114, 104)))
            using (Pen frame = new Pen(Color.FromArgb(64, 58, 50)))
            using (SolidBrush empty = new SolidBrush(Color.FromArgb(34, 31, 28)))
            {
                foreach (EquipmentSlot s in slots)
                {
                    Rectangle box = s.Bounds;
                    Rectangle art = new Rectangle(box.X, box.Y, box.Width, box.Height - 13);
                    g.FillRectangle(empty, art);

                    if (s.Item != null)
                    {
                        using (SolidBrush tint = new SolidBrush(InventoryGrid.QualityColor(s.Item.Classification)))
                            g.FillRectangle(tint, art);
                        DrawFitted(g, s.Item.Icon, art);
                    }

                    // With an item in hand every slot says whether it would take
                    // it, so a two-hander with a shield already worn is refused
                    // before the click rather than after.
                    Pen outline = s == hovered ? Pens.Goldenrod : frame;
                    // The slot the item was taken from is left alone: it is not a
                    // destination, and clicking it just puts the item back.
                    if (held != null && s.Item != held)
                    {
                        bool ok = CanAccept != null && CanAccept(s);
                        using (SolidBrush wash = new SolidBrush(ok
                                   ? Color.FromArgb(70, 120, 220, 120)
                                   : Color.FromArgb(70, 220, 100, 100)))
                            g.FillRectangle(wash, art);
                        if (s == hovered)
                        {
                            if (ok) DrawFitted(g, held.Icon, art);
                            using (Pen p = new Pen(ok ? Color.LimeGreen : Color.OrangeRed, 2))
                                g.DrawRectangle(p, art.X + 1, art.Y + 1, art.Width - 2, art.Height - 2);
                            outline = null;
                        }
                    }

                    if (outline != null) g.DrawRectangle(outline, art);
                    g.DrawString(s.Label, label, labelBrush, box.X + 1, box.Bottom - 13);
                }
            }
        }
    }

    public class ItemHoverEventArgs : EventArgs
    {
        public readonly ItemView Item;
        public readonly Point ScreenPoint;
        public ItemHoverEventArgs(ItemView item, Point screenPoint) { Item = item; ScreenPoint = screenPoint; }
    }
}
