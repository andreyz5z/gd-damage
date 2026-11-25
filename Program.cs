using System;
using System.Windows.Forms;

namespace Gdc.Gui
{
    static partial class Program
    {
        static partial void RunLocalCommand(string[] args, ref bool handled);

        [STAThread]
        static void Main(string[] args)
        {
            string forcedLanguage = Environment.GetEnvironmentVariable("GDC_LANG");
            if (!string.IsNullOrEmpty(forcedLanguage)) Settings.Forced = forcedLanguage;

            if (args.Length > 0 && args[0] == "--wheregame")
            {
                string found = GameData.FindGame(args.Length >= 2 ? args[1] : null);
                Settings settings = Settings.Load(null);
                Console.WriteLine("Detected path:     " + (found ?? "<not found>"));
                Console.WriteLine("Configured path:   " + (settings.GamePath ?? "<empty>"));
                Console.WriteLine("Valid game folder: " + settings.GameLooksValid);
                Environment.ExitCode = found == null ? 1 : 0;
                return;
            }

            bool handled = false;
            RunLocalCommand(args, ref handled);
            if (handled) return;

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Unknown command: " + args[0]);
                Environment.ExitCode = 2;
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            if (args.Length > 0) form.Shown += delegate { form.LoadSave(args[0]); };
            Application.Run(form);
        }
    }
}
