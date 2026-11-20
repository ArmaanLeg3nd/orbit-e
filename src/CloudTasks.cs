using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orbit
{
    internal static class CloudTasks
    {
        public static List<ChatSummary> Read(string explicitCli)
        {
            var cli = FindCli(explicitCli);
            if (cli == null)
                throw new InvalidOperationException("Install the Codex CLI or launch Orbit with --codex-cli pointing to codex.exe.");

            return ReadPages(cursor => Run(cli, "cloud list --limit 20 --json" +
                (cursor == "" ? "" : " --cursor " + QuoteArgument(cursor)), 15000));
        }

        internal static List<ChatSummary> ReadPages(Func<string, string> readPage)
        {
            var tasks = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { "" };
            var cursor = "";
            for (var page = 0; page < 5; page++)
            {
                var response = Json.Parse(readPage(cursor));
                var items = Json.Get(response, "tasks") as object[];
                if (items == null) throw new InvalidOperationException("The Codex CLI returned an unrecognized task list.");
                foreach (var item in items.OfType<Dictionary<string, object>>())
                {
                    var id = Json.Text(item, "id");
                    if (id == "" || tasks.ContainsKey(id)) continue;
                    var title = Json.Clean(Json.Text(item, "title"), 110);
                    var changes = Json.Map(item, "summary");
                    tasks.Add(id, new ChatSummary {
                        Id = "cloud:" + id,
                        Title = title == "" ? "Cloud task" : title,
                        IsCloud = true,
                        ProjectKey = "codex-cloud",
                        ProjectName = "Codex Cloud",
                        ProjectHint = "Tasks from the signed-in Codex CLI",
                        Updated = Json.Time(item, "updated_at", DateTime.MinValue),
                        CloudStatus = StatusLabel(Json.Text(item, "status")),
                        CloudEnvironment = Json.Clean(Json.Text(item, "environment_label"), 110),
                        CloudUrl = BrowserUrl(Json.Text(item, "url")),
                        FilesChanged = Count(changes, "files_changed"),
                        LinesAdded = Count(changes, "lines_added"),
                        LinesRemoved = Count(changes, "lines_removed"),
                        Attempts = Count(item, "attempt_total")
                    });
                }
                cursor = Json.Text(response, "cursor");
                if (cursor == "" || !visited.Add(cursor)) break;
            }
            return tasks.Values.OrderByDescending(task => task.Updated).ThenBy(task => task.Title).ToList();
        }

        private static int? Count(Dictionary<string, object> item, string key)
        {
            var number = Json.Get(item, key);
            if (number == null) return null;
            int value;
            return Int32.TryParse(Convert.ToString(number, CultureInfo.InvariantCulture), NumberStyles.None,
                CultureInfo.InvariantCulture, out value) ? (int?)value : null;
        }

        private static string StatusLabel(string raw)
        {
            if (raw == "") return "Status unavailable";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Json.Clean(raw.Replace('_', ' ').Replace('-', ' '), 50).ToLowerInvariant());
        }

        internal static Uri BrowserUrl(string raw)
        {
            const string taskPrefix = "/codex/tasks/";
            Uri url;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps ||
                (url.Host != "chatgpt.com" && url.Host != "www.chatgpt.com" && url.Host != "chat.openai.com") ||
                !url.IsDefaultPort || url.UserInfo != "" || url.Query != "" || url.Fragment != "" ||
                !url.AbsolutePath.StartsWith(taskPrefix, StringComparison.Ordinal)) return null;
            var id = url.AbsolutePath.Substring(taskPrefix.Length);
            return id != "" && id.All(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                (ch >= '0' && ch <= '9') || ch == '_' || ch == '-') ? url : null;
        }

        private static string FindCli(string explicitCli)
        {
            if (explicitCli != null) return File.Exists(explicitCli) ? explicitCli : null;
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var directory = entry.Trim().Trim('"');
                try
                {
                    if (directory.Length < 3 || directory[1] != ':' ||
                        (directory[2] != '\\' && directory[2] != '/')) continue;
                    var candidate = Path.Combine(directory, "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }

            // An Explorer-launched widget may not inherit Codex Desktop's PATH.
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return null;
            try
            {
                var location = Run(powershell, "-NoProfile -NonInteractive -Command " +
                    QuoteArgument("Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1 -ExpandProperty InstallLocation"), 5000).Trim();
                if (location == "") return null;
                var bundled = Path.Combine(location, "app", "resources", "codex.exe");
                return File.Exists(bundled) ? bundled : null;
            }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        private static string Run(string executable, string arguments, int timeout)
        {
            var start = new ProcessStartInfo(executable, arguments) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Could not start the Codex CLI.");
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("The Codex CLI timed out while loading cloud chats.");
                }
                if (!Task.WaitAll(new Task[] { output, errors }, 1000) || process.ExitCode != 0)
                    throw new InvalidOperationException("The Codex CLI could not load cloud chats. Check its sign-in and try again.");
                return output.Result;
            }
        }

        internal static string QuoteArgument(string value)
        {
            var argument = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                argument.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                argument.Append(character);
                slashes = 0;
            }
            return argument.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
