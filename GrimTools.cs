// READING A GRIMTOOLS BUILD: what sits behind a link like
// `https://www.grimtools.com/calc/2ELApkPN`, and how it is matched to the game's
// own records.
//
// The whole write-up is MECHANICS 106. The short of it, so that nothing here needs
// looking up:
//
// 1. THE BUILD ITSELF IS IN THE PAGE'S HTML, one line of it, and no sign-in:
//        <script>var isMobile=0;window['buildInfo'] = {"data":{"bio":{…},"equipment":{…},…}};
//    The page is 37 KB. Inside: level and attributes, 16 equipment slots (item,
//    prefix, suffix, ascension, component, augment, relic bonus), skill levels, the
//    devotion stars taken, and which skill each celestial power hangs on.
//
// 2. THINGS ARE NAMED BY THEIR OWN NUMBERS (`it14666`, `pre9636`, `sk1277`), not by
//    record paths. Those numbers are spelled out by their own database,
//    `/db/itemdb/itemdb.js` — 8.7 MB of text, 1.2 MB over the wire. It holds the
//    game's objects under THE GAME'S OWN field names; only some of the names are
//    shortened, by the table `window.shortNameMapping` (`itemNameTag:"a"`,
//    `lootRandomizerName:"c"`, `bitmap:"n"`, `levelRequirement:"k"`, `Class:"l"`).
//
// 3. THE MATCHING GOES BY TAGS AND PICTURES, NOT BY NAMES: a name follows the
//    language, while `itemNameTag`, `lootRandomizerName`, `skillDisplayName`,
//    `bitmapName` and `skillUpBitmapName` are the game's own fields and do not.
//    One picture is not enough: several records may share a
//    picture and differing only in the level they ask for. The key is the tag PLUS
//    the picture PLUS the level requirement.
//
// 4. THE BUILD HOLDS NO ROLLS AT ALL: GrimTools has no notion of a seed. We put the
//    very same item in (the record, the affixes, the component, the augment — the
//    save keeps all of them as record paths), but the numbers inside the ranges are
//    ours. The seed is set to zero, as the user asked; any item can then be turned
//    to taste in the edit window.
//
// THE NETWORK. This is the only place in the editor that goes outside, and it only
// goes when asked: on the import button, or on `--gtimport`.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Gdc.Gui
{
    // A FETCH THAT WENT WRONG, AND WHICH WAY IT WENT WRONG.
    //
    // A generic fetch error tells the user nothing they can do. Three different things
    // go wrong here and each has its own answer: there is no network at all, the page
    // did not open (a mistyped code, their site down), or the page opened and holds no
    // build. So the kind travels as a KEY INTO THE LANGUAGE FILES — the window looks
    // the wording up by it, because nothing the user reads may be worded in code.
    // `Message` stays Russian for the check's report, the same as every other report.
    public sealed class GtFetchError : Exception
    {
        public readonly string Key;
        public readonly string Detail;

        public GtFetchError(string key, string report, string detail)
            : base(report)
        {
            Key = key;
            Detail = detail ?? "";
        }
    }

    // One record of their database, boiled down to what hooks it to the game.
    public sealed class GtEntry
    {
        public string Id = "";
        public string NameTag = "";       // itemNameTag — or `description`, for components and relics
        public string LootName = "";      // lootRandomizerName — what an affix is called
        public string Bitmap = "";        // bitmap (.png) — the game calls it bitmapName (.tex)
        public string RecordClass = "";   // Class
        public string Rarity = "";        // itemClassification
        public int Level;                 // levelRequirement
        public int Jitter;                // lootRandomizerJitter — tells like-named affixes apart
        public string SkillTag = "";      // skillDisplayName
        public string SkillIcon = "";     // skillUpBitmapName
        // FOR A DEVOTION STAR — WHICH CONSTELLATION HOLDS IT, said by what that
        // constellation gives, asks and how many stars it has. Not on the star's own
        // record on either side; read from their table of constellations. See
        // `ReadConstellations`.
        public string Home = "";
        // And WHICH BUTTON of that constellation it is (`devotionButtonN`). Two stars
        // of one displayed object may be the same record word for word, so when the
        // numbers agree for both, this is all that is left.
        public int HomeButton;
        public string BuffSkill = "";     // buffSkillName — a shell record hands off along this

        // AN ASCENSION HAS NO NAME AT ALL — neither theirs nor ours (checked over all
        // 1181 of the game's `lootaffixes/ascended` records: not one carries
        // `lootRandomizerName`). It is known by what it does, so these four fields and
        // `Sig` below are what it is matched by.
        public string ModifiedSkill = "";   // modifiedSkillName1 — a TAG on their side, a path on ours
        public string ModifierSkill = "";   // modifierSkillName1 — their number; the record that does the work
        public string AugmentSkill = "";    // augmentSkillName1 — likewise a tag on their side
        public string AugmentSkill2 = "";   // augmentSkillName2
        public int AugmentLevel;            // augmentSkillLevel1

        // Every plain number the record carries, as `field=value` sorted by field.
        // Both sides are built the same way, so two records that do the same thing
        // come out with the same string. Only filled for the tables that need it.
        public string Sig = "";
    }

    public sealed class GtDb
    {
        public string Stamp = "";
        // The game the table's own item signatures were counted from (MECHANICS 116).
        public string GameSignature = "";
        public readonly Dictionary<string, GtEntry> Items = New();
        public readonly Dictionary<string, GtEntry> Prefixes = New();
        public readonly Dictionary<string, GtEntry> Suffixes = New();
        public readonly Dictionary<string, GtEntry> Ascended = New();
        public readonly Dictionary<string, GtEntry> RelicBonuses = New();
        public readonly Dictionary<string, GtEntry> Skills = New();

        static Dictionary<string, GtEntry> New()
        { return new Dictionary<string, GtEntry>(StringComparer.Ordinal); }

        public int Count
        {
            get
            {
                return Items.Count + Prefixes.Count + Suffixes.Count +
                       Ascended.Count + RelicBonuses.Count + Skills.Count;
            }
        }

        public GtEntry Find(string id)
        {
            GtEntry got;
            if (string.IsNullOrEmpty(id)) return null;
            if (Items.TryGetValue(id, out got)) return got;
            if (Prefixes.TryGetValue(id, out got)) return got;
            if (Suffixes.TryGetValue(id, out got)) return got;
            if (Ascended.TryGetValue(id, out got)) return got;
            if (RelicBonuses.TryGetValue(id, out got)) return got;
            if (Skills.TryGetValue(id, out got)) return got;
            return null;
        }
    }

    public static class GrimTools
    {
        public const string Host = "https://www.grimtools.com";

        // The build's code out of the link. A bare code is taken as well as a full
        // address with a language in the path (`/calc/ru/XYZ`) — that is how their
        // own "add build" window reads it.
        public static string BuildId(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string s = text.Trim();
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            s = s.TrimEnd('/');
            int slash = s.LastIndexOf('/');
            string tail = slash < 0 ? s : s.Substring(slash + 1);
            foreach (char c in tail)
                if (!char.IsLetterOrDigit(c)) return null;
            return tail.Length >= 4 ? tail : null;
        }

        // ------------------------------------------------------------------ network

        static HttpClient client;

        static HttpClient Client()
        {
            if (client != null) return client;
            client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            // Without a name of some sort a number of sites answer with a refusal;
            // we give our own and do not pretend to be anyone else.
            client.DefaultRequestHeaders.Add("User-Agent", "gdc-gui (Grim Dawn save editor)");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/javascript,*/*");
            return client;
        }

        public static string Get(string url) { return Get(url, null); }

        // HOW LONG WE WAIT FOR AN ANSWER — which is not the same as how long a download may
        // take, and the difference is the whole point.
        //
        // The client's own timeout is a minute, and for the TRANSFER that is right: their
        // item database is eight megabytes of text, and a slow line has every right to take
        // its time. For the ANSWER it is wrong. With the site down or no internet at all the
        // the window may appear hung for the whole minute. Keep the response-header
        // deadline short while allowing the body download to use the normal timeout.
        //
        // So the deadline is put on the part that stalls — finding the host and getting the
        // answer's headers — and the body is left alone: it comes with a progress bar and
        // does not look hung. Fifteen seconds, because a page of theirs that answers at all
        // answers in well under a second.
        public const int AnswerSeconds = 15;

        // THE DOWNLOAD IS READ IN PIECES SO THAT SOMEBODY CAN BE TOLD HOW IT IS GOING.
        // Their item database is eight megabytes of text, a megabyte and a bit over
        // the wire, and a window that says one line and then sits still for half a
        // minute looks exactly like a window that has hung — which is what the user
        // reported (PLAN, item 2g). `onProgress` is handed the bytes read and the
        // whole size when the answer names one, or −1 when it does not.
        public static string Get(string url, Action<long, long> onProgress)
        { return Get(url, onProgress, AnswerSeconds); }

        // `seconds` — a deadline for the ANSWER to this request; zero leaves the client's
        // own. Because the answer is asked for with `ResponseHeadersRead`, the deadline
        // covers finding the host and getting the headers — the part that stalls when the
        // site is unreachable — and stops before the body, which is read below with its own
        // progress shown.
        public static string Get(string url, Action<long, long> onProgress, int seconds)
        {
            CancellationTokenSource stop = null;
            try
            {
                CancellationToken token = CancellationToken.None;
                if (seconds > 0)
                { stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); token = stop.Token; }
                HttpResponseMessage r = Client()
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
                if (!r.IsSuccessStatusCode)
                    throw new GtFetchError("import.err.page",
                                           "Could not open the page: " + (int)r.StatusCode + " " + r.ReasonPhrase,
                                           ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture));
                long total = r.Content.Headers.ContentLength.HasValue
                           ? r.Content.Headers.ContentLength.Value : -1;
                using (Stream stream = r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (MemoryStream into = new MemoryStream())
                {
                    byte[] buffer = new byte[64 * 1024];
                    long done = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        into.Write(buffer, 0, read);
                        done += read;
                        if (onProgress != null) onProgress(done, total);
                    }
                    // Their pages and their two databases are all UTF-8, and they say
                    // so in the answer's own header.
                    return new UTF8Encoding(false).GetString(into.ToArray());
                }
            }
            catch (GtFetchError) { throw; }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // OUR OWN DEADLINE AND THE CLIENT'S LOOK THE SAME FROM HERE, and the person
                // needs the same sentence either way: the site did not answer, look at the
                // connection. The seconds are named so that the line does not read as
                // "somewhere between a moment and forever".
                throw new GtFetchError("import.err.timeout",
                                       "The site did not respond within " + AnswerSeconds + " seconds.", null);
            }
            catch (OperationCanceledException)
            {
                throw new GtFetchError("import.err.timeout",
                                       "The site did not respond within " + AnswerSeconds + " seconds.", null);
            }
            catch (HttpRequestException e)
            {
                throw new GtFetchError("import.err.network", "Could not connect to the site: " + e.Message, null);
            }
            catch (IOException e)
            {
                throw new GtFetchError("import.err.network", "The connection was interrupted: " + e.Message, null);
            }
            finally { if (stop != null) stop.Dispose(); }
        }

        // ---------------------------------------------------- the build's page

        // `window['buildInfo'] = {…};` — exactly one such piece on the page, and it
        // is strict JSON, so an ordinary reader takes it from there.
        //
        // THE SITE STOPPED PUTTING IT THERE IN OCTOBER 2026 (MECHANICS 116). The page now
        // carries a short-lived signed token instead, and the data comes in a second
        // request. Both ways are kept: saved pages (the battery's own
        // `reference/grimtools/build-….html`) still hold the inline piece, and a live
        // page no longer does.
        public static string BuildJsonOf(string html)
        {
            int at = html.IndexOf("buildInfo", StringComparison.Ordinal);
            if (at < 0) return null;
            int brace = html.IndexOf('{', at);
            if (brace < 0) return null;
            int end = MatchBrace(html, brace);
            return end < 0 ? null : html.Substring(brace, end - brace + 1);
        }

        // `window['_calcDataToken']="eyJiIjoi…";` — the page's key to its own build data.
        //
        // It decodes (the first dot-separated piece is plain base64) to
        // `{"b":"V7pMDxJZ","m":"","exp":1791564459}` — the build's id, the mod, and when
        // the key stops working. SHORT-LIVED: asking with a token taken minutes earlier
        // answers `403 {"error":"not_allowed"}`, which is what makes this look like a
        // refusal rather than an expiry. Neither cookies nor a Referer are needed —
        // checked all four combinations.
        public static string DataTokenOf(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            int at = html.IndexOf("_calcDataToken", StringComparison.Ordinal);
            if (at < 0) return null;
            int q = html.IndexOf('"', at);
            if (q < 0) return null;
            int end = html.IndexOf('"', q + 1);
            return end < 0 ? null : html.Substring(q + 1, end - q - 1);
        }

        public static string DataUrlOf(string token)
        { return Host + "/load_calc_data.php?t=" + Uri.EscapeDataString(token ?? ""); }

        // THE BUILD'S JSON, whichever way the page offers it. Taken straight from the
        // page when it is there (a saved page, an older one), otherwise fetched with the
        // token. The answer has the same shape either way — `{"data":{…},"masteries":[…],
        // "created_for_build":"1.3.0.8"}` — so nothing downstream had to change.
        public static string BuildJsonOf(string html, Func<string, string> fetch)
        {
            string inline = BuildJsonOf(html);
            if (inline != null) return inline;
            string token = DataTokenOf(html);
            if (token == null || fetch == null) return null;
            string json = fetch(DataUrlOf(token));
            return string.IsNullOrEmpty(json) ? null : json;
        }

        // The version of their database, as the page itself names it:
        // `/db/itemdb/itemdb.js?1789115170`. The decoded table is cached under that
        // stamp — they change the database, the stamp changes with it.
        //
        // THERE ARE TWO DATABASES, AND THE SECOND IS NOT WHERE ONE WOULD LOOK: items,
        // affixes and relic bonuses sit in `itemdb.js`, while the SKILLS
        // (`window.eSkills`) sit in `calc/calc.js` itself. Both carry the same stamp.
        public static string DbUrlOf(string html) { return UrlOf(html, "/db/itemdb/itemdb.js"); }

        public static string CalcUrlOf(string html) { return UrlOf(html, "/calc/calc.js"); }

        static string UrlOf(string html, string path)
        {
            int at = html.IndexOf(path, StringComparison.Ordinal);
            if (at < 0) return Host + path;
            int end = at;
            while (end < html.Length && "\"'<> \t\r\n)".IndexOf(html[end]) < 0) end++;
            return Host + html.Substring(at, end - at);
        }

        public static string StampOf(string url)
        {
            int q = url == null ? -1 : url.IndexOf('?');
            return q < 0 ? "" : url.Substring(q + 1);
        }

        static int MatchBrace(string s, int start)
        {
            int depth = 0;
            bool inString = false;
            char quote = '"';
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // ------------------------------------------------- their own database

        // The field names their table shortens: code → the game's real name.
        static Dictionary<string, string> ShortNames(string js)
        {
            Dictionary<string, string> made = new Dictionary<string, string>(StringComparer.Ordinal);
            int at = js.IndexOf("window.shortNameMapping=", StringComparison.Ordinal);
            if (at < 0) return made;
            int brace = js.IndexOf('{', at);
            int end = MatchBrace(js, brace);
            if (brace < 0 || end < 0) return made;
            // Pairs such as `itemNameTag:"a"`; the full name is on the left.
            int i = brace + 1;
            while (i < end)
            {
                while (i < end && (js[i] == ',' || js[i] == '\n' || js[i] == ' ')) i++;
                int keyStart = i;
                while (i < end && js[i] != ':') i++;
                if (i >= end) break;
                string key = js.Substring(keyStart, i - keyStart).Trim().Trim('"');
                i++;
                if (i < end && js[i] == '"')
                {
                    int vs = ++i;
                    while (i < end && js[i] != '"') i++;
                    made[js.Substring(vs, i - vs)] = key;
                    i++;
                }
                else while (i < end && js[i] != ',') i++;
            }
            return made;
        }

        // Their whole database, boiled down to what the matching needs. `itemdb` is
        // the file of items, `calc` is `calc.js`, where the skills live; the second
        // one may be left out.
        public static GtDb ParseDb(string itemdb, string calc, string stamp)
        {
            GtDb db = new GtDb();
            db.Stamp = stamp ?? "";
            Dictionary<string, string> real = ShortNames(itemdb);
            Read(itemdb, "allItems", real, db.Items);
            // EVERY TABLE BUT THE ITEMS KEEPS ITS NUMBERS. An item is known by its
            // name tag and its picture, but an affix often is not: two suffixes called
            // `tagSuffixA008`, both level 90, both Magical, differ only in being +830
            // and +1220 health. And an ascension has no name on either side at all.
            // The items are left out because there are 8612 of them and the signature
            // would only make the cache fat.
            //
            // THE SKILLS NEED IT JUST AS BADLY, and that showed itself late: an
            // ORDINARY DEVOTION STAR is named by the tag of its CONSTELLATION, one tag
            // for all its stars, and every star of the game wears the same picture. So
            // the tag and the picture together still leave four or five candidates that
            // nothing tells apart, the first of them wins, and a build's fifty stars
        // come out as thirteen (PLAN, item 2a). The numbers do tell them apart —
            // one star is +25 physique, its neighbour is +40 offensive ability.
            signing = true;
            Read(itemdb, "prefixes", real, db.Prefixes);
            Read(itemdb, "suffixes", real, db.Suffixes);
            // The ascensions live under `ascensionAffixData`; `ascensionAffixes` is
            // only the lists of which item may carry which.
            Read(itemdb, "ascensionAffixData", real, db.Ascended);
            Read(itemdb, "bonusTableAffixes", real, db.RelicBonuses);
            // THE SKILLS ARE IN THREE HEAPS, AND TWO OF THEM IN A DIFFERENT FILE:
            // `eSkills` is in `calc.js`, while the buffs a shell skill hands off to
            // (`buffSkills`) and the modifiers gear grants (`itemSkills`) are in
            // `itemdb.js`. Without the buffs half the mastery skills did not match:
                // Some records contain only a `buffSkillName`.
            Read(itemdb, "buffSkills", real, db.Skills);
            Read(itemdb, "itemSkills", real, db.Skills);
            if (!string.IsNullOrEmpty(calc))
            {
                // AND THE BUFFS ARE IN BOTH FILES, WITH DIFFERENT CONTENTS. `itemdb.js`
                // holds 159 of them — the ones gear grants — and `calc.js` holds the
                // rest under `eBuffSkills`. A shell record may hand off to a buff record,
                // and that one is only in the second file.
                Read(calc, "eSkills", real, db.Skills);
                Read(calc, "eBuffSkills", real, db.Skills);
                Read(calc, "ePetSkills", real, db.Skills);
            }
            signing = false;
            ReadConstellations(calc, db);
            return db;
        }

        // ------------------------------------------- their table of constellations

        // WHY A STAR NEEDS TO KNOW WHICH CONSTELLATION HOLDS IT. Five constellations
        // of the game share one display name; all five carry ONE tag
        // (`tagDevotion_A15`), one picture, and one star apiece — and their NUMBERS
        // repeat as well: `tier1_15d` and `tier1_15e` are both +18 offensive ability,
        // `tier1_15b` and `tier1_15c` are both +5% health. So the tag, the picture and
        // the numbers together still leave two standing, the first was taken, and a
        // build asking for the ASCENDANT crossroads got the ELDRITCH one. The
        // character came out one affinity short and the constellation standing on that
        // affinity became illegal — found 2 October on the user's own build.
        //
        // WHAT TELLS THEM APART IS THE CONSTELLATION, and both sides hold it under the
        // game's own field names: `affinityGivenName1/affinityGiven1`,
        // `affinityRequiredName1/affinityRequired1`, and the list of its stars. Ours
        // come from the game's records (`DevotionTable`), theirs from the blocks
        // called `devotionConstellationNN` inside `window.masterTable` — written out
        // in full there, not shortened. The giving is stamped onto every star of the
        // constellation, and the two sides are then compared by it.
        static void ReadConstellations(string calc, GtDb db)
        {
            if (string.IsNullOrEmpty(calc)) return;
            const string Head = "devotionConstellation";
            int at = 0;
            while (true)
            {
                at = calc.IndexOf(Head, at, StringComparison.Ordinal);
                if (at < 0) return;
                int i = at + Head.Length;
                while (i < calc.Length && char.IsDigit(calc[i])) i++;
                if (i >= calc.Length || calc[i] != ':' || i + 1 >= calc.Length || calc[i + 1] != '{')
                { at = i; continue; }
                int end = MatchBrace(calc, i + 1);
                if (end < 0) { at = i; continue; }
                string block = calc.Substring(i + 1, end - i);
                SortedDictionary<int, string> stars = StarsOf(block);
                string home = HomeSignature(Affinities(block, "affinityGiven"),
                                            Affinities(block, "affinityRequired"), stars.Count);
                foreach (KeyValuePair<int, string> star in stars)
                {
                    GtEntry e;
                    if (!db.Skills.TryGetValue(star.Value, out e)) continue;
                    e.Home = home;
                    e.HomeButton = star.Key;
                }
                at = end + 1;
            }
        }

        // `affinityGivenName1:"Eldritch" … affinityGiven1:5` — up to four pairs, and
        // their order in the text is not fixed, so the answer is sorted.
        static SortedDictionary<string, int> Affinities(string block, string key)
        {
            SortedDictionary<string, int> made = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int n = 1; n <= 4; n++)
            {
                string name = Field(block, key + "Name" + n);
                string count = Field(block, key + n);
                int value;
                if (string.IsNullOrEmpty(name) || count == null ||
                    !int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) continue;
                made[name] = value;
            }
            return made;
        }

        // The same words on both sides, so that a constellation of theirs and one of
        // ours can be compared as they stand. The number of stars is in it because two
        // constellations CAN give and ask for the same thing.
        public static string HomeSignature(SortedDictionary<string, int> gives,
                                           SortedDictionary<string, int> needs, int stars)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("gives ").Append(Signature(Words(gives)));
            sb.Append(" | requires ").Append(Signature(Words(needs)));
            sb.Append(" | stars ").Append(stars);
            return sb.ToString();
        }

        // Old caches use localized labels for these same three ordered fields.
        static string ReadHomeSignature(string value)
        {
            string[] parts = value.Split(" | ", StringSplitOptions.None);
            if (parts.Length != 3) return value;
            return "gives " + parts[0].Substring(parts[0].IndexOf(' ') + 1) +
                   " | requires " + parts[1].Substring(parts[1].IndexOf(' ') + 1) +
                   " | stars " + parts[2].Substring(parts[2].IndexOf(' ') + 1);
        }

        static SortedDictionary<string, string> Words(SortedDictionary<string, int> of)
        {
            SortedDictionary<string, string> made = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in of)
                made[kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            return made;
        }

        // The stars of one constellation: `devotionButtonN:{…}`, and inside each the
        // first number of theirs that looks like a skill. Which key holds it is not
        // asked after — a picture's path never looks like `sk739`.
        //
        // THE NUMBER N IS READ, NOT COUNTED. Their file writes an object's fields in
        // no particular order, so the third `devotionButton` met in the text is not
        // the third button; and the game itself leaves gaps in the numbering.
        static SortedDictionary<int, string> StarsOf(string block)
        {
            SortedDictionary<int, string> made = new SortedDictionary<int, string>();
            const string Head = "devotionButton";
            int at = 0;
            while (true)
            {
                at = block.IndexOf(Head, at, StringComparison.Ordinal);
                if (at < 0) return made;
                int i = at + Head.Length;
                int digits = i;
                while (i < block.Length && char.IsDigit(block[i])) i++;
                if (i >= block.Length || block[i] != ':' || i + 1 >= block.Length || block[i + 1] != '{')
                { at = i; continue; }
                int number;
                if (!int.TryParse(block.Substring(digits, i - digits), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out number)) { at = i; continue; }
                int end = MatchBrace(block, i + 1);
                if (end < 0) return made;
                int q = block.IndexOf("\"sk", i + 1, StringComparison.Ordinal);
                if (q > 0 && q < end)
                {
                    int shut = block.IndexOf('"', q + 1);
                    if (shut > 0 && shut < end) made[number] = block.Substring(q + 1, shut - q - 1);
                }
                at = end + 1;
            }
        }

        // One field of a block, as text. The value is run through by the same reader
        // the records use, so a string, a number and an object all come out whole.
        static string Field(string block, string key)
        {
            int at = block.IndexOf(key + ":", StringComparison.Ordinal);
            if (at < 0) return null;
            int i = at + key.Length + 1;
            int from = i;
            Skip(block, ref i, block.Length);
            return Text(block, from, i);
        }

        // `window.<name>={id:{…},id:{…}}` — one record at a time, and only the
        // identifying fields are kept from each. Everything else is skipped without
        // allocating anything: the file runs to eight-odd megabytes, and nearly all
        // of it is the numbers of skills and items, which we read from the game
        // itself anyway.
        static void Read(string js, string name, Dictionary<string, string> real,
                         Dictionary<string, GtEntry> into)
        {
            int at = js.IndexOf("window." + name + "=", StringComparison.Ordinal);
            if (at < 0) return;
            int i = js.IndexOf('{', at);
            int end = MatchBrace(js, i);
            if (i < 0 || end < 0) return;
            i++;
            while (i < end)
            {
                SkipSpace(js, ref i, end);
                if (i >= end) break;
                if (js[i] == ',') { i++; continue; }
                string id = Key(js, ref i, end);
                SkipSpace(js, ref i, end);
                if (i >= end || js[i] != ':') break;
                i++;
                SkipSpace(js, ref i, end);
                if (i >= end) break;
                if (js[i] != '{') { Skip(js, ref i, end); continue; }
                GtEntry e = new GtEntry();
                e.Id = id;
                ReadFields(js, ref i, end, real, e);
                if (!string.IsNullOrEmpty(id)) into[id] = e;
            }
        }

        // Whether the table being read needs a signature of its numbers.
        static bool signing;

        static void ReadFields(string js, ref int i, int end, Dictionary<string, string> real, GtEntry e)
        {
            int close = MatchBrace(js, i);
            if (close < 0 || close > end) { Skip(js, ref i, end); return; }
            int stop = close;
            i++;
            SortedDictionary<string, string> sig =
                signing ? new SortedDictionary<string, string>(StringComparer.Ordinal) : null;
            while (i < stop)
            {
                SkipSpace(js, ref i, stop);
                if (i >= stop) break;
                if (js[i] == ',') { i++; continue; }
                string key = Key(js, ref i, stop);
                SkipSpace(js, ref i, stop);
                if (i >= stop || js[i] != ':') break;
                i++;
                SkipSpace(js, ref i, stop);
                int valueAt = i;
                Skip(js, ref i, stop);
                string full;
                if (!real.TryGetValue(key, out full)) full = key;
                Take(js, valueAt, i, full, e, real);
                if (sig != null)
                {
                    string number = Numbers(js.Substring(valueAt, i - valueAt));
                    if (number != null && !AllZero(number) && !Structural(full)) sig[full] = number;
                }
            }
            if (sig != null) e.Sig = Signature(sig);
            i = close + 1;
        }

        // FIELDS THAT SAY WHERE A RECORD SITS, NOT WHAT IT DOES. They are left out of
        // the signature: the price of an affix and the attack speed band it belongs to
        // are the same for half the database, and the level it asks for is not the
        // same on both sides of an ascension.
        public static bool Structural(string field)
        {
            switch (field)
            {
                case "lootRandomizerCost":
                case "marketAdjustmentPercent":
                case "levelRequirement":
                case "lootRandomizerJitter":
                case "attributeScalePercent":
                case "characterBaseAttackSpeed":
                case "itemLevel":
                // A LABEL THE DESIGNERS LEFT IN THE RECORD, AND OFTEN A NUMBER: the
                // suffix `a018b_ch_oa_08` has `FileDescription 132` beside its
                // `characterOffensiveAbility 132`. Their database drops it, so a
                // signature that kept it agreed with nothing.
                case "FileDescription":
                    return true;
                default:
                    return false;
            }
        }

        // The value as numbers, or null when it is not one. A list comes back joined
        // with ';', the way the game's own records write it.
        public static string Numbers(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '[' && s[s.Length - 1] == ']') s = s.Substring(1, s.Length - 2);
            if (s.Length == 0) return null;
            StringBuilder sb = new StringBuilder();
            foreach (string part in s.Split(';', ','))
            {
                string one = part.Trim().Trim('"');
                double v;
                if (!double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // A FIELD THAT IS ALL ZEROES IS LEFT OUT, and that is not tidiness. The game's
        // records list every field they could ever have, most of them at zero, while
        // their database drops those on the way out — so a signature that kept them
        // would never agree with the other side.
        public static bool AllZero(string joined)
        {
            if (string.IsNullOrEmpty(joined)) return true;
            foreach (string part in joined.Split(';'))
            {
                double v;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
                if (v != 0) return false;
            }
            return true;
        }

        public static string Signature(SortedDictionary<string, string> fields)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in fields)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        static void Take(string js, int from, int to, string field, GtEntry e, Dictionary<string, string> real)
        {
            switch (field)
            {
                case "itemNameTag": e.NameTag = Text(js, from, to); break;
                case "description": if (e.NameTag.Length == 0) e.NameTag = Text(js, from, to); break;
                case "lootRandomizerName": e.LootName = Text(js, from, to); break;
                case "bitmap": case "bitmapName": e.Bitmap = Text(js, from, to); break;
                // NOT ONLY THE FIELD NAMES ARE SHORTENED, THE VALUES ARE TOO: the
                // record's class stands as a code (`c21`), and the same table spells
                // it out — `WeaponMelee_Axe:"c21"`. Until it was decoded the class
                // agreed for no item at all, and every match fell through to the
                // weakest key there is.
                case "Class":
                    {
                        string code = Text(js, from, to), full;
                        e.RecordClass = real.TryGetValue(code, out full) ? full : code;
                        break;
                    }
                case "itemClassification": e.Rarity = Text(js, from, to); break;
                case "levelRequirement": e.Level = Number(js, from, to); break;
                case "lootRandomizerJitter": e.Jitter = Number(js, from, to); break;
                case "skillDisplayName": e.SkillTag = Text(js, from, to); break;
                case "skillUpBitmapName": e.SkillIcon = Text(js, from, to); break;
                // WHERE A SHELL RECORD HANDS OFF TO — the same three fields, in the
                // same order, that `ItemViewFactory.NextSkillRecord` walks on our
                // side. A pet modifier of the tree is a record
                // with nothing but `petSkillName`, and without this step it had
                // neither a name nor an icon to be matched by.
                case "buffSkillName": e.BuffSkill = Text(js, from, to); break;
                case "petSkillName":
                case "skillBaseName":
                    if (e.BuffSkill.Length == 0) e.BuffSkill = Text(js, from, to);
                    break;
                case "modifiedSkillName1": e.ModifiedSkill = Text(js, from, to); break;
                case "modifierSkillName1": e.ModifierSkill = Text(js, from, to); break;
                case "augmentSkillName1": e.AugmentSkill = Text(js, from, to); break;
                case "augmentSkillName2": e.AugmentSkill2 = Text(js, from, to); break;
                case "augmentSkillLevel1": e.AugmentLevel = Number(js, from, to); break;
            }
        }

        static string Text(string js, int from, int to)
        {
            if (to <= from) return "";
            string s = js.Substring(from, to - from).Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
            return s;
        }

        static int Number(string js, int from, int to)
        {
            double v;
            return double.TryParse(Text(js, from, to), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                 ? (int)Math.Round(v) : 0;
        }

        static void SkipSpace(string js, ref int i, int end)
        {
            while (i < end && (js[i] == ' ' || js[i] == '\n' || js[i] == '\r' || js[i] == '\t')) i++;
        }

        // A key is either a bare name or a quoted string.
        static string Key(string js, ref int i, int end)
        {
            SkipSpace(js, ref i, end);
            if (i < end && js[i] == '"')
            {
                int s = ++i;
                while (i < end && js[i] != '"') i++;
                string got = js.Substring(s, i - s);
                if (i < end) i++;
                return got;
            }
            int start = i;
            while (i < end && js[i] != ':' && js[i] != ',' && js[i] != '}' && js[i] != ' ') i++;
            return js.Substring(start, i - start);
        }

        // Skip a value of any shape, up to the comma that ends it.
        //
        // IT IS NOT ALWAYS A VALUE — SOMETIMES IT IS AN EXPRESSION, and that cost a
        // day of wondering why half the skills would not match. Their file writes the
        // long trigger lists as
        //
        //     refreshCooldownTrigger:"OnEquip OnKill LowHealth …".split(" "),
        //
        // and a reader that stops at the closing quote leaves `.split(" ")` behind.
        // The next turn of the loop then takes `.split("` for a field name, finds no
        // colon after it and gives up on the record — so everything written after the
        // first such field was lost. `sk5412` came out with one number of its three,
        // and the ascensions matched to nothing.
        //
        // So the value is run through to the comma that ends it, counting brackets of
        // every kind and stepping over strings: an expression, an object and a list
        // all come out right.
        static void Skip(string js, ref int i, int end)
        {
            SkipSpace(js, ref i, end);
            int depth = 0;
            while (i < end)
            {
                char c = js[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < end)
                    {
                        if (js[i] == '\\') { i += 2; continue; }
                        if (js[i] == quote) { i++; break; }
                        i++;
                    }
                    continue;
                }
                if (c == '{' || c == '[' || c == '(') { depth++; i++; continue; }
                if (c == '}' || c == ']' || c == ')')
                {
                    if (depth == 0) return;
                    depth--;
                    i++;
                    continue;
                }
                if (c == ',' && depth == 0) return;
                i++;
            }
        }

        // ---------------------------------------------------------- the cache

        // `--gtcache=<path>` — read the decoded table from here instead of the
        // program's own folder. That is how the check does without a network: the
        // build's page and the table sit in `reference/grimtools/`, and the run
        // downloads nothing at all.
        public static string CacheOverride;

        public static string CachePath
        {
            get
            {
                return string.IsNullOrEmpty(CacheOverride) ? CacheFile.Of("grimtools") : CacheOverride;
            }
        }

        // THE NUMBER OF THE TABLE'S OWN SHAPE, and it is not a nicety: the columns
        // have changed twice already while the stamp of THEIR database stayed put, so
        // a table written by an older build was read as good and everything the new
        // columns carry came back empty. That cost all thirteen ascensions of a build
        // (PLAN, item 2b). Raise it whenever a column is added, moved or dropped.
        //
        //   1  twelve columns, no signatures (never numbered, rejected by this check)
        //   2  eighteen columns: the modified/augmented skills, the signature, the
        //      second augmented skill — and the signature now stands on the skills too
        //   3  nineteen: a star also carries the constellation that holds it
        //   4  twenty: and which button of that constellation it is
        // 5 (9 October): the header now carries THE GAME'S SIGNATURE as well. Their stamp
        // alone was not enough: the table also holds OUR item signatures (`SigOf`), counted
        // from the game's own records, and a patch moves those while their stamp stays put.
        // Until this the cache went on matching by numbers that no longer existed
        // (MECHANICS 116).
        public const int CacheFormat = 5;

        // The decoded table of their numbers sits beside the program, the same way
        // the item catalogue does. The stamp is the one standing in their own address
        // for the database: change the database and the stamp changes with it, and
        // the table is read again.
        // `gameDir` — the game the cache must have been built from. Empty means "do not
        // ask", which is what the battery's frozen reference copy needs (MECHANICS 116).
        public static GtDb LoadCache(string wantStamp, string gameDir)
        {
            GtDb db = LoadCache(wantStamp);
            if (db == null || string.IsNullOrEmpty(gameDir)) return db;
            return string.Equals(db.GameSignature ?? "", ItemCatalog.SignatureOf(gameDir),
                                 StringComparison.Ordinal) ? db : null;
        }

        public static GtDb LoadCache(string wantStamp)
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                string[] lines = File.ReadAllLines(CachePath, Encoding.UTF8);
                if (lines.Length == 0) return null;
                string[] head = lines[0].Split('\t');
                // `#gdc-grimtools <format> <their stamp>`. A header of the old shape —
                // two fields, the stamp straight after the name — has no format at all
                // and is thrown away whatever the stamp says.
                if (head.Length < 3 || head[0] != "#gdc-grimtools") return null;
                int format;
                if (!int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out format) ||
                    format != CacheFormat) return null;
                if (!string.IsNullOrEmpty(wantStamp) && head[2] != wantStamp) return null;
                GtDb db = new GtDb();
                db.Stamp = head[2];
                // Fourth field, since format 5: the game the signatures were counted from.
                db.GameSignature = head.Length > 3 ? head[3] : "";
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('\t');
                    if (p.Length < 10) continue;
                    GtEntry e = new GtEntry();
                    e.Id = p[1];
                    e.NameTag = p[2];
                    e.LootName = p[3];
                    e.Bitmap = p[4];
                    e.RecordClass = p[5];
                    e.Rarity = p[6];
                    int lvl;
                    int.TryParse(p[7], out lvl);
                    e.Level = lvl;
                    e.SkillTag = p[8];
                    e.SkillIcon = p[9];
                    if (p.Length > 10) e.BuffSkill = p[10];
                    int jit;
                    if (p.Length > 11 && int.TryParse(p[11], out jit)) e.Jitter = jit;
                    if (p.Length > 16)
                    {
                        e.ModifiedSkill = p[12];
                        e.ModifierSkill = p[13];
                        e.AugmentSkill = p[14];
                        int lvl2;
                        int.TryParse(p[15], out lvl2);
                        e.AugmentLevel = lvl2;
                        e.Sig = p[16];
                        if (p.Length > 17) e.AugmentSkill2 = p[17];
                        if (p.Length > 18) e.Home = ReadHomeSignature(p[18]);
                        int button;
                        if (p.Length > 19 && int.TryParse(p[19], out button)) e.HomeButton = button;
                    }
                    Bag(db, p[0])[e.Id] = e;
                }
                return db.Count > 0 ? db : null;
            }
            catch (Exception) { return null; }
        }

        public static void SaveCache(GtDb db)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("#gdc-grimtools\t").Append(CacheFormat).Append('\t').Append(db.Stamp)
              .Append('\t').Append(db.GameSignature ?? "").Append('\n');
            Dump(sb, "item", db.Items);
            Dump(sb, "prefix", db.Prefixes);
            Dump(sb, "suffix", db.Suffixes);
            Dump(sb, "ascended", db.Ascended);
            Dump(sb, "relicbonus", db.RelicBonuses);
            Dump(sb, "skill", db.Skills);
            File.WriteAllText(CachePath, sb.ToString(), new UTF8Encoding(false));
        }

        static Dictionary<string, GtEntry> Bag(GtDb db, string kind)
        {
            switch (kind)
            {
                case "prefix": return db.Prefixes;
                case "suffix": return db.Suffixes;
                case "ascended": return db.Ascended;
                case "relicbonus": return db.RelicBonuses;
                case "skill": return db.Skills;
                default: return db.Items;
            }
        }

        static void Dump(StringBuilder sb, string kind, Dictionary<string, GtEntry> bag)
        {
            foreach (KeyValuePair<string, GtEntry> kv in bag)
            {
                GtEntry e = kv.Value;
                sb.Append(kind).Append('\t').Append(e.Id).Append('\t').Append(e.NameTag).Append('\t')
                  .Append(e.LootName).Append('\t').Append(e.Bitmap).Append('\t').Append(e.RecordClass).Append('\t')
                  .Append(e.Rarity).Append('\t').Append(e.Level).Append('\t').Append(e.SkillTag).Append('\t')
                  .Append(e.SkillIcon).Append('\t').Append(e.BuffSkill).Append('\t').Append(e.Jitter).Append('\t')
                  .Append(e.ModifiedSkill).Append('\t').Append(e.ModifierSkill).Append('\t')
                  .Append(e.AugmentSkill).Append('\t').Append(e.AugmentLevel).Append('\t')
                  .Append(e.Sig).Append('\t').Append(e.AugmentSkill2).Append('\t')
                  .Append(e.Home).Append('\t').Append(e.HomeButton).Append('\n');
            }
        }
    }
}
