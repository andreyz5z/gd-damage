// Character viewer: pick a player.gdc, see the headline numbers, the worn
// equipment and the bags. The shared stash is a separate block in the save and
// is deliberately not shown.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public partial class MainForm : Form
    {
        Settings settings;
        GameData game;
        Character character;
        string savePath;

        // The top menu uses regular buttons rather than a `ToolStrip`; Settings is last.
        readonly FlowLayoutPanel toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(4),
        };
        // Use distinct normal, hover, and pressed colors for the dark theme.
        static readonly Color MenuFace = Color.FromArgb(0xBE, 0xE6, 0xFD);
        static readonly Color MenuHover = Color.FromArgb(0x7F, 0xC4, 0xF2);
        static readonly Color MenuPressed = Color.FromArgb(0x5F, 0xAF, 0xE8);
        static readonly Color MenuBorder = Color.FromArgb(0x3C, 0x7F, 0xB1);
        readonly Button openButton = MenuButton("main.menu.open");
        readonly Button skillsButton = MenuButton("main.menu.skills");
        readonly Button damageButton = MenuButton("main.menu.damage");
        readonly Button settingsButton = MenuButton("main.menu.settings");
        readonly Label pathLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(12, 10, 3, 3),
        };

        // THE LOOK OF THE TOP ROW'S BUTTONS, in one place. It is not only for this
        // row: the import button on the character panel wears it too, at the user's
        // It is a regular action button with the same visual weight as Open Save.
        // The size is left alone, because the two rows size themselves differently.
        public static void MenuLook(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = MenuFace;
            b.ForeColor = SystemColors.ControlText;
            b.FlatAppearance.BorderColor = MenuBorder;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = MenuHover;
            b.FlatAppearance.MouseDownBackColor = MenuPressed;
        }

        static Button MenuButton(string key)
        {
            // Use a fixed height and measure width from the localized label.
            Button b = new Button
            {
                AutoSize = false,
                Height = 26,
                Margin = new Padding(3),
            };
            MenuLook(b);
            b.TextChanged += delegate { b.Width = TextRenderer.MeasureText(b.Text, b.Font).Width + 24; };
            Lang.Bind(b, key);
            return b;
        }

        readonly CharacterPanel characterPanel = new CharacterPanel();
        readonly SaveManager saver = new SaveManager();
        readonly EquipmentPanel equipment = new EquipmentPanel();
        readonly DropZone trash = new DropZone();
        // Item editing belongs to the inventory panel because the action targets a
        // specific item. It is a toggle with a visible pressed state.
        readonly CheckBox editButton = new CheckBox
        {
            AutoSize = true,
            Appearance = Appearance.Button,
            Margin = new Padding(3, 6, 3, 6),
        };
        // Make the pressed state explicit so a click is not mistaken for moving an item.
        static readonly Color EditOn = Color.FromArgb(0x03, 0xA9, 0xF4);
        readonly FlowLayoutPanel editBar = new FlowLayoutPanel();

        // The two books of grids. They behave identically; only the page sizes
        // and the way coordinates are stored differ, and GridSet carries that.
        // The second value is an internal name and is not displayed.
        readonly GridSet bags = new GridSet(false, "bags");
        readonly GridSet stash = new GridSet(true, "stash");
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel("");
        readonly ItemTooltip tooltip = new ItemTooltip();

        readonly CatalogPanel catalogPanel = new CatalogPanel();
        ItemCatalog catalog;

        SplitContainer mainSplit, topSplit, outerSplit, stashSplit;
        readonly Label stashNote = new Label();

        // Set by the self test so no dialog can block a headless run.
        public static bool Headless;

        public MainForm()
        {
            Width = 1620;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            AppIcon.Apply(this);
            settings = Settings.Load(null);
            BuildLayout();
            ApplyTexts();
            Lang.Changed += ApplyTexts;
            FormClosed += delegate { Lang.Changed -= ApplyTexts; };
            OpenGameData();
        }

        // Bind labels whose text depends on form state; reapply them after a language change.
        void ApplyTexts()
        {
            // The build's version goes after the title, whatever the title says — see AppVersion,
            // where the number comes from and why it has no language key of its own.
            Text = AppVersion.Suffix(character == null
                                         ? Lang.T("main.title")
                                         : Lang.T("main.title.character", character.Name));
            editButton.Text = Lang.T(editMode ? "main.edit.armed" : "main.edit.button");
            trash.IdleText = Lang.T("main.trash.idle");
            trash.ArmedText = Lang.T("main.trash.armed");
            trash.Invalidate();
            equipment.Invalidate();      // Slot labels are rendered by the control.
            // Inventory, stash, and status text are rebuilt by `ShowCharacter`.
            if (character == null) stashNote.Text = Lang.T("main.stash.title");
        }

        // ------------------------------------------------------------- layout

        void BuildLayout()
        {
            openButton.Click += delegate { OpenSave(); };
            settingsButton.Click += delegate { EditSettings(); };
            skillsButton.Click += delegate { ShowSkills(); };
            damageButton.Click += delegate { ShowDamage(); };

            toolbar.Controls.Add(openButton);
            toolbar.Controls.Add(skillsButton);
            toolbar.Controls.Add(damageButton);
            toolbar.Controls.Add(settingsButton);
            toolbar.Controls.Add(pathLabel);

            characterPanel.Dock = DockStyle.Fill;
            // AN EDITED ATTRIBUTE COUNTS AT ONCE, NOT ONLY AFTER THE SAVE IS REOPENED.
            //
            // Level, physique, cunning, spirit and devotion all feed the requirement line of
            // every item (player level, missing points) and the damage multipliers
            // of granted skills, and all of that lives in `who`, which only `ReloadViews`
            // rebuilds. Through a timer rather than straight away: the panel raises `Changed` on
            // every click of a spinner, and a rebuild of every view costs about a millisecond
            // per item (368 of them in `_Acid Vines`) — a quarter-second of quiet is enough to
            // turn a held-down arrow into one recount.
            characterPanel.Changed += delegate { saver.RequestSave(); recount.Stop(); recount.Start(); };
            // An imported build rewrites the gear, the skills and the attributes at
            // once, so the window is rebuilt rather than merely marked dirty.
            characterPanel.Imported += delegate { AfterImport(); };
            recount.Tick += delegate { recount.Stop(); ReloadViews(); };
            Disposed += delegate { recount.Dispose(); };

            saver.Saved += delegate (object s, SaveEventArgs e)
            {
                if (e.Ok)
                    statusLabel.Text = e.BackupPath != null
                        ? Lang.T("main.status.savedBackup", DateTime.Now.ToString("HH:mm:ss"), Path.GetFileName(e.BackupPath))
                        : Lang.T("main.status.saved", DateTime.Now.ToString("HH:mm:ss"));
                else
                {
                    statusLabel.Text = Lang.T("main.status.saveError", e.Error);
                    MessageBox.Show(this, e.Error, Lang.T("main.saveError.title"),
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            equipment.Dock = DockStyle.Left;
            equipment.ItemHovered += delegate (object s, ItemHoverEventArgs e) { ShowTip(e); };
            equipment.SlotClicked += delegate (object s, EquipmentClickEventArgs e) { EquipmentClicked(e); };
            equipment.CanAccept = SlotWouldAccept;
            Panel equipmentHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 22, 20), AutoScroll = true };
            equipmentHost.Controls.Add(equipment);

            editButton.Click += delegate { ToggleEditMode(); };
            editBar.Dock = DockStyle.Top;
            // Keep the action button visually separate from the inventory tabs.
            editBar.Height = 48;
            editBar.Padding = new Padding(6, 2, 6, 14);
            editBar.Controls.Add(editButton);

            trash.Dock = DockStyle.Bottom;
            trash.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (editMode) { CancelEdit(); return; }
                if (e.Button == MouseButtons.Right) { CancelHold(); return; }
                DeleteHeld();
            };

            WireTabStrip(bags);
            WireTabStrip(stash);

            topSplit = new SplitContainer { Dock = DockStyle.Fill };
            topSplit.Panel1.Controls.Add(characterPanel);
            topSplit.Panel2.Controls.Add(equipmentHost);

            mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            mainSplit.Panel1.Controls.Add(topSplit);
            mainSplit.Panel2.Controls.Add(bags.Tabs);
            mainSplit.Panel2.Controls.Add(trash);
            mainSplit.Panel2.Controls.Add(editBar);

            stashNote.Dock = DockStyle.Top;
            stashNote.AutoSize = false;
            stashNote.Height = 34;
            stashNote.ForeColor = Color.FromArgb(150, 145, 135);
            stashNote.Padding = new Padding(6, 8, 6, 0);

            Panel stashHost = new Panel { Dock = DockStyle.Fill };
            stashHost.Controls.Add(stash.Tabs);
            stashHost.Controls.Add(stashNote);

            stashSplit = new SplitContainer { Dock = DockStyle.Fill };
            stashSplit.Panel1.Controls.Add(mainSplit);
            stashSplit.Panel2.Controls.Add(stashHost);

            catalogPanel.Dock = DockStyle.Fill;
            catalogPanel.ItemHovered += delegate (object s, CatalogHoverEventArgs e)
            {
                if (heldView != null) { tooltip.Hide(); return; }
                if (e.Entry == null) { tooltip.Hide(); return; }
                ItemView v = ViewForCatalog(e.Entry);
                if (v != null) tooltip.ShowFor(v, e.ScreenPoint);
            };
            catalogPanel.ItemPicked += delegate (object s, CatalogPickEventArgs e)
            {
                // A catalog entry is not an inventory instance and cannot be edited.
                if (editMode) { CancelEdit(); return; }
                TakeFromCatalog(e.Entry);
            };

            outerSplit = new SplitContainer { Dock = DockStyle.Fill };
            outerSplit.Panel1.Controls.Add(catalogPanel);
            outerSplit.Panel2.Controls.Add(stashSplit);

            status.Items.Add(statusLabel);

            Controls.Add(outerSplit);
            Controls.Add(toolbar);
            Controls.Add(status);

            Shown += delegate
            {
                ApplySplitters();
                // An owned window keeps its owner looking active, so the title
                // bar no longer greys out every time the description pops up.
                tooltip.Owner = this;
            };
        }

        // Splitter distances are only valid once the form has a real size, so
        // they are applied here rather than while the controls are being built.
        // Four columns have to fit across: catalogue, character fields, worn
        // equipment and the stash. Three of them have a width they cannot go
        // under, so the distances are worked out from the window rather than
        // hard-coded, and the character fields take what is left.
        const int CatalogWidth = 320;
        static int StashWidth { get { return 10 * GameData.CellPixels + 46; } }
        static int EquipmentWidth { get { return EquipmentPanel.Columns * EquipmentPanel.BlockWidth + 14; } }

        public void ApplySplitters()
        {
            int gutter = outerSplit.SplitterWidth + stashSplit.SplitterWidth + topSplit.SplitterWidth;
            int forCharacter = Math.Max(380, ClientSize.Width - CatalogWidth - StashWidth - EquipmentWidth - gutter);

            SetSplit(outerSplit, CatalogWidth);
            SetSplit(stashSplit, Math.Max(200, stashSplit.Width - StashWidth));
            SetSplit(mainSplit, EquipmentPanel.Rows * EquipmentPanel.BlockHeight + 14);
            SetSplit(topSplit, forCharacter);
        }

        // A tab header both switches pages on hover and accepts the item in hand
        // on click, the way the game behaves while dragging.
        void WireTabStrip(GridSet set)
        {
            set.Tabs.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (heldView == null) return;
                int index = set.TabAt(e.Location);
                if (index >= 0 && index != set.Tabs.SelectedIndex) set.Tabs.SelectedIndex = index;
            };
            set.Tabs.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (heldView == null) return;
                if (e.Button == MouseButtons.Right) { CancelHold(); return; }
                int index = set.TabAt(e.Location);
                if (index >= 0) DropOnTab(set, index);
            };
        }

        static void SetSplit(SplitContainer sc, int distance)
        {
            int span = sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height;
            int max = span - sc.SplitterWidth - 1;
            if (max <= 1) return;
            try { sc.SplitterDistance = Math.Max(1, Math.Min(distance, max)); }
            catch (InvalidOperationException) { }
        }

        // -------------------------------------------------------------- tooltip

        void ShowTip(ItemHoverEventArgs e)
        {
            if (e.Item == null) tooltip.Hide();
            else tooltip.ShowFor(e.Item, e.ScreenPoint);
        }

        // --------------------------------------------------------------- data

        void OpenGameData()
        {
            if (game != null) { game.Dispose(); game = null; }
            // Views built against the previous game data must not be reused.
            catalogViews.Clear();
            try
            {
                if (!settings.GameLooksValid)
                {
                    statusLabel.Text = Lang.T("main.status.noGame");
                    AskForGameFolder();
                    return;
                }
                Cursor = Cursors.WaitCursor;
                game = new GameData(settings.GamePath, settings.Language ?? Settings.DefaultLanguage);
                statusLabel.Text = Lang.T("main.status.gameBase", settings.GamePath, settings.Language ?? Settings.DefaultLanguage);
                OpenCatalog(false);
            }
            catch (Exception e)
            {
                statusLabel.Text = Lang.T("main.status.gameError", e.Message);
            }
            finally { Cursor = Cursors.Default; }
        }

        // The game is not always installed through Steam, and the automatic
        // search can only look where games usually are. When it comes up empty
        // there is nothing the program can do until someone says where the game
        // is, so it asks instead of leaving a line in the status bar that is easy
        // to miss. Asked once per start: saying no must not trap the window in a
        // dialog it reopens forever.
        bool askedForGameFolder, waitingToAsk;

        void AskForGameFolder()
        {
            if (Headless || askedForGameFolder) return;
            // The first call comes from the constructor, before there is a window
            // to own the dialog; it is deferred to Shown, and only once.
            if (!IsHandleCreated)
            {
                if (waitingToAsk) return;
                waitingToAsk = true;
                Shown += delegate { AskForGameFolder(); };
                return;
            }
            askedForGameFolder = true;

            MessageBox.Show(this, Lang.T("main.askGame.text"), Lang.T("main.askGame.title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            EditSettings();
        }

        void ShowSkills()
        {
            if (character == null || game == null)
            {
                statusLabel.Text = Lang.T("main.status.openFirst");
                return;
            }
            using (SkillsForm dlg = new SkillsForm(game, character))
            {
                // The riftgate and faction tabs write into the character, and the
                // save is written the same way every other edit in this window is.
                dlg.Changed += delegate { saver.RequestSave(); };
                dlg.ShowDialog(this);
            }
            // Rebuild the main view after the mastery reset; the points remain in
            // the character and save, but the old view is otherwise stale.
            // Rebuild the main view so skill and devotion points match the updated
            // character state after a mastery reset.
            ShowCharacter();
        }

        void ShowDamage()
        {
            if (character == null || game == null)
            {
                statusLabel.Text = Lang.T("main.status.openFirst");
                return;
            }
            using (DamageForm dlg = new DamageForm(game, character)) dlg.ShowDialog(this);
        }

        void EditSettings()
        {
            using (SettingsForm dlg = new SettingsForm(settings, delegate { OpenCatalog(true); }))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                settings.Save();
                // Interface and game-name language use the same setting. Rebind labels
                // and rebuild the character view; newly opened windows use the new language.
                if (!string.Equals(Lang.Current, settings.Language ?? Settings.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                    Lang.Load(settings.Language);
                OpenGameData();
                if (character != null) ShowCharacter();
            }
        }

        // The catalogue is cached on disk because a full scan of the databases
        // takes a couple of seconds. The cache records which game build it came
        // from, so a patched game is noticed and a rebuild is offered.
        public void OpenCatalog(bool force)
        {
            if (game == null) return;
            catalog = force ? null : ItemCatalog.Load(ItemCatalog.CachePath);
            bool stale = catalog != null && !catalog.MatchesGame(settings.GamePath, settings.Language ?? Settings.DefaultLanguage);

            if (catalog == null || stale)
            {
                string question = Lang.T(stale ? "main.catalog.stale" : "main.catalog.missing");
                if (!force && !Headless && MessageBox.Show(this, question, Lang.T("main.catalog.title"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    // Even when the user declines, the panel must be pointed at the
                    // current game data — otherwise it keeps using the old one.
                    catalogPanel.Attach(catalog, game);
                    return;
                }
                RebuildCatalog();
                return;
            }
            catalogPanel.Attach(catalog, game);
            statusLabel.Text = Lang.T("main.status.catalog", catalog.Entries.Count);
        }

        void RebuildCatalog()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                statusLabel.Text = Lang.T("main.status.catalogBuilding");
                Application.DoEvents();
                catalog = ItemCatalog.Build(settings.GamePath, settings.Language ?? Settings.DefaultLanguage, null);
                catalog.Save(ItemCatalog.CachePath);
                catalogPanel.Attach(catalog, game);
                statusLabel.Text = Lang.T("main.status.catalogBuilt", catalog.Entries.Count);
            }
            catch (Exception e)
            {
                statusLabel.Text = Lang.T("main.status.catalogError", e.Message);
                MessageBox.Show(this, e.Message, Lang.T("main.catalog.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        readonly Dictionary<string, ItemView> catalogViews = new Dictionary<string, ItemView>(StringComparer.OrdinalIgnoreCase);

        ItemView ViewForCatalog(CatalogEntry entry)
        {
            if (game == null) return null;
            ItemView cached;
            if (catalogViews.TryGetValue(entry.Dbr, out cached)) return cached;
            Item probe = Item.Create(entry.Dbr, character != null ? character.ItemTailWords : 6);
            // A catalog entry is not an item instance and has no roll to apply.
            ItemView v = ItemViewFactory.Build(game, probe, settings.ShowRawFields, character == null ? null : who, false);
            catalogViews[entry.Dbr] = v;
            return v;
        }

        void OpenSave()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("main.open.title");
                dlg.Filter = Lang.T("main.open.filter");
                dlg.InitialDirectory = savePath != null ? Path.GetDirectoryName(savePath) : settings.SavesPath;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                LoadSave(dlg.FileName);
            }
        }

        public void LoadSave(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                saver.Flush();
                character = Character.Load(path);
                savePath = path;
                saver.Attach(character, path);
                settings.LastSave = path;
                settings.Save();
                pathLabel.Text = path;
                Text = Lang.T("main.title.character", character.Name);
                ShowCharacter();
                // A character with no recorded game entry may have no inventory or
                // equipment blocks; show that state explicitly instead of treating it
                // as an editor failure.
                if (character.InventoryHasData == 0)
                    MessageBox.Show(this, Lang.T("main.noInventory.text"), Lang.T("main.noInventory.title"),
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, Lang.T("main.loadError.title"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        string ClassName()
        {
            if (game == null) return character.ClassTag ?? "";
            string gender = character.Sex != 0 ? "ms" : "fs";
            // Class names are gendered like item nouns; the save records the sex.
            string raw = game.Tag(character.ClassTag);
            string shown = string.IsNullOrEmpty(raw) ? "" : GameData.Form(raw, gender).Trim();
            // If a language package has no usable class name, use the game's English
            // name instead of exposing the tag key.
            if (shown.Length == 0)
            {
                string en = game.EnglishTag(character.ClassTag);
                shown = string.IsNullOrEmpty(en) ? "" : GameData.Form(en, gender).Trim();
            }
            return shown.Length > 0 ? shown : character.ClassTag ?? "";
        }

        void ShowCharacter()
        {
            // The grids and the equipment views are about to be rebuilt, so
            // anything still in hand would be pointing at objects that no longer
            // belong to any panel.
            ClearHold();
            // Load game data so skill tooltips use the same character modifiers as the game.
            who = ItemViewFactory.CharacterContext.Of(game, character);
            // Catalogue previews are cached per record and carry the mastery
            // notes, so they belong to the character that was open.
            catalogViews.Clear();
            string summary = Lang.T("main.summary", character.Sacks.Count, character.TotalInventoryItems,
                                    character.StashTabs.Count, character.TotalStashItems);
            // Report an uninitialized inventory directly instead of showing empty bags
            // as an editor failure.
            if (character.InventoryHasData == 0)
                summary = Lang.T("main.summary.noInventory", character.StashTabs.Count, character.TotalStashItems);
            characterPanel.Show(character, Path.GetFileName(savePath), ClassName(), summary);
            // What the gear and the skills add, so the panel can say what an
            // attribute is right now and not only what points were spent on it.
            if (game != null) characterPanel.SetBonuses(CharacterTotals.Of(game, character), game);
            BuildEquipment();
            BuildTabs();
        }

        ItemView MakeView(Item it)
        {
            if (it == null || string.IsNullOrEmpty(it.BaseName)) return null;
            return ItemViewFactory.Build(game, it, settings.ShowRawFields, who);
        }

        // The character's masteries, so item bonuses aimed at a mastery they
        // never took can be marked. Recomputed when a save is opened — and after every edit,
        // through `ReloadViews`.
        ItemViewFactory.CharacterContext who = new ItemViewFactory.CharacterContext();

        // The quiet quarter-second after an attribute edit, see the `Changed` handler above.
        readonly Timer recount = new Timer { Interval = 250 };

        void BuildEquipment()
        {
            equipment.SetCharacter(delegate (string group, int index)
            {
                List<Item> source =
                    group == "weapon1" ? character.Weapons1 :
                    group == "weapon2" ? character.Weapons2 : character.Equipment;
                if (index < 0 || index >= source.Count) return null;
                return MakeView(source[index]);
            });
        }

        void BuildTabs()
        {
            BuildBook(bags);
            BuildBook(stash);
            int items = 0;
            for (int i = 0; i < character.StashTabs.Count; i++) items += character.StashTabs[i].Items.Count;
            stashNote.Text = character.StashTabs.Count == 0
                ? Lang.T("main.stash.none")
                : Lang.T("main.stash.count", character.StashTabs.Count, items);
        }

        void BuildBook(GridSet set)
        {
            set.Clear();
            int pages = set.IsStash ? character.StashTabs.Count : character.Sacks.Count;
            for (int i = 0; i < pages; i++)
            {
                List<ItemView> views = new List<ItemView>();
                foreach (Item it in ItemsOf(set, i))
                {
                    ItemView v = MakeView(it);
                    if (v == null) continue;
                    // Stash coordinates are floats where the bags store plain
                    // ints, and the view is built from the raw word.
                    if (set.IsStash) { v.X = (int)it.XFloat; v.Y = (int)it.YFloat; }
                    views.Add(v);
                }
                Size size = PageSize(set, i);
                InventoryGrid grid = set.Add(views, size.Width, size.Height, "");
                grid.ItemHovered += delegate (object s, ItemHoverEventArgs e) { if (heldView == null) ShowTip(e); };
                GridSet capturedSet = set;
                int captured = i;
                grid.MouseDown += delegate (object s, MouseEventArgs e) { GridClicked(capturedSet, captured, grid, e); };
            }
            RetitleBook(set);
            if (set.Tabs.TabPages.Count > 0) set.Tabs.SelectedIndex = 0;
        }

        // Bag sizes are fixed by the game and absent from the save; stash page
        // sizes are recorded per page, and are 10x19 in current saves.
        Size PageSize(GridSet set, int page)
        {
            if (!set.IsStash) return new Size(InventoryGrid.ColumnsFor(page), InventoryGrid.BagRows);
            StashTab tab = character.StashTabs[page];
            return new Size(Math.Max(1, tab.Width), Math.Max(1, tab.Height));
        }

        List<Item> ItemsOf(GridSet set, int page)
        {
            return set.IsStash ? character.StashTabs[page].Items : character.Sacks[page].Items;
        }

        void RetitleBook(GridSet set)
        {
            GridSet captured = set;
            set.RetitlePages(delegate (int i)
            {
                string name = captured.IsStash
                    ? (i + 1).ToString()
                    : (i == 0 ? Lang.T("main.bags.main") : Lang.T("main.bags.extra", i));
                return name + " (" + captured.ViewsOf(i).Count + ")";
            });
        }

        public void SelectTab(int index)
        {
            if (index >= 0 && index < bags.Tabs.TabPages.Count) bags.Tabs.SelectedIndex = index;
        }

        // ------------------------------------------------------ moving items

        // Where the item in hand came from, so putting it down knows what to
        // clear. It stays in its old place until it lands somewhere, which is why
        // closing the window mid-move cannot lose it.
        enum HoldFrom { Catalog, Grid, Equipment }

        ItemView heldView;
        HoldFrom heldFrom;
        GridSet heldSet;       // which book, when the item came out of a grid
        int heldPage = -1;
        EquipmentSlot heldSlot;

        // A brand new item: the game rolls its numbers from the seed the first
        // time it loads the character, so a random one is what makes each copy
        // different, exactly as a dropped item would be.
        // `bulk` requests a stack; it is explicit so headless tests can exercise the
        // same behavior without relying on keyboard state.
        void TakeFromCatalog(CatalogEntry entry) { TakeFromCatalog(entry, (ModifierKeys & Keys.Control) != 0); }

        void TakeFromCatalog(CatalogEntry entry, bool bulk)
        {
            if (character == null)
            {
                statusLabel.Text = Lang.T("main.status.openFirst");
                return;
            }
            Item item = Item.Create(entry.Dbr, character.ItemTailWords);
            byte[] rnd = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Create().GetBytes(rnd);
            item.Seed = BitConverter.ToUInt32(rnd, 0) | 1u;

            ItemView view = ItemViewFactory.Build(game, item, settings.ShowRawFields);
            if (!view.SizeKnown)
            {
                statusLabel.Text = Lang.T("main.status.noIcon");
                return;
            }
            // Ctrl takes a stack. Use the limit from Stacking when available; use
            // the configured fallback for items without a declared limit.
            string bulkNote = "";
            DbRecord rec = game == null ? null : game.Record(entry.Dbr);
            if (bulk && Stacking.Stacks(game, rec))
            {
                int take = Stacking.Bulk(game, rec);
                item.StackCount = (uint)take;
                view = ItemViewFactory.Build(game, item, settings.ShowRawFields);
                bulkNote = "  " + Lang.T(Stacking.LimitOf(game, rec) == Stacking.NoLimit
                                             ? "main.status.bulkNoLimit" : "main.status.bulkLimit", take);
            }

            Hold(view, HoldFrom.Catalog, null, -1, null);
            statusLabel.Text = Lang.T("main.status.newInHand", view.Name, view.CellsWide, view.CellsHigh) + bulkNote;
        }

        // ============================ ITEM EDITING =============================
        //
        // The inventory button arms edit mode; the next item click opens the seed
        // editor. The mode is one-shot so subsequent clicks move items normally.
        bool editMode;

        void ToggleEditMode()
        {
            if (character == null)
            {
                statusLabel.Text = Lang.T("main.status.openFirst");
                ArmEdit(false);
                return;
            }
            ArmEdit(!editMode);
            statusLabel.Text = editMode ? Lang.T("main.status.editArmed") : "";
        }

        // Toggle edit mode and update both its color and label.
        void ArmEdit(bool on)
        {
            editMode = on;
            editButton.Checked = on;
            editButton.Text = Lang.T(on ? "main.edit.armed" : "main.edit.button");
            editButton.BackColor = on ? EditOn : Color.Empty;
            editButton.UseVisualStyleBackColor = !on;
        }

        // A click outside an item cancels the armed mode.
        void CancelEdit()
        {
            if (!editMode) return;
            ArmEdit(false);
            statusLabel.Text = Lang.T("main.status.editCancelled");
        }

        void EditItem(ItemView view)
        {
            ArmEdit(false);
            if (view == null || view.Source == null) return;
            if (!view.HasSeed || game == null)
            {
                statusLabel.Text = Lang.T("main.status.editNoSeed");
                return;
            }
            using (ItemEditForm f = new ItemEditForm(game, view.Source, who))
            {
                f.ShowDialog(this);
                if (!f.Changed) { statusLabel.Text = Lang.T("main.status.editUnchanged"); return; }
            }
            // Rebuild the tooltip because its values come from the updated seed.
            ReloadViews();
            saver.RequestSave();
            statusLabel.Text = Lang.T("main.status.editDone");
        }

        // After an edit refresh inventory and equipment views plus CharacterTotals.
        // Reloading the entire save would reset the selected inventory tab.
        // AND IT STARTS WITH THE CHARACTER CONTEXT, NOT WITH THE TOOLTIPS.
        //
        // `who` holds what the whole window reads about the character: the worn records (a set
        // counts its pieces by them), `Have` — level and attributes, against which every item's
        // requirement line is written, the masteries, and the damage multipliers with which a
        // granted skill's numbers are printed. It is built once, in
        // `ShowCharacter`, and nothing rebuilt it afterwards — so every edit was drawn against
        // the character as they were when the save was opened.
        //
        // Recalculate totals after adding or replacing gear so set bonuses and
        // character attributes reflect the current equipment.
        //
        // Both come from the same stale `who`: the set counted `WornRecords` of the empty
        // character, and the requirement line compared against `Have` of the level-1 one.
        // The catalogue previews are cached per record and carry the same marks, so they go too.
        // `--norecount` — the old behaviour on purpose, to see the check redden (PLAN, rule 12).
        public static bool NoRecount;

        // Everything an imported build touched, shown again: the headline numbers,
        // the worn gear and the tabs, and the save marked for writing.
        void AfterImport()
        {
            if (character == null) return;
            string summary = Lang.T("main.summary", character.Sacks.Count, character.TotalInventoryItems,
                                    character.StashTabs.Count, character.TotalStashItems);
            characterPanel.Show(character, Path.GetFileName(savePath), ClassName(), summary);
            ReloadViews();
            BuildTabs();
            saver.RequestSave();
        }

        void ReloadViews()
        {
            if (character == null) return;
            if (!NoRecount)
            {
                who = ItemViewFactory.CharacterContext.Of(game, character);
                catalogViews.Clear();
            }
            if (game != null) characterPanel.SetBonuses(CharacterTotals.Of(game, character), game);
            BuildEquipment();
            ReloadGridViews();
        }

        // Rebuild item descriptions in place without reloading the save or losing
        // the selected tab and scroll position.
        void ReloadGridViews()
        {
            foreach (GridSet set in new GridSet[] { bags, stash })
                for (int p = 0; p < set.Count; p++)
                {
                    List<ItemView> views = set.ViewsOf(p);
                    for (int i = 0; i < views.Count; i++)
                    {
                        // Preserve the character context so mastery-dependent notes
                        // remain identical before and after the edit.
                        ItemView made = MakeView(views[i].Source);
                        if (made == null) continue;
                        made.X = views[i].X; made.Y = views[i].Y;
                        views[i] = made;
                    }
                    set.Grids[p].SetItems(views);
                }
            equipment.Invalidate();
            characterPanel.Invalidate();
            bags.InvalidateAll();
            stash.InvalidateAll();
        }

        void Hold(ItemView view, HoldFrom from, GridSet set, int page, EquipmentSlot slot)
        {
            heldView = view;
            heldFrom = from;
            heldSet = set;
            heldPage = page;
            heldSlot = slot;
            bags.SetHeld(view);
            stash.SetHeld(view);
            equipment.Held = view;
            trash.Armed = view != null;
            // Highlight valid targets for a held component. Ordinary items have no
            // insertion targets and should not dim the inventory.
            bool part = view != null && ItemParts.KindOf(view.ItemClass) != ItemParts.Part.None;
            bags.SetFits(part ? (Func<ItemView, bool>)PartGoesIntoView : null);
            stash.SetFits(part ? (Func<ItemView, bool>)PartGoesIntoView : null);
            tooltip.Hide();
        }

        void GridClicked(GridSet set, int page, InventoryGrid grid, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { CancelHold(); return; }
            if (e.Button != MouseButtons.Left) return;

            // In edit mode, clicking an item opens the seed editor. Clear the mode
            // after opening so the next click moves items normally.
            if (editMode)
            {
                ItemView pick = grid.HitTest(e.Location);
                if (pick != null) EditItem(pick); else CancelEdit();
                return;
            }

            if (heldView == null)
            {
                ItemView pick = grid.HitTest(e.Location);
                if (pick == null) return;
                if (!pick.SizeKnown)
                {
                    statusLabel.Text = Lang.T("main.status.sizeUnknown");
                    return;
                }
                Hold(pick, HoldFrom.Grid, set, page, null);
                UpdateHoldStatus();
                return;
            }

            // A stack can be added to an occupied cell when the items are compatible.
            ItemView under = grid.HitTest(e.Location);
            if (under != null && under != heldView &&
                Stacking.SameKind(heldView.Source, under.Source))
            { MergeHeld(set, page, under); return; }

            // A held component can be inserted into a compatible item below it.
            // `ItemParts`).
            if (under != null && under != heldView &&
                ItemParts.KindOf(heldView.ItemClass) != ItemParts.Part.None)
            { AttachHeldTo(under.Source, under.Name); return; }

            Point cell = grid.CellAt(e.Location);
            Rectangle area = new Rectangle(cell.X, cell.Y, heldView.CellsWide, heldView.CellsHigh);
            if (!grid.IsFree(area, heldView))
            {
                statusLabel.Text = Lang.T("main.status.noRoom", heldView.CellsWide, heldView.CellsHigh);
                return;
            }
            PlaceHeld(set, page, cell);
        }

        // Add to a stack up to the limit returned by Stacking; any remainder stays
        // in hand. Items without a declared limit accept the full remainder.
        void MergeHeld(GridSet target, int page, ItemView under)
        {
            Item to = under.Source, from = heldView.Source;
            DbRecord rec = game == null ? null : game.Record(to.BaseName);
            if (!Stacking.Stacks(game, rec))
            {
                statusLabel.Text = Lang.T("main.status.noStacking", under.Name);
                return;
            }
            long room = Stacking.Room(game, rec, to);
            if (room <= 0)
            {
                int limit = Stacking.LimitOf(game, rec);
                statusLabel.Text = limit == Stacking.NoLimit
                    ? Lang.T("main.status.stackFull", to.StackCount)
                    : Lang.T("main.status.stackFullOf", to.StackCount, limit);
                return;
            }

            uint take = (uint)Math.Min(room, from.StackCount);
            to.StackCount += take;
            from.StackCount -= take;

            bool emptied = from.StackCount == 0;
            if (emptied) { DetachHeld(); ClearHold(); }

            // Refresh both stack descriptions because the count is shown in the icon
            // and the first tooltip row.
            ItemView again = ReplaceView(target, page, under);
            if (!emptied)
            {
                // Refresh the held remainder as well.
                if (heldFrom == HoldFrom.Grid && heldSet != null)
                {
                    ItemView fresh = ReplaceView(heldSet, heldPage, heldView);
                    if (fresh != null) Hold(fresh, HoldFrom.Grid, heldSet, heldPage, null);
                }
                else heldView.Stack = from.StackCount;
            }
            bags.InvalidateAll();
            stash.InvalidateAll();
            saver.RequestSave();
            statusLabel.Text = emptied
                ? Lang.T("main.status.merged", take, to.StackCount)
                : Lang.T("main.status.mergedRest", take, to.StackCount, from.StackCount);
        }

        // Rebuild one item view in place. Insert a held component into the item under
        // the cursor, whether in inventory, stash, or equipment.
        //
        // Read insertion flags from the component record (`ring`, `offhand`, `sword2h`)
        // through `ItemParts`.
        //
        // Remove exactly one component from a stack; the remainder stays in hand.
        //
        // Inserted components use seed zero, as with imported gear; their range values
        // are derived from that seed.
        void AttachHeldTo(Item target, string targetName)
        {
            if (target == null || heldView == null || game == null) return;
            ItemView part = heldView;
            DbRecord rec = game.Record(part.Source.BaseName);
            ItemParts.Part kind = ItemParts.KindOf(rec == null ? "" : rec.Class);
            if (kind == ItemParts.Part.None) return;

            DbRecord targetRec = game.Record(target.BaseName);
            string targetClass = targetRec == null ? "" : targetRec.Class;
            if (!ItemParts.Fits(rec, targetClass))
            {
                // Do not expose item class and record flags in the user-facing message.
                statusLabel.Text = Lang.T("main.status.partWrongItem", part.Name, targetName);
                return;
            }

            string had = kind == ItemParts.Part.Component ? target.RelicName : target.AugmentName;
            string hadName = PartName(had);
            string partName = part.Name;
            Item partItem = part.Source;

            // Apply item changes first, rebuild all views, then refresh the held view;
            // otherwise it could reference an object removed from the grid.
            HoldFrom from = heldFrom;
            GridSet handSet = heldSet;
            int handPage = heldPage;
            bool lastOne = partItem.StackCount <= 1;

            if (lastOne)
            {
                if (from != HoldFrom.Catalog) DetachHeld();
                ClearHold();
            }
            else partItem.StackCount -= 1;   // Remove exactly one item from the stack.

            if (kind == ItemParts.Part.Component) { target.RelicName = partItem.BaseName; target.RelicSeed = 0; }
            else { target.AugmentName = partItem.BaseName; target.AugmentSeed = 0; }

            // Refresh both the item tooltip and character totals when the item is equipped.
            ReloadViews();

            if (!lastOne)
            {
                ItemView fresh = from == HoldFrom.Grid && handSet != null
                               ? FindView(handSet, handPage, partItem)
                               : MakeView(partItem);
                if (fresh != null) Hold(fresh, from, handSet, handPage, null);
                UpdateHoldStatus();
            }

            saver.RequestSave();
            statusLabel.Text = hadName == null
                ? Lang.T("main.status.partAttached", partName, targetName)
                : Lang.T("main.status.partReplaced", partName, targetName, hadName);
        }

        // Fresh view of the same item after rebuilding descriptions.
        ItemView FindView(GridSet set, int page, Item source)
        {
            if (set == null || page < 0 || page >= set.Count) return null;
            foreach (ItemView v in set.ViewsOf(page)) if (v.Source == source) return v;
            return null;
        }

        // Name of the component already installed in the item; components use
        // `description` rather than the normal item-name tag.
        string PartName(string dbr)
        {
            if (string.IsNullOrEmpty(dbr)) return null;
            DbRecord rec = game == null ? null : game.Record(dbr);
            if (rec == null) return dbr;
            string said = game.Tag(rec.Get("itemNameTag"));
            if (string.IsNullOrEmpty(said)) said = game.Tag(rec.Get("description"));
            return string.IsNullOrEmpty(said) ? dbr : said;
        }

        ItemView ReplaceView(GridSet set, int page, ItemView old)
        {
            if (set == null || page < 0 || page >= set.Count || old == null) return null;
            List<ItemView> views = set.ViewsOf(page);
            int at = views.IndexOf(old);
            if (at < 0) return null;
            ItemView made = MakeView(old.Source);
            if (made == null) return null;
            made.X = old.X; made.Y = old.Y;
            views[at] = made;
            set.Grids[page].SetItems(views);
            return made;
        }

        void PlaceHeld(GridSet target, int page, Point cell)
        {
            ItemView view = heldView;
            Item item = view.Source;
            HoldFrom from = heldFrom;
            bool samePage = from == HoldFrom.Grid && heldSet == target && heldPage == page;

            // Where it lands has to be known before it joins the target grid: a
            // grid sizes itself around its items, so a position left over from
            // the page the item came from stretches it. A tall stash page turned
            // an eight-row bag into a nineteen-row one that way.
            item.Attached = 0;
            SetCell(item, cell, target.IsStash);
            view.X = cell.X;
            view.Y = cell.Y;

            if (!samePage)
            {
                DetachHeld();
                ItemsOf(target, page).Add(item);
                target.ViewsOf(page).Add(view);
                RetitleBook(target);
            }
            // Always, not only when the page changed: this is what recomputes the
            // drawn size, so a grid stretched earlier settles back.
            target.Grids[page].SetItems(target.ViewsOf(page));

            ClearHold();
            bags.InvalidateAll();
            stash.InvalidateAll();
            // TAKING A WORN ITEM OFF IS A CHANGE OF THE WORN SET TOO — and it is the half the
            // user hits first: strip the character, and the set bonus must go out. A move from
            // bag to bag changes nothing about the character, and a full rebuild of every view
            // (about a millisecond each, 368 items in `_Acid Vines`) is not paid for it.
            if (from == HoldFrom.Equipment) ReloadViews();
            saver.RequestSave();
            // Keep localization keys literal so `--langtest` can find them.
            string done = from == HoldFrom.Catalog
                            ? (target.IsStash ? "main.status.addedStash" : "main.status.addedBag")
                        : from == HoldFrom.Equipment
                            ? (target.IsStash ? "main.status.unequippedStash" : "main.status.unequippedBag")
                            : (target.IsStash ? "main.status.movedStash" : "main.status.movedBag");
            statusLabel.Text = Lang.T(done, view.Name);
        }

        // The stash keeps grid positions as floats, the bags as plain ints. Same
        // four bytes either way, so only the interpretation differs.
        static void SetCell(Item item, Point cell, bool asFloat)
        {
            if (asFloat)
            {
                item.XRaw = BitConverter.ToUInt32(BitConverter.GetBytes((float)cell.X), 0);
                item.YRaw = BitConverter.ToUInt32(BitConverter.GetBytes((float)cell.Y), 0);
            }
            else
            {
                item.XRaw = (uint)cell.X;
                item.YRaw = (uint)cell.Y;
            }
        }

        // Takes the held item out of wherever it still sits. Called only once the
        // destination has been checked, so a refused move leaves everything alone.
        void DetachHeld()
        {
            if (heldFrom == HoldFrom.Grid)
            {
                ItemsOf(heldSet, heldPage).Remove(heldView.Source);
                List<ItemView> from = heldSet.ViewsOf(heldPage);
                from.Remove(heldView);
                heldSet.Grids[heldPage].SetItems(from);
                RetitleBook(heldSet);
            }
            else if (heldFrom == HoldFrom.Equipment)
            {
                SlotList(heldSlot)[heldSlot.Index] = EmptyItem();
                heldSlot.Item = null;
                equipment.Invalidate();
            }
        }

        List<Item> SlotList(EquipmentSlot slot)
        {
            return slot.Group == "weapon1" ? character.Weapons1 :
                   slot.Group == "weapon2" ? character.Weapons2 : character.Equipment;
        }

        // What the game leaves in an empty slot: no record path, stack count one,
        // everything else zero. Checked against all fifteen sample saves.
        Item EmptyItem() { return Item.Create("", character.ItemTailWords); }

        Item WornIn(string group, int index)
        {
            List<Item> list = group == "weapon1" ? character.Weapons1 :
                              group == "weapon2" ? character.Weapons2 : character.Equipment;
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        static bool Occupied(Item it) { return it != null && !string.IsNullOrEmpty(it.BaseName); }

        // ------------------------------------------------ wearing and removing

        void EquipmentClicked(EquipmentClickEventArgs e)
        {
            if (character == null) return;
            if (e.Button == MouseButtons.Right) { CancelHold(); return; }
            if (e.Button != MouseButtons.Left || e.Slot == null) return;

            if (editMode)
            {
                if (e.Slot.Item != null) EditItem(e.Slot.Item); else CancelEdit();
                return;
            }

            if (heldView == null)
            {
                if (e.Slot.Item == null) return;
                Hold(e.Slot.Item, HoldFrom.Equipment, null, -1, e.Slot);
                statusLabel.Text = Lang.T("main.status.takenOff", heldView.Name);
                equipment.Invalidate();
                return;
            }

            // Clicking the slot the item came from simply puts it back.
            if (e.Slot == heldSlot) { CancelHold(); return; }

            // A COMPONENT OR AN AUGMENT GOES INTO WHAT IS WORN TOO, and that is the
            // half that matters: the sums are recounted straight away, so the panel
            // shows what the part did.
            if (ItemParts.KindOf(heldView.ItemClass) != ItemParts.Part.None)
            {
                Item worn = WornIn(e.Slot.Group, e.Slot.Index);
                if (Occupied(worn))
                {
                    AttachHeldTo(worn, e.Slot.Item == null ? e.Slot.Label : e.Slot.Item.Name);
                    return;
                }
            }

            string why;
            if (!CanEquip(e.Slot, out why)) { statusLabel.Text = why; return; }
            EquipHeld(e.Slot);
        }

        // The rules the panel paints and the click enforces, in one place.
        bool CanEquip(EquipmentSlot slot, out string why)
        {
            why = null;
            if (heldView == null) return false;
            // A save without a recorded game entry has no inventory block, so it
            // cannot accept equipment edits. Do not synthesize an unverified binary block.
            if (character != null && character.InventoryHasData == 0)
            {
                why = Lang.T("main.why.noInventory");
                return false;
            }
            if (slot == heldSlot) { why = Lang.T("main.why.sameSlot"); return false; }

            if (!slot.Takes(heldView.ItemClass))
            {
                // Keep the game record class out of the user-facing slot message.
                why = Lang.T("main.why.wrongSlot", heldView.Name, slot.Label);
                return false;
            }

            if (Occupied(WornIn(slot.Group, slot.Index)))
            {
                why = Lang.T("main.why.slotBusy", slot.Label);
                return false;
            }

            // The one requirement the game really stores. Attribute requirements
            // are computed by the game and we cannot reproduce them reliably, so
            // they are not checked — see ItemRequirements.cs.
            if (heldView.Needs.Level > character.Level)
            {
                why = Lang.T("main.why.level", heldView.Name, heldView.Needs.Level, character.Level);
                return false;
            }

            // A two-hander takes both hands, so it needs the off hand free, and
            // nothing goes in the off hand while one is equipped.
            if (slot.IsWeapon)
            {
                bool twoHanded = EquipRules.IsTwoHanded(heldView.ItemClass);
                if (slot.Index == 0 && twoHanded && Occupied(WornIn(slot.Group, 1)))
                {
                    why = Lang.T("main.why.twoHanded");
                    return false;
                }
                if (slot.Index == 1)
                {
                    // The panel already holds a view of what is worn, so the main
                    // hand's class is read from there rather than rebuilt on every
                    // repaint.
                    EquipmentSlot main = equipment.Find(slot.Group, 0);
                    if (main != null && main.Item != null && EquipRules.IsTwoHanded(main.Item.ItemClass))
                    {
                        why = Lang.T("main.why.offHandBusy");
                        return false;
                    }
                }
            }
            return true;
        }

        bool SlotWouldAccept(EquipmentSlot slot)
        {
            // A held component targets insertion rather than equipment; treat the
            // slot as valid when the component can be inserted into the item.
            if (heldView != null && ItemParts.KindOf(heldView.ItemClass) != ItemParts.Part.None)
                return PartGoesInto(WornIn(slot.Group, slot.Index));
            string why;
            return CanEquip(slot, out why);
        }

        // Whether the held component can be inserted into this item; shared by the
        // inventory grids and equipment panel.
        bool PartGoesInto(Item target)
        {
            if (heldView == null || game == null || !Occupied(target)) return false;
            DbRecord part = game.Record(heldView.Source.BaseName);
            if (ItemParts.KindOf(part == null ? "" : part.Class) == ItemParts.Part.None) return false;
            DbRecord rec = game.Record(target.BaseName);
            return rec != null && ItemParts.Fits(part, rec.Class);
        }

        // Same check for an inventory or stash item; the grid stores the view.
        bool PartGoesIntoView(ItemView v)
        {
            return v != null && v != heldView && PartGoesInto(v.Source);
        }

        void EquipHeld(EquipmentSlot slot)
        {
            ItemView view = heldView;
            Item item = view.Source;
            bool wasWorn = heldFrom == HoldFrom.Equipment;

            DetachHeld();
            item.Attached = 1;
            item.XRaw = 0;
            item.YRaw = 0;
            view.X = 0;
            view.Y = 0;
            SlotList(slot)[slot.Index] = item;

            ClearHold();
            // THE WORN SET CHANGED, SO EVERYTHING COUNTED FROM IT IS RECOUNTED (`ReloadViews`):
            // the set bonus, the totals panel, and every tooltip that names the character.
            ReloadViews();
            saver.RequestSave();
            statusLabel.Text = Lang.T(wasWorn ? "main.status.reequipped" : "main.status.equipped", view.Name, slot.Label);
        }

        void DeleteHeld()
        {
            if (heldView == null) return;
            ItemView view = heldView;
            if (heldFrom == HoldFrom.Catalog)
            {
                ClearHold();
                statusLabel.Text = Lang.T("main.status.discarded", view.Name);
                return;
            }

            DetachHeld();
            ClearHold();
            // Deleting a worn item changes the worn set just as taking it off does.
            ReloadViews();
            saver.RequestSave();
            statusLabel.Text = Lang.T("main.status.deleted", view.Name);
        }

        // Dropping on a tab header puts the item in the first free spot of that
        // bag, scanning left to right and top to bottom as the game does.
        void DropOnTab(GridSet set, int page)
        {
            if (heldView == null || page < 0 || page >= set.Count) return;
            Point? spot = set.Grids[page].FindFreeSpot(heldView.CellsWide, heldView.CellsHigh,
                heldFrom == HoldFrom.Grid && heldSet == set && page == heldPage ? heldView : null);
            if (spot == null)
            {
                statusLabel.Text = Lang.T(set.IsStash ? "main.status.noRoomStash" : "main.status.noRoomBag",
                                          heldView.CellsWide, heldView.CellsHigh);
                return;
            }
            PlaceHeld(set, page, spot.Value);
        }

        void CancelHold()
        {
            if (heldView == null) return;
            string name = heldView.Name;
            ClearHold();
            statusLabel.Text = Lang.T("main.status.cancelled", name);
        }

        void ClearHold()
        {
            heldView = null;
            heldFrom = HoldFrom.Catalog;
            heldSet = null;
            heldPage = -1;
            heldSlot = null;
            bags.SetHeld(null); bags.SetFits(null); bags.InvalidateAll();
            stash.SetHeld(null); stash.SetFits(null); stash.InvalidateAll();
            equipment.Held = null;
            trash.Armed = false;
        }

        void UpdateHoldStatus()
        {
            statusLabel.Text = Lang.T("main.status.inHand", heldView.Name, heldView.CellsWide, heldView.CellsHigh);
        }

        // ------------------------------------------------------- self checking

        // The following command-line reports are developer diagnostics and are not
        // localized.
        #region Self-check — not interface

        // Puts the first item of the shown bag in hand, so a screenshot can show
        // what the window looks like mid-move: the drop highlights on the bag,
        // the accept/refuse wash on the equipment, the armed delete strip.
        public void HoldFirstItemForShot()
        {
            int bag = bags.Tabs.SelectedIndex;
            if (character == null || bag < 0 || bag >= bags.Count) return;
            List<ItemView> views = bags.ViewsOf(bag);
            foreach (ItemView v in views)
                if (v.SizeKnown) { Hold(v, HoldFrom.Grid, bags, bag, null); UpdateHoldStatus(); return; }
        }

        // Hold the first component for `--screenshot ... --holdpart` so valid targets
        // can be captured.
        public void HoldFirstPartForShot()
        {
            if (character == null || game == null) return;
            foreach (GridSet set in new GridSet[] { bags, stash })
                for (int p = 0; p < set.Count; p++)
                    foreach (ItemView v in set.ViewsOf(p))
                    {
                        if (!v.SizeKnown) continue;
                        if (ItemParts.KindOf(v.ItemClass) == ItemParts.Part.None) continue;
                        if (set == stash) stash.Tabs.SelectedIndex = p; else bags.Tabs.SelectedIndex = p;
                        Hold(v, HoldFrom.Grid, set, p, null);
                        UpdateHoldStatus();
                        return;
                    }
        }








        #endregion

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && editMode) { CancelEdit(); return true; }
            if (keyData == Keys.Escape && heldView != null) { CancelHold(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            saver.Flush();   // nothing typed in the last moment gets lost
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (game != null) game.Dispose();
            tooltip.Dispose();
        }
    }

    public class SettingsForm : Form
    {
        readonly Settings settings;
        readonly TextBox gameBox = new TextBox();
        readonly TextBox savesBox = new TextBox();
        readonly ComboBox languageBox = new ComboBox();
        readonly CheckBox rawBox = new CheckBox();
        readonly Action rebuildCatalog;

        public SettingsForm(Settings s, Action rebuildCatalog)
        {
            settings = s;
            this.rebuildCatalog = rebuildCatalog;
            Text = Lang.T("settings.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 292);

            // Keep the gap below a label at least as high as the label itself; size
            // labels from their text and use a minimum row step.
            Label l1 = new Label { Text = Lang.T("settings.gameFolder"), Left = 12, Top = 14, AutoSize = true };
            gameBox.Left = 12; gameBox.Top = 40; gameBox.Width = 500; gameBox.Text = s.GamePath ?? "";
            Button b1 = new Button { Text = Lang.T("settings.browse"), Left = 520, Top = 39, Width = 80 };
            b1.Click += delegate { Browse(gameBox, Lang.T("settings.gameFolder.browse")); };

            Label l2 = new Label { Text = Lang.T("settings.savesFolder"), Left = 12, Top = 78, AutoSize = true };
            savesBox.Left = 12; savesBox.Top = 104; savesBox.Width = 500; savesBox.Text = s.SavesPath ?? "";
            Button b2 = new Button { Text = Lang.T("settings.browse"), Left = 520, Top = 103, Width = 80 };
            b2.Click += delegate { Browse(savesBox, Lang.T("settings.savesFolder.browse")); };

            // Place the language list and button after the localized label width.
            Label l3 = new Label { Text = Lang.T("settings.language"), Left = 12, Top = 146, AutoSize = true };
            languageBox.Left = l3.Left + l3.PreferredWidth + 8; languageBox.Top = 142; languageBox.Width = 70;
            languageBox.DropDownStyle = ComboBoxStyle.DropDownList;
            List<string> languages = GameData.AvailableLanguages(s.GamePath);
            if (languages.Count == 0) languages.Add("EN");
            languageBox.Items.AddRange(languages.ToArray());
            languageBox.SelectedItem = languages.Contains(s.Language ?? Settings.DefaultLanguage) ? (s.Language ?? Settings.DefaultLanguage) : languages[0];

            Button rebuild = new Button { Text = Lang.T("settings.rebuildCatalog"), Left = languageBox.Right + 18, Top = 141, Width = 250 };
            rebuild.Click += delegate
            {
                settings.GamePath = gameBox.Text.Trim();
                if (languageBox.SelectedItem != null) settings.Language = languageBox.SelectedItem.ToString();
                settings.Save();
                if (rebuildCatalog != null) rebuildCatalog();
            };

            rawBox.Left = 12; rawBox.Top = 184; rawBox.AutoSize = true;
            rawBox.Text = Lang.T("settings.rawFields");
            rawBox.Checked = s.ShowRawFields;

            Label hint = new Label
            {
                Left = 12,
                Top = 216,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = Lang.T("settings.fileHint")
            };

            Button ok = new Button { Text = Lang.T("settings.ok"), DialogResult = DialogResult.OK, Left = 430, Top = 250, Width = 80 };
            Button cancel = new Button { Text = Lang.T("settings.cancel"), DialogResult = DialogResult.Cancel, Left = 520, Top = 250, Width = 80 };
            ok.Click += delegate
            {
                settings.GamePath = gameBox.Text.Trim();
                settings.SavesPath = savesBox.Text.Trim();
                settings.ShowRawFields = rawBox.Checked;
                if (languageBox.SelectedItem != null) settings.Language = languageBox.SelectedItem.ToString();
            };

            Controls.AddRange(new Control[] { l1, gameBox, b1, l2, savesBox, b2, l3, languageBox, rebuild, rawBox, hint, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static void Browse(TextBox target, string title)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = title;
                if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
                if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.SelectedPath;
            }
        }
    }
}
