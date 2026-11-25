// "IMPORT A CHARACTER FROM GRIMTOOLS" — the window the link is pasted into.
//
// The work itself is elsewhere: `GrimTools` fetches, `GrimToolsMap` matches the
// numbers to the game's records, `CharacterDress` writes them into the save. This
// window asks for the link, keeps the user company while the two files come down,
// and says afterwards what went in and what did not.
//
// WHY A THREAD. Their item database is eight megabytes of text and takes a moment
// to come down and read; doing that on the interface's own thread would freeze the
// window and look like a hang.
//
// A visible progress indicator is required so users can distinguish a slow import
// from a failure and know when the import is complete. Three cases are handled:
//
//   * THE PROGRESS. A step line that is always current, and a bar that fills as the
//     download comes in — their database is a megabyte over the wire and the window
//     used to sit still through all of it.
//   * THE END. The step line says it in so many words and turns green, and the one
//     button becomes «close»; before, the report simply stopped growing.
//   * THE TROUBLE, EACH KIND BY ITS OWN NAME. No network, the page would not open,
//     the page has no build in it, their database would not parse. `GtFetchError`
//     carries the kind as a key into the language files, and what did not match is
//     said by the COUNT — a number of theirs in a window would tell nobody anything.
//
// EVERY LINE HERE COMES FROM `lang/<CODE>/import.txt`, in all twelve languages. The
// slot names too: the build calls them `weapon1Alt` and `amulet`, and those are
// names for a file, not for a person.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class GrimToolsImportForm : Form
    {
        readonly GameData game;
        readonly Character character;
        readonly TextBox link = new TextBox();
        readonly Button go = new Button();
        readonly Label note = new Label();
        readonly Label status = new Label();
        // The bar is two panels rather than a `ProgressBar`: the system one keeps its
        // own colours whatever it is told, and this window is dark.
        readonly Panel barFrame = new Panel();
        readonly Panel barFill = new Panel();
        readonly TextBox report = new TextBox();

        public bool Imported { get; private set; }

        public GrimToolsImportForm(GameData game, Character character)
        {
            this.game = game;
            this.character = character;

            AppIcon.Apply(this);
            Text = Lang.T("import.title");
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 22, 20);
            ClientSize = new Size(720, 470);
            MinimumSize = new Size(560, 380);

            Label what = new Label
            {
                Left = 12, Top = 12, Width = 690, Height = 34, ForeColor = Color.Gainsboro,
                Text = Lang.T("import.paste"),
            };
            link.Left = 12;
            link.Top = 52;
            link.Width = 560;
            link.ForeColor = Color.Gainsboro;
            link.BackColor = Color.FromArgb(38, 35, 32);
            link.BorderStyle = BorderStyle.FixedSingle;
            // A link on the clipboard is almost always the one that is wanted here.
            try
            {
                string had = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (had != null && had.IndexOf("grimtools.com/calc/", StringComparison.OrdinalIgnoreCase) >= 0)
                    link.Text = had.Trim();
            }
            catch (Exception) { }

            go.Left = 582;
            go.Top = 51;
            go.Width = 120;
            go.Height = 25;
            go.Text = Lang.T("import.go");
            go.ForeColor = Color.Gainsboro;
            go.BackColor = Color.FromArgb(38, 35, 32);
            go.FlatStyle = FlatStyle.Flat;
            // The one button does both jobs: it starts the import, and once the
            // character has been dressed it closes the window.
            go.Click += delegate { if (Imported) { DialogResult = DialogResult.OK; Close(); } else Start(); };

            note.Left = 12;
            note.Top = 84;
            note.Width = 690;
            // Two lines: the note about the rolls does not fit on one at any width
            // the window is likely to have.
            note.Height = 34;
            note.ForeColor = Color.DimGray;
            note.Text = Lang.T("import.seedNote");

            // THE STEP LINE. It always says what is happening right now, and when the
            // import is over it says so in words and turns green.
            status.Left = 12;
            status.Top = 122;
            status.Width = 690;
            status.Height = 18;
            status.ForeColor = Color.Gainsboro;
            status.Text = "";

            barFrame.Left = 12;
            barFrame.Top = 142;
            barFrame.Width = 690;
            barFrame.Height = 8;
            barFrame.BackColor = Color.FromArgb(38, 35, 32);
            barFrame.Visible = false;
            barFill.Left = 0;
            barFill.Top = 0;
            barFill.Width = 0;
            barFill.Height = 8;
            barFill.BackColor = Color.FromArgb(186, 152, 84);
            barFrame.Controls.Add(barFill);

            report.Left = 12;
            report.Top = 158;
            report.Width = 690;
            report.Height = 300;
            report.Multiline = true;
            report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Vertical;
            report.WordWrap = false;
            report.ForeColor = Color.Gainsboro;
            report.BackColor = Color.FromArgb(20, 19, 18);
            report.BorderStyle = BorderStyle.FixedSingle;
            report.Font = new Font("Consolas", 9f);

            Controls.AddRange(new Control[] { what, link, go, note, status, barFrame, report });
            Resize += delegate
            {
                link.Width = ClientSize.Width - 160;
                go.Left = link.Right + 10;
                what.Width = note.Width = status.Width = barFrame.Width = report.Width = ClientSize.Width - 24;
                report.Height = ClientSize.Height - 170;
                Fill();
            };
            AcceptButton = go;
        }

        void Say(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Say), line); return; }
            report.AppendText(line + Environment.NewLine);
        }

        // `--importshot <save> <png> --running` — the window as it looks WHILE the
        // download is going: the step line and the bar half full. Without it a
        // snapshot only ever catches the window idle, and the parts that were added
        // because the window was silent would be the very parts never seen.
        public void ShowRunning()
        {
            Say(Lang.T("import.step.page"));
            Say(Lang.T("import.step.database"));
            Step(Lang.T("import.status.database", 1, 2, Mb(3800000), Mb(8730000)), 3800000.0 / 8730000.0);
            go.Enabled = false;
            link.Enabled = false;
        }

        // `--importshot <save> <png> --finished` — and the window as it looks when it
        // is over, which is the other half of the same want: the last line of the log
        // has to say so, not only the button at the top.
        public void ShowFinished()
        {
            Say(Lang.T("import.step.match"));
            Say("");
            Say(Lang.T("import.result.gear", 14, 0));
            Say(Lang.T("import.result.skills", 25, 55, 6));
            Say(Lang.T("import.result.bio", 100, 698, 50, 274));
            Say("");
            Say(SlotName("weapon1") + ":   …");
            Say("");
            Say(Lang.T("import.status.done"));
            Finished(0);
        }

        // ------------------------------------------------------- the visible progress

        string step = "";
        double share = -1;       // how much of the download is in, or −1 for no bar

        // The step line and the bar, both set from the worker thread. The share is
        // kept here so that resizing the window refills the bar to the same place.
        void Step(string line, double done)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, double>(Step), line, done); return; }
            step = line ?? "";
            share = done;
            status.ForeColor = Color.Gainsboro;
            status.Text = step;
            barFrame.Visible = done >= 0;
            Fill();
        }

        void Fill()
        {
            double at = share < 0 ? 0 : (share > 1 ? 1 : share);
            barFill.Width = (int)Math.Round(barFrame.ClientSize.Width * at);
        }

        // THE DOWNLOAD IS LOUD, BUT NOT TOO LOUD: a line per 64 KB piece would be
        // fifty times a second on a good connection, and the window would spend its
        // time redrawing. A fifth of a second is often enough to look alive.
        DateTime spoke = DateTime.MinValue;

        string Fetching(string url, int which, int howMany)
        {
            spoke = DateTime.MinValue;
            return GrimTools.Get(url, delegate (long done, long total)
            {
                DateTime now = DateTime.UtcNow;
                bool last = total > 0 && done >= total;
                if (!last && (now - spoke).TotalMilliseconds < 200) return;
                spoke = now;
                if (total > 0)
                    Step(Lang.T("import.status.database", which, howMany, Mb(done), Mb(total)),
                         (double)done / total);
                else
                    Step(Lang.T("import.status.databaseSoFar", which, howMany, Mb(done)), -1);
            });
        }

        // Megabytes, one decimal, in the user's own notation — a size is a number and
        // the word beside it comes from the language file.
        static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture);
        }

        void Start()
        {
            string text = link.Text.Trim();
            if (GrimTools.BuildId(text) == null)
            {
                MessageBox.Show(this, Lang.T("import.badLink"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            go.Enabled = false;
            link.Enabled = false;
            report.Clear();
            Step("", -1);
            Thread worker = new Thread(delegate () { Run(text); });
            worker.IsBackground = true;
            worker.Start();
        }

        void Run(string text)
        {
            try
            {
                string id = GrimTools.BuildId(text);
                Step(Lang.T("import.step.page"), -1);
                Say(Lang.T("import.step.page"));
                string html = GrimTools.Get(GrimTools.Host + "/calc/" + id);
                // The data no longer sits in the page itself: it is asked for with the
                // page's own token, in a second request (MECHANICS 116). `BuildJsonOf`
                // still takes it straight from the page when it is there.
                string json = GrimTools.BuildJsonOf(html, GrimTools.Get);
                // The page opened and there is no build on it — a different trouble
                // from the page not opening, and it gets a different sentence.
                if (json == null) { Fail("import.err.build", null); return; }

                string dbUrl = GrimTools.DbUrlOf(html), calcUrl = GrimTools.CalcUrlOf(html);
                string stamp = GrimTools.StampOf(dbUrl);
                // THE CACHE MUST MATCH THE GAME AS WELL AS THEIR DATABASE: it holds our own
                // item signatures, counted from the game's records, and a patch moves those
                // while their stamp stays where it was (MECHANICS 116).
                GtDb db = GrimTools.LoadCache(stamp, game.GameDir);
                if (db == null)
                {
                    Say(Lang.T("import.step.database"));
                    string js = Fetching(dbUrl, 1, 2);
                    string calc = Fetching(calcUrl, 2, 2);
                    Step(Lang.T("import.step.reading"), -1);
                    Say(Lang.T("import.step.reading"));
                    db = GrimTools.ParseDb(js, calc, stamp);
                    db.GameSignature = ItemCatalog.SignatureOf(game.GameDir);
                    GrimTools.SaveCache(db);
                }
                else Say(Lang.T("import.step.haveDatabase"));
                if (db.Items.Count == 0) { Fail("import.err.database", null); return; }

                Step(Lang.T("import.step.match"), -1);
                Say(Lang.T("import.step.match"));
                GtCharacter build = GrimToolsMap.Resolve(game, db, json, null);
                if (build.NoData) { Fail("import.err.build", null); return; }
                if (build.NoCatalog) { Fail("import.err.catalog", null); return; }

                Step(Lang.T("import.step.dress"), -1);
                DressResult dressed = CharacterDress.Apply(game, character, build);

                Say("");
                Say(Lang.T("import.result.gear", dressed.Slots, dressed.Cleared));
                Say(Lang.T("import.result.skills", dressed.Skills, dressed.Stars, dressed.Bindings));
                Say(Lang.T("import.result.bio", build.Level, build.Physique, build.Cunning, build.Spirit));

                // WHAT DID NOT COME OVER, SAID PLAINLY AND BY THE COUNT. It used to be
                // said nowhere at all: the report of the check knew, the window did not.
                List<string> missed = new List<string>();
                if (dressed.Skipped > 0)
                    missed.Add(Lang.T("import.missed.items", dressed.Skipped,
                                      string.Join(", ", SlotNames(dressed.Notes))));
                if (build.UnmatchedParts > 0) missed.Add(Lang.T("import.missed.parts", build.UnmatchedParts));
                if (build.UnmatchedSkills > 0) missed.Add(Lang.T("import.missed.skills", build.UnmatchedSkills));
                if (build.UnmatchedPowers > 0) missed.Add(Lang.T("import.missed.powers", build.UnmatchedPowers));
                if (build.UnmatchedShapes > 0) missed.Add(Lang.T("import.missed.shapes", build.UnmatchedShapes));
                if (dressed.Collapsed > 0) missed.Add(Lang.T("import.missed.repeats", dressed.Collapsed));
                if (missed.Count > 0)
                {
                    Say("");
                    foreach (string one in missed) Say(one);
                }

                Say("");
                foreach (GtItem one in build.Items)
                {
                    if (one.ItemName.Length == 0) continue;
                    Say(SlotName(one.Slot) + ":   " + one.ItemName);
                }
                // AND A LAST LINE THAT SAYS IT IS OVER. The user's words, 2 October:
                // the eye waits for the bottom of the list to finish something, while
                // the only word about the end stood at the top of the window.
                Say("");
                Say(missed.Count > 0 ? Lang.T("import.status.donePartly") : Lang.T("import.status.done"));
                Finished(missed.Count);
            }
            catch (GtFetchError e)
            {
                Fail(e.Key, e.Detail);
            }
            catch (Exception e)
            {
                // Anything we did not foresee still gets a sentence of our own, with
                // the system's own words after it: better a long line than silence.
                Fail("import.err.other", e.Message);
            }
        }

        // The build's own name for a slot («weapon1Alt», «amulet») is a name for a
        // file. The window says what a player would say.
        //
        // EVERY KEY IS WRITTEN OUT WHOLE and not glued together from pieces, because
        // the language check finds keys by reading the source: a key assembled at run
        // time would be invisible to it and would go untranslated (`LangTest.cs`).
        static readonly Dictionary<string, string> SlotKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "head", "import.slot.head" },
            { "amulet", "import.slot.amulet" },
            { "chest", "import.slot.chest" },
            { "legs", "import.slot.legs" },
            { "feet", "import.slot.feet" },
            { "hands", "import.slot.hands" },
            { "ring1", "import.slot.ring1" },
            { "ring2", "import.slot.ring2" },
            { "waist", "import.slot.waist" },
            { "shoulders", "import.slot.shoulders" },
            { "medal", "import.slot.medal" },
            { "relic", "import.slot.relic" },
            { "weapon1", "import.slot.weapon1" },
            { "weapon2", "import.slot.weapon2" },
            { "weapon1Alt", "import.slot.weapon1Alt" },
            { "weapon2Alt", "import.slot.weapon2Alt" },
        };

        static string SlotName(string slot)
        {
            string key;
            if (!SlotKeys.TryGetValue(slot ?? "", out key)) return slot ?? "";
            string got = Lang.T(key);
            return string.IsNullOrEmpty(got) || got[0] == '[' ? slot : got;
        }

        static string[] SlotNames(List<string> slots)
        {
            List<string> made = new List<string>();
            foreach (string one in slots) made.Add(SlotName(one));
            return made.ToArray();
        }

        void Fail(string key, string detail)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(Fail), key, detail); return; }
            string said = string.IsNullOrEmpty(detail) ? Lang.T(key) : Lang.T(key, detail);
            Say("");
            Say(said);
            Step(Lang.T("import.status.failed"), -1);
            status.ForeColor = Color.FromArgb(214, 108, 96);
            go.Enabled = true;
            link.Enabled = true;
        }

        // THE END, SAID OUT LOUD. The user could not tell when the import was over,
        // and that is the whole of this: a green line, and the one button turns into
        // «close».
        void Finished(int missed)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int>(Finished), missed); return; }
            Step(missed > 0 ? Lang.T("import.status.donePartly") : Lang.T("import.status.done"), -1);
            status.ForeColor = missed > 0 ? Color.FromArgb(212, 176, 96) : Color.FromArgb(132, 186, 112);
            go.Enabled = true;
            link.Enabled = true;
            Imported = true;
            go.Text = Lang.T("import.close");
        }
    }
}
