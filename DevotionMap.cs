// THE DEVOTION MAP, DRAWN WITH THE GAME'S OWN PICTURES.
//
// The same thing that was done for the mastery trees (MECHANICS 102e), for the
// sky: nothing here is our own artwork. The star field, the nebulae, the picture
// of every constellation, the stars and the lines between them are the textures
// the game ships, laid at the coordinates the game's own records give.
//
// WHERE IT ALL COMES FROM — `records/ui/skills/devotion/devotion_mastertable.dbr`:
//
//     sizeX / sizeY                  the size of the sky; coordinates are counted
//                                    from its CENTRE and are freely negative
//     bgTile                         the star field, one 1024×1024 tile repeated
//     nebulaSections                 five pictures, each with its own position
//     connectionActive/Inactive/Locked  the line between two stars, 78×16
//     connectionWidth                how thick that line is drawn, 7
//     skill/active/availableBorderBitmap   the three round 52×52 rings
//     emptySkillBitmap               the "?" a celestial power shows until it is
//                                    bound to a skill
//     effectBgBitmap                 the glow behind a star that carries a power
//     devotionConstellation1..N      the constellations themselves
//
// THE SKY GREW WITH EVERY EXPANSION and the records were re-positioned each time —
// 6144×5120, then 6144×6144, then 7168×6144 — so every number is read, none is
// written down. `GameData.Record` takes the last archive that has a record, which
// is the one the game uses.
//
// WHAT IS OURS AND NOT THE GAME'S, so that it is not mistaken for measured:
//   * the map is scaled to fit the panel and zoomed with a wheel or two buttons;
//     the game has its own zoom buttons in its side bar, which we do not draw;
//   * the line between two stars is drawn as the 78×16 texture stretched along it
//     and `connectionWidth` pixels thick. The texture is the same all the way
//     along its length (checked pixel by pixel: it only changes across), so
//     stretching and repeating give the same picture — but whether the game draws
//     it 7 pixels thick or the texture's own 16 is not measured;
//   * the game has four constellation states and the record has only three sets of
//     colour, so "some stars taken" is drawn with the same tint as "open and
//     empty".

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Gdc.Gui
{
    // Everything the master table says about the sky as a whole.
    public sealed class DevotionSky
    {
        public int SizeX = 6144, SizeY = 5120;
        public string TilePath;
        public readonly List<string> NebulaPaths = new List<string>();
        public readonly List<Point> NebulaAt = new List<Point>();
        public string LineOn, LineOff, LineLocked;
        public int LineWidth = 7;
        public string BorderPlain, BorderActive, BorderAvailable;
        public string EmptyIcon;
        public string PowerGlow;
        public int PowerGlowX, PowerGlowY;

        public static DevotionSky Of(GameData game)
        {
            DevotionSky sky = new DevotionSky();
            DbRecord master = game == null ? null : game.Record(DevotionTable.MasterTable);
            if (master == null) return sky;
            if (master.GetInt("sizeX") > 0) sky.SizeX = master.GetInt("sizeX");
            if (master.GetInt("sizeY") > 0) sky.SizeY = master.GetInt("sizeY");
            sky.TilePath = master.Get("bgTile");
            foreach (string one in (master.Get("nebulaSections") ?? "").Split(';'))
            {
                DbRecord rec = string.IsNullOrEmpty(one) ? null : game.Record(one.Trim());
                if (rec == null) continue;
                sky.NebulaPaths.Add(rec.Get("bitmapName"));
                sky.NebulaAt.Add(new Point(rec.GetInt("bitmapPositionX"), rec.GetInt("bitmapPositionY")));
            }
            sky.LineOn = master.Get("connectionActiveTexture");
            sky.LineOff = master.Get("connectionInactiveTexture");
            sky.LineLocked = master.Get("connectionLockedTexture");
            if (master.GetInt("connectionWidth") > 0) sky.LineWidth = master.GetInt("connectionWidth");
            sky.BorderPlain = Bitmap(game, master.Get("skillBorderBitmap"));
            sky.BorderActive = Bitmap(game, master.Get("activeBorderBitmap"));
            sky.BorderAvailable = Bitmap(game, master.Get("availableBorderBitmap"));
            sky.EmptyIcon = master.Get("emptySkillBitmap");
            DbRecord glow = game.Record(master.Get("effectBgBitmap") ?? "");
            if (glow != null)
            {
                sky.PowerGlow = glow.Get("bitmapName");
                sky.PowerGlowX = glow.GetInt("bitmapPositionX");
                sky.PowerGlowY = glow.GetInt("bitmapPositionY");
            }
            return sky;
        }

        static string Bitmap(GameData game, string dbr)
        {
            DbRecord rec = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            return rec == null ? null : rec.Get("bitmapName");
        }
    }

    public class DevotionHoverEventArgs : EventArgs
    {
        public readonly DevotionStar Star;
        public readonly Point ScreenPoint;
        public DevotionHoverEventArgs(DevotionStar s, Point p) { Star = s; ScreenPoint = p; }
    }

    public class DevotionClickEventArgs : EventArgs
    {
        public readonly DevotionStar Star;
        public readonly MouseButtons Button;
        public readonly Point ScreenPoint;
        public DevotionClickEventArgs(DevotionStar s, MouseButtons b, Point p)
        { Star = s; Button = b; ScreenPoint = p; }
    }

    public partial class DevotionMap : Control
    {
        readonly GameData game;
        Character character;
        readonly DevotionSky sky;
        readonly List<Constellation> all = new List<Constellation>();

        // What the character has, read again after every click: which stars are
        // taken, what affinity that adds up to, and which class skill each
        // celestial power is bound to (the icon a star shows is the bound skill's).
        Dictionary<string, int> taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> affinity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> boundTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        DevotionStar hovered;
        float scale = 0.1f;
        float viewX, viewY;            // map coordinate at the top left of the panel
        bool dragging;
        Point dragFrom;
        float dragViewX, dragViewY;
        bool placed;                   // has the first fit-to-panel been done

        // The star pictures are 64×64 and their icons 32×32 — the game's own sizes,
        // taken from the textures themselves when they load.
        const int StarSize = 64, IconSize = 32, RingSize = 52;

        public DevotionMap(GameData game, Character character)
        {
            this.game = game;
            this.character = character;
            DoubleBuffered = true;
            // The wheel zooms, and a wheel message only reaches the control that
            // has the focus — so the map takes it as the mouse comes in.
            SetStyle(ControlStyles.Selectable, true);
            TabStop = false;
            BackColor = Color.FromArgb(6, 6, 10);
            sky = DevotionSky.Of(game);
            all.AddRange(DevotionTable.Backdrops(game));
            all.AddRange(DevotionTable.Of(game));
            Reload(character);
        }

        // The levels read again out of the character, after a point went in or came
        // out. The layout is the game's and does not move.
        public void Reload(Character c)
        {
            character = c;
            taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in c.Skills)
                if (!string.IsNullOrEmpty(s.Name) && s.Level > 0) taken[s.Name] = s.Level;
            affinity = DevotionEdit.Affinity(game, c);
            boundTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in c.Skills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill)) boundTo[s.AutoCastSkill] = s.Name;
            foreach (ItemSkill s in c.ItemSkills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill)) boundTo[s.AutoCastSkill] = s.SkillName;
            Invalidate();
        }

        // ------------------------------------------------------------ geometry

        int MapLeft { get { return -sky.SizeX / 2; } }
        int MapTop { get { return -sky.SizeY / 2; } }

        // Named `Zoom` rather than `Scale`: `Control` already has a `Scale` of its
        // own, and hiding it would be asking for trouble.
        public float Zoom { get { return scale; } }

        // WHAT IS ACTUALLY DRAWN, which is not the whole sky. The master table gives
        // 7168 × 6144, and the constellations sit inside a smaller box than that —
        // fitting the panel to the declared size would put a fifth of the view on
        // empty black. So the box is measured off the pictures and the stars
        // themselves, and that is what the zoom fits and the panning stops at.
        Rectangle content = Rectangle.Empty;

        void MeasureContent()
        {
            if (content != Rectangle.Empty) return;
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (Constellation k in all)
            {
                Image art = Texture(k.ArtPath);
                if (art != null)
                {
                    l = Math.Min(l, k.ArtX); t = Math.Min(t, k.ArtY);
                    r = Math.Max(r, k.ArtX + art.Width); b = Math.Max(b, k.ArtY + art.Height);
                }
                foreach (DevotionStar star in k.Buttons)
                {
                    l = Math.Min(l, star.X); t = Math.Min(t, star.Y);
                    r = Math.Max(r, star.X + StarSize); b = Math.Max(b, star.Y + StarSize);
                }
            }
            if (l > r || t > b) { content = new Rectangle(MapLeft, MapTop, sky.SizeX, sky.SizeY); return; }
            content = new Rectangle(l, t, r - l, b - t);
        }

        float FitScale()
        {
            if (Width <= 0 || Height <= 0) return 0.1f;
            MeasureContent();
            return Math.Min((float)Width / content.Width, (float)Height / content.Height);
        }

        // Everything at once, and that is also as far out as the zoom goes: there is
        // nothing past the edge of the drawn sky to look at.
        public void Fit()
        {
            MeasureContent();
            scale = FitScale();
            CentreOn(content.X + content.Width / 2f, content.Y + content.Height / 2f);
            placed = true;
        }

        // For a headless snapshot: the map at this zoom, centred here. Without it
        // `--skillshot` could only ever show the whole sky at once, and a star at
        // that size is six pixels across.
        public void ShowAt(float zoom, float mx, float my)
        {
            MeasureContent();
            scale = Math.Max(0.02f, Math.Min(1f, zoom));
            CentreOn(mx, my);
            placed = true;
            Invalidate();
            EventHandler h = ZoomChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // Where a star stands, so a snapshot can be asked for by name.
        public DevotionStar Find(string part)
        {
            if (string.IsNullOrEmpty(part)) return null;
            foreach (Constellation k in all)
                foreach (DevotionStar star in k.Buttons)
                    if (star.Dbr.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        k.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return star;
            return null;
        }

        // Everything the map read, as text — what a headless run can check without
        // looking at a picture: one line per constellation and one per star, with the
        // picture each asked for and whether it was found.
        // The report is written in Russian like every other check report, and is not
        // interface text — hence the region, which keeps it out of the language files.
        #region Self-check — not interface (the `--skillshot … .map.txt` report, not moved into the language files)
        #endregion

        void CentreOn(float mx, float my)
        {
            viewX = mx - Width / (2 * scale);
            viewY = my - Height / (2 * scale);
            Clamp();
        }

        // Panning stops at the edge of the sky, and a side that is smaller than the
        // panel is centred instead.
        void Clamp()
        {
            MeasureContent();
            float seenW = Width / scale, seenH = Height / scale;
            if (seenW >= content.Width) viewX = content.X + (content.Width - seenW) / 2;
            else viewX = Math.Max(content.X, Math.Min(content.Right - seenW, viewX));
            if (seenH >= content.Height) viewY = content.Y + (content.Height - seenH) / 2;
            else viewY = Math.Max(content.Y, Math.Min(content.Bottom - seenH, viewY));
        }

        float ToViewX(float mx) { return (mx - viewX) * scale; }
        float ToViewY(float my) { return (my - viewY) * scale; }

        RectangleF Box(float mx, float my, float w, float h)
        {
            return new RectangleF(ToViewX(mx), ToViewY(my), w * scale, h * scale);
        }

        public void ZoomBy(float factor) { ZoomAt(factor, new Point(Width / 2, Height / 2)); }

        void ZoomAt(float factor, Point at)
        {
            float min = FitScale(), max = 1f;
            float want = Math.Max(min, Math.Min(max, scale * factor));
            if (Math.Abs(want - scale) < 1e-6f) return;
            // The point under the cursor stays under the cursor.
            float mx = viewX + at.X / scale, my = viewY + at.Y / scale;
            scale = want;
            viewX = mx - at.X / scale;
            viewY = my - at.Y / scale;
            Clamp();
            Invalidate();
            EventHandler h = ZoomChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public event EventHandler ZoomChanged;

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!placed) { Fit(); return; }
            if (scale < FitScale()) scale = FitScale();
            Clamp();
        }

        // ------------------------------------------------------------- pictures

        // The records name their pictures as `.png` in places and `.tex` in others,
        // the archives hold `.tex` — the same two-step the skill tree does.
        Image Texture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Image got = game.SkillIcon(path);
            if (got != null) return got;
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                 ? game.SkillIcon(path.Substring(0, path.Length - 4) + ".tex") : null;
        }

        Image StarArt(DevotionStar star, string field)
        {
            DbRecord ui = game.Record(star.UiDbr);
            return ui == null ? null : Texture(ui.Get(field));
        }

        // ------------------------------------------------------------- painting

        // What state a constellation is in, which decides both the tint of its
        // picture and whether its stars can be clicked.
        enum State { Locked, Open, Partial, Complete }

        State StateOf(Constellation k)
        {
            if (k.Stars.Count == 0) return State.Open;
            // Closed by affinity is closed, whatever is inside — the same order the
            // game asks in: the gate first, the stars after.
            if (!DevotionEdit.Unlocked(game, affinity, k)) return State.Locked;
            int mine = 0;
            foreach (string star in k.Stars) if (taken.ContainsKey(star)) mine++;
            if (mine >= k.Stars.Count) return State.Complete;
            return mine > 0 ? State.Partial : State.Open;
        }

        static ImageAttributes Multiply(Tint t)
        {
            ColorMatrix m = new ColorMatrix();
            m.Matrix00 = t.R; m.Matrix11 = t.G; m.Matrix22 = t.B; m.Matrix33 = t.A;
            ImageAttributes a = new ImageAttributes();
            a.SetColorMatrix(m);
            return a;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!placed) Fit();
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            PaintSkyTiles(g);
            PaintNebulae(g);
            PaintConstellations(g);
            PaintLinks(g);
            PaintStars(g);
            PaintPlate(g);
        }

        // The star field: one tile repeated over the whole sky. Only the tiles the
        // panel can see are drawn.
        //
        // NEIGHBOURING TILES ARE MADE TO SHARE THEIR EDGE, rather than each being
        // rounded on its own: a tile 1024 wide at nine per cent is 92.16 pixels, and
        // rounding every one of them separately leaves a hairline of background
        // between them — a grid of seams right across the sky.
        void PaintSkyTiles(Graphics g)
        {
            Image tile = Texture(sky.TilePath);
            if (tile == null) return;
            int w = tile.Width, h = tile.Height;
            if (w <= 0 || h <= 0) return;
            float right = viewX + Width / scale, bottom = viewY + Height / scale;
            int firstX = (int)Math.Floor((Math.Max(viewX, MapLeft) - MapLeft) / (double)w);
            int firstY = (int)Math.Floor((Math.Max(viewY, MapTop) - MapTop) / (double)h);
            for (int ty = firstY; MapTop + ty * h < Math.Min(bottom, MapTop + sky.SizeY); ty++)
            {
                int y0 = (int)Math.Round(ToViewY(MapTop + ty * h));
                int y1 = (int)Math.Round(ToViewY(MapTop + (ty + 1) * h));
                for (int tx = firstX; MapLeft + tx * w < Math.Min(right, MapLeft + sky.SizeX); tx++)
                {
                    int x0 = (int)Math.Round(ToViewX(MapLeft + tx * w));
                    int x1 = (int)Math.Round(ToViewX(MapLeft + (tx + 1) * w));
                    g.DrawImage(tile, new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)),
                                0, 0, w, h, GraphicsUnit.Pixel, null);
                }
            }
        }

        void PaintNebulae(Graphics g)
        {
            for (int i = 0; i < sky.NebulaPaths.Count; i++)
            {
                Image art = Texture(sky.NebulaPaths[i]);
                if (art == null) continue;
                RectangleF box = Box(sky.NebulaAt[i].X, sky.NebulaAt[i].Y, art.Width, art.Height);
                if (!Seen(box)) continue;
                g.DrawImage(art, box);
            }
        }

        bool Seen(RectangleF box)
        {
            return box.Right >= -2 && box.Bottom >= -2 && box.Left <= Width + 2 && box.Top <= Height + 2;
        }

        void PaintConstellations(Graphics g)
        {
            foreach (Constellation k in all)
            {
                Image art = Texture(k.ArtPath);
                if (art == null) continue;
                RectangleF box = Box(k.ArtX, k.ArtY, art.Width, art.Height);
                if (!Seen(box)) continue;
                State state = StateOf(k);
                Tint tint = state == State.Complete ? k.Active
                          : state == State.Locked ? k.Unavailable
                          : k.Available;
                using (ImageAttributes at = Multiply(tint))
                    g.DrawImage(art, Rect(box), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, at);
            }
        }

        static Rectangle Rect(RectangleF box)
        {
            return new Rectangle((int)Math.Round(box.X), (int)Math.Round(box.Y),
                                 Math.Max(1, (int)Math.Round(box.Width)), Math.Max(1, (int)Math.Round(box.Height)));
        }

        // THE LINE BETWEEN TWO STARS. The texture is a cross-section — it is the
        // same all along its length and only changes across it — so it is stretched
        // from one star's centre to the other's and drawn `connectionWidth` thick.
        void PaintLinks(Graphics g)
        {
            foreach (Constellation k in all)
            {
                if (k.Buttons.Count == 0) continue;
                State state = StateOf(k);
                foreach (DevotionStar star in k.Buttons)
                {
                    DevotionStar parent = DevotionEdit.ParentOf(star);
                    if (parent == null) continue;
                    bool lit = taken.ContainsKey(star.Dbr) && taken.ContainsKey(parent.Dbr);
                    Image art = Texture(lit ? sky.LineOn
                                            : state == State.Locked ? sky.LineLocked : sky.LineOff);
                    if (art == null) continue;
                    PointF a = Centre(star), b = Centre(parent);
                    if (!Seen(RectangleF.FromLTRB(Math.Min(a.X, b.X) - 4, Math.Min(a.Y, b.Y) - 4,
                                                  Math.Max(a.X, b.X) + 4, Math.Max(a.Y, b.Y) + 4))) continue;
                    float dx = b.X - a.X, dy = b.Y - a.Y;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (len < 0.5f) continue;
                    float thick = Math.Max(1f, sky.LineWidth * scale);
                    GraphicsState keep = g.Save();
                    g.TranslateTransform(a.X, a.Y);
                    g.RotateTransform((float)(Math.Atan2(dy, dx) * 180 / Math.PI));
                    g.DrawImage(art, new RectangleF(0, -thick / 2f, len, thick));
                    g.Restore(keep);
                }
            }
        }

        PointF Centre(DevotionStar star)
        {
            return new PointF(ToViewX(star.X + StarSize / 2f), ToViewY(star.Y + StarSize / 2f));
        }

        void PaintStars(Graphics g)
        {
            Image empty = Texture(sky.EmptyIcon);
            Image glow = Texture(sky.PowerGlow);
            foreach (Constellation k in all)
            {
                if (k.Buttons.Count == 0) continue;
                State state = StateOf(k);
                foreach (DevotionStar star in k.Buttons)
                {
                    RectangleF box = Box(star.X, star.Y, StarSize, StarSize);
                    if (!Seen(box)) continue;
                    bool mine = taken.ContainsKey(star.Dbr);
                    bool open = state != State.Locked &&
                                (DevotionEdit.ParentOf(star) == null ||
                                 taken.ContainsKey(DevotionEdit.ParentOf(star).Dbr));

                    // The glow behind a star that carries a celestial power, at the
                    // offset the record gives it.
                    if (star.Power && glow != null && (mine || open))
                        g.DrawImage(glow, Box(star.X + sky.PowerGlowX, star.Y + sky.PowerGlowY,
                                              glow.Width, glow.Height));

                    // FOUR STATES, FOUR PICTURES — AND ALL FOUR ARE THE GAME'S.
                    // A star button's record carries exactly the four the game uses,
                    // and the mapping is the plain one a button has:
                    //
                    //     bitmapNameDown       GOLDEN — the star is taken
                    //     bitmapNameInFocus    under the cursor
                    //     bitmapNameUp         white-blue: can be taken now
                    //     bitmapNameDisabled   dim: the constellation is still closed
                    //
                    // The first pass had this wrong: `Up` was used for a taken star
                    // and a faded `Up` for one within reach, so nothing was gold and
                    // the two states looked the same. Noticed by the user against the
                    // game and GrimTools, where a taken star is gold and the
                    // white-blue one is the next star that can be taken. Their own
                    // code says the same thing (`ic()`: selected / hover / available /
                    // unavailable), and selected wins over hover — so a taken star
                    // stays gold under the cursor.
                    Image art = StarArt(star, mine ? "bitmapNameDown"
                                            : star == hovered ? "bitmapNameInFocus"
                                            : open ? "bitmapNameUp" : "bitmapNameDisabled");
                    if (art == null) art = StarArt(star, "bitmapNameUp");
                    if (art != null)
                        g.DrawImage(art, Rect(box), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, null);

                    // A celestial power shows the icon of the class skill it is bound
                    // to, and the game's "?" until it is bound to anything.
                    if (star.Power)
                    {
                        string owner;
                        Image icon = boundTo.TryGetValue(star.Dbr, out owner)
                                   ? Texture(SkillText.IconOf(game, owner)) : null;
                        if (icon == null) icon = empty;
                        float inset = (StarSize - IconSize) / 2f;
                        if (icon != null) g.DrawImage(icon, Box(star.X + inset, star.Y + inset, IconSize, IconSize));
                        string ring = mine ? sky.BorderActive : open ? sky.BorderAvailable : sky.BorderPlain;
                        Image border = Texture(ring);
                        float ringInset = (StarSize - RingSize) / 2f;
                        if (border != null)
                            g.DrawImage(border, Box(star.X + ringInset, star.Y + ringInset, RingSize, RingSize));
                    }

                    if (star == hovered)
                        using (Pen p = new Pen(Color.FromArgb(200, 218, 165, 32), 2f))
                            g.DrawEllipse(p, Rect(box));
                }
            }
        }

        // The five affinities, in the order the game lists them.
        static readonly string[] AffinityKeys = { "Ascendant", "Chaos", "Eldritch", "Order", "Primordial" };

        // THE PLATE: how many devotion points are left, and the affinity as the game
        // shows it — a row of its own coloured balls with a figure under each, not
        // five lines of coloured words. The heading is the game's own
        // (the localized affinity label, `tagDevotionAffinity`).
        void PaintPlate(Graphics g)
        {
            const int Orb = 26, Step = 34;
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (Font small = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (SolidBrush back = new SolidBrush(Color.FromArgb(170, 10, 10, 14)))
            using (StringFormat mid = new StringFormat { Alignment = StringAlignment.Center })
            {
                int width = 16 + AffinityKeys.Length * Step;
                g.FillRectangle(back, 0, 0, Math.Max(190, width), 72);
                using (SolidBrush b = new SolidBrush(character.DevotionPoints > 0
                                                     ? Color.FromArgb(235, 235, 235) : Color.FromArgb(255, 66, 0)))
                    g.DrawString(Lang.T("skills.devo.points", character.DevotionPoints), f, b, 8, 3);
                int x = 8;
                foreach (string key in AffinityKeys)
                {
                    int have;
                    affinity.TryGetValue(key, out have);
                    Image orb = Texture(DevotionEdit.AffinityIcon(game, key));
                    if (orb != null) g.DrawImage(orb, x + (Step - Orb) / 2, 24, Orb, Orb);
                    using (SolidBrush b = new SolidBrush(have > 0 ? Color.FromArgb(240, 235, 215)
                                                                  : Color.FromArgb(120, 120, 120)))
                        g.DrawString(have.ToString(), small, b, new RectangleF(x, 50, Step, 18), mid);
                    x += Step;
                }
            }
        }

        // ---------------------------------------------------------------- mouse

        public DevotionStar HitTest(Point p)
        {
            // Back to front, so the star drawn last is the one picked.
            for (int i = all.Count - 1; i >= 0; i--)
                for (int j = all[i].Buttons.Count - 1; j >= 0; j--)
                {
                    DevotionStar star = all[i].Buttons[j];
                    RectangleF box = Box(star.X, star.Y, StarSize, StarSize);
                    // A star drawn three pixels across still has to be clickable.
                    if (box.Width < 12) box.Inflate((12 - box.Width) / 2, (12 - box.Height) / 2);
                    if (box.Contains(p)) return star;
                }
            return null;
        }

        public event EventHandler<DevotionHoverEventArgs> Hovered;
        public event EventHandler<DevotionClickEventArgs> Clicked;

        // A click without a mouse, for a headless run: it goes through the same
        // handler the window hung on `Clicked`, so what is checked is the whole
        // chain — the rules, the save, the map reading itself again and the sums
        // beside it — and not only the rules.
        public void Press(DevotionStar star, MouseButtons button)
        {
            EventHandler<DevotionClickEventArgs> h = Clicked;
            if (h != null && star != null) h(this, new DevotionClickEventArgs(star, button, Point.Empty));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            DevotionStar star = HitTest(e.Location);
            if (star != null)
            {
                EventHandler<DevotionClickEventArgs> h = Clicked;
                if (h != null) h(this, new DevotionClickEventArgs(star, e.Button, PointToScreen(e.Location)));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragFrom = e.Location;
            dragViewX = viewX;
            dragViewY = viewY;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!Focused && CanFocus) Focus();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                viewX = dragViewX - (e.X - dragFrom.X) / scale;
                viewY = dragViewY - (e.Y - dragFrom.Y) / scale;
                Clamp();
                Invalidate();
                return;
            }
            DevotionStar star = HitTest(e.Location);
            if (star == hovered) return;
            hovered = star;
            Invalidate();
            EventHandler<DevotionHoverEventArgs> h = Hovered;
            if (h != null) h(this, new DevotionHoverEventArgs(star, PointToScreen(e.Location)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            dragging = false;
            if (hovered == null) return;
            hovered = null;
            Invalidate();
            EventHandler<DevotionHoverEventArgs> h = Hovered;
            if (h != null) h(this, new DevotionHoverEventArgs(null, Point.Empty));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.Delta > 0 ? 1.25f : 1 / 1.25f, e.Location);
        }
    }
}
