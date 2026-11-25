// Owns the opened save file: makes the one backup per opening, writes changes
// out as they happen, and never leaves a half-written file behind.

using System;
using System.IO;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class SaveManager
    {
        readonly Timer timer = new Timer();
        Character character;
        string path;
        bool backupDone;
        bool pending;

        public event EventHandler<SaveEventArgs> Saved;

        public string Path { get { return path; } }
        public bool Enabled { get; set; }

        public SaveManager()
        {
            Enabled = true;
            // Edits arrive keystroke by keystroke; coalescing them keeps the disk
            // quiet without the user ever having to press a save button.
            timer.Interval = 400;
            timer.Tick += delegate { timer.Stop(); if (pending) WriteNow(); };
        }

        public void Attach(Character c, string filePath)
        {
            Flush();
            character = c;
            path = filePath;
            backupDone = false;
            pending = false;
        }

        public void RequestSave()
        {
            if (character == null || path == null || !Enabled) return;
            pending = true;
            timer.Stop();
            timer.Start();
        }

        public void Flush()
        {
            timer.Stop();
            if (pending) WriteNow();
        }

        // Mastery slots are granted by the game at level transitions. Editing the
        // level field does not grant slots, so the save must preserve the counter.
        //
        //     level >= 2    MasteriesAllowed >= 1
        //     level >= 10   MasteriesAllowed >= 2
        //
        // Never decrease the counter: a granted slot remains granted, and the
        // counter must cover the number of selected masteries. Use the character
        // flag diagnostic when investigating inconsistent saves.
        //
        // A mastery is a `_classtraining_classNN.dbr` record with class
        // `Skill_Mastery`, also recognized by `MasteryEdit`.
        static void FixMasterySlots(Character c)
        {
            if (c == null) return;
            int taken = 0;
            foreach (Skill s in c.Skills)
                if (!string.IsNullOrEmpty(s.Name) &&
                    s.Name.IndexOf("_classtraining_", StringComparison.OrdinalIgnoreCase) >= 0) taken++;
            int want = c.Level >= 10 ? 2 : (c.Level >= 2 ? 1 : 0);
            if (want < taken) want = taken;
            if (c.MasteriesAllowed < want) c.MasteriesAllowed = want;
        }

        void WriteNow()
        {
            pending = false;
            try
            {
                // A block we could only keep as raw bytes is not really decoded:
                // its plaintext is wrong, and writing it out under a key stream
                // that has shifted hands the game rubbish. Refuse rather than
                // produce a save that crashes it.
                if (!character.SafeToRewrite)
                    throw new GdcException(Lang.T("save.error.unparsed", string.Join(", ", character.UnparsedBlocks)));
                // Ensure the counter covers the number of selected masteries.
                // Do not infer the complete level schedule here; preserve granted
                // slots and only raise an insufficient counter.
                FixMasterySlots(character);
                EnsureBackup();
                // Write beside the target and swap in, so a crash mid-write
                // cannot leave the character truncated.
                string temp = path + ".writing";
                File.WriteAllBytes(temp, character.ToBytes());
                VerifyReadable(temp);
                File.Copy(temp, path, true);
                File.Delete(temp);
                Raise(true, null);
            }
            catch (Exception e)
            {
                Raise(false, e.Message);
            }
        }

        // Reads the freshly written file back before it replaces the original,
        // so a bad write is caught here rather than by the game. Re-encrypting it
        // under a different key is part of the check on purpose: a field read with
        // the wrong primitive still round-trips through the same key stream, and
        // only shows up once the stream moves.
        static void VerifyReadable(string file)
        {
            byte[] onDisk = File.ReadAllBytes(file);
            Character check = Character.Load(file);
            if (!check.SafeToRewrite)
                throw new GdcException(Lang.T("save.error.writtenUnparsed", string.Join(", ", check.UnparsedBlocks)));
            Compare(check.ToBytes(), onDisk, Lang.T("save.error.reread"));

            byte[] rekeyed = check.ToBytes(check.Seed ^ 0x9E3779B9u);
            string temp = file + ".rekey";
            try
            {
                File.WriteAllBytes(temp, rekeyed);
                Character second = Character.Load(temp);
                Compare(second.ToBytes(check.Seed), onDisk, Lang.T("save.error.rekey"));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static void Compare(byte[] a, byte[] b, string what)
        {
            if (a.Length != b.Length) throw new GdcException(Lang.T("save.error.length", what, a.Length, b.Length));
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) throw new GdcException(Lang.T("save.error.offset", what, i));
        }

        // One backup per opening. Existing ones are never overwritten: .bak,
        // then .bak1, .bak2 and so on, for the user to clean up when they like.
        void EnsureBackup()
        {
            if (backupDone) return;
            backupDone = true;
            string candidate = path + ".bak";
            for (int n = 1; File.Exists(candidate); n++) candidate = path + ".bak" + n;
            File.Copy(path, candidate);
            BackupPath = candidate;
        }

        public string BackupPath { get; private set; }

        void Raise(bool ok, string error)
        {
            EventHandler<SaveEventArgs> h = Saved;
            if (h != null) h(this, new SaveEventArgs(ok, error, BackupPath));
        }
    }

    public class SaveEventArgs : EventArgs
    {
        public readonly bool Ok;
        public readonly string Error;
        public readonly string BackupPath;
        public SaveEventArgs(bool ok, string error, string backupPath)
        {
            Ok = ok; Error = error; BackupPath = backupPath;
        }
    }
}
