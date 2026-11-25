// Program settings, kept in settings.json next to the executable so the folder
// stays portable and the file can be edited by hand.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gdc.Gui
{
    public class Settings
    {
        [JsonPropertyName("gamePath")]
        public string GamePath { get; set; }

        [JsonPropertyName("savesPath")]
        public string SavesPath { get; set; }

        [JsonPropertyName("lastSave")]
        public string LastSave { get; set; }

        // Default language when settings.json or its language field is absent.
        // A saved setting takes precedence.
        public const string DefaultLanguage = "EN";

        // Per-run language override via `GDC_LANG`. Command-line tests may use
        // localized names in saved fixtures, so they must not depend on the
        // language persisted in the working directory.
        //
        // Batch scripts set their own language override, so checks are independent
        // of the language selected in the UI.
        //
        // The override is never persisted: Save writes the language originally
        // read from the settings file.
        public static string Forced;

        // Language originally read from the file, before applying `Forced`.
        [JsonIgnore]
        string fromFile;

        [JsonPropertyName("language")]
        public string Language { get; set; } = DefaultLanguage;

        [JsonPropertyName("autoSave")]
        public bool AutoSave { get; set; } = true;

        [JsonPropertyName("makeBackup")]
        public bool MakeBackup { get; set; } = true;

        // Appends the raw dump of a record's non-zero fields to every item
        // description. A debugging aid, so it lives in the settings rather than on
        // the toolbar where it is in the way of ordinary use.
        [JsonPropertyName("showRawFields")]
        public bool ShowRawFields { get; set; }

        [JsonIgnore]
        public string Path { get; private set; }

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); }
        }

        public static Settings Load(string path)
        {
            if (path == null) path = DefaultPath;
            Settings s = null;
            try
            {
                if (File.Exists(path))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception) { /* a broken file must not stop the program starting */ }
            if (s == null) s = new Settings();
            s.Path = path;
            s.fromFile = s.Language;
            if (!string.IsNullOrEmpty(Forced)) s.Language = Forced;
            // Also search again when the remembered folder no longer holds the
            // game: it may have been moved, or settings.json may have been copied
            // from another computer.
            if (!s.GameLooksValid) s.GamePath = ItemDbLocator.FindGame(s.GamePath) ?? s.GamePath;
            if (string.IsNullOrEmpty(s.SavesPath)) s.SavesPath = GuessSavesFolder();
            return s;
        }

        public void Save()
        {
            // Do not persist the per-run `GDC_LANG` override; restore the file's
            // original language while saving.
            string running = Language;
            if (!string.IsNullOrEmpty(Forced) && fromFile != null) Language = fromFile;
            try
            {
                JsonSerializerOptions o = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(Path ?? DefaultPath, JsonSerializer.Serialize(this, o), new UTF8Encoding(false));
            }
            catch (Exception) { }
            finally { Language = running; }
        }

        // Prefers a "saves" folder shipped next to the tool, then the game's own.
        static string GuessSavesFolder()
        {
            for (DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                string candidate = System.IO.Path.Combine(d.FullName, "saves");
                if (Directory.Exists(candidate)) return candidate;
            }
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string gd = System.IO.Path.Combine(docs, @"My Games\Grim Dawn\save\main");
            return Directory.Exists(gd) ? gd : docs;
        }

        public bool GameLooksValid
        {
            get
            {
                return !string.IsNullOrEmpty(GamePath) &&
                       File.Exists(System.IO.Path.Combine(GamePath, @"database\database.arz"));
            }
        }
    }
}
