using System;
using System.ComponentModel;
using System.Diagnostics;

namespace OrbitEvolved
{
    internal static class CodexLink
    {
        public static Uri ForChat(string id)
        {
            Guid parsed;
            return Guid.TryParseExact(id, "D", out parsed)
                ? new Uri("codex://threads/" + parsed.ToString("D")) : null;
        }

        public static bool Open(Uri link)
        {
            if (link == null) return false;
            try
            {
                Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch (Win32Exception) { return false; }
        }
    }
}
