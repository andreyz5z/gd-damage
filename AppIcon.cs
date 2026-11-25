// Shared application icon for all windows.
//
// `ApplicationIcon` applies only to the executable. Window titles need the icon
// assigned explicitly. The icon is an `EmbeddedResource`, so locate the first
// `.ico` resource instead of hard-coding its generated name.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public static class AppIcon
    {
        static Icon icon;
        static bool tried;

        public static Icon Get()
        {
            if (tried) return icon;
            tried = true;
            try
            {
                Assembly me = typeof(AppIcon).Assembly;
                foreach (string name in me.GetManifestResourceNames())
                {
                    if (!name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream s = me.GetManifestResourceStream(name))
                        if (s != null) icon = new Icon(s);
                    break;
                }
            }
            catch (Exception) { icon = null; }   // the window can run without an icon
            return icon;
        }

        public static void Apply(Form form)
        {
            Icon one = Get();
            if (form != null && one != null) form.Icon = one;
        }
    }
}
