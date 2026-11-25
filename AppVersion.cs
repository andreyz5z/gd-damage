using System;
using System.Reflection;

namespace Gdc.Gui
{
    // THE BUILD'S VERSION, AS THE TITLE BAR SHOWS IT.
    //
    // The number is not kept in the source: `GdcGui.csproj` holds a target that reads
    // `build-number.txt`, adds one to it and writes it back before every compile, so each
    // rebuild gets a number of its own. Here we only read what was stamped into the assembly.
    //
    // WHY THE INFORMATIONAL VERSION AND NOT `AssemblyName.Version`: the latter is always four
    // numbers and silently appends a zero (`1.0.42` becomes `1.0.42.0`), while the
    // informational one is the string the project set, as it was set. If some build has none,
    // the file version is the fallback, and an empty answer is also lawful — then the title
    // simply stays as it was.
    static class AppVersion
    {
        static string cached;

        public static string Text
        {
            get
            {
                if (cached != null) return cached;
                cached = "";
                try
                {
                    Assembly a = typeof(AppVersion).Assembly;
                    AssemblyInformationalVersionAttribute info = (AssemblyInformationalVersionAttribute)
                        Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
                    string v = info == null ? null : info.InformationalVersion;
                    // A source-controlled build appends `+<commit>` to the informational version.
                    // The title has no use for it.
                    if (!string.IsNullOrEmpty(v))
                    {
                        int plus = v.IndexOf('+');
                        if (plus > 0) v = v.Substring(0, plus);
                    }
                    if (string.IsNullOrEmpty(v) && a.GetName().Version != null)
                        v = a.GetName().Version.ToString();
                    cached = v ?? "";
                }
                catch (Exception) { cached = ""; }
                return cached;
            }
        }

        // The title with the version appended, or the title unchanged when there is no version
        // to append. The "v" and the spacing are NOT interface text and have no language key:
        // a build number reads the same in every language, and a key per language would only
        // invite thirteen ways to write one number (PLAN, the rule about new interface strings).
        public static string Suffix(string title)
        {
            string v = Text;
            return v.Length == 0 ? title : title + "   v" + v;
        }
    }
}
