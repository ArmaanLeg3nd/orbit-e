using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OrbitEvolved
{
    public sealed class ChatSummary
    {
        public string Id, Title, ProjectKey, ProjectName, ProjectHint;
        public DateTime Updated;
        public bool Archived;
    }

    internal sealed class ChatCatalog
    {
        private enum ChatContent { Unknown, Empty, HasActivity }

        private sealed class Rollout
        {
            public FileInfo File;
            public string Id, Parent, Workspace, DeclaredRoot;
            public bool Subagent, Archived;
            public long Length;
            public DateTime LastActivity;
            public long ProbedLength = -1;
            public DateTime ProbedWrite;
            public ChatContent Content;
        }

        private sealed class Project
        {
            public string Id, Name;
            public string[] Roots;
        }

        private readonly string active, archived, home, generatedWorkspaces;
        private Dictionary<string, Rollout> files = new Dictionary<string, Rollout>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Rollout> threads = new Dictionary<string, Rollout>(StringComparer.Ordinal);
        private readonly Dictionary<string, ActivityReader> readers = new Dictionary<string, ActivityReader>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Rollout>> teams = new Dictionary<string, List<Rollout>>(StringComparer.Ordinal);
        private List<ChatSummary> chats = new List<ChatSummary>();
        private DateTime nextScan;

        public ChatCatalog(string home, string activeDirectory)
            : this(home, activeDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex"))
        {
        }

        internal ChatCatalog(string home, string activeDirectory, string generatedWorkspaces)
        {
            this.home = home;
            this.generatedWorkspaces = generatedWorkspaces;
            active = activeDirectory ?? Path.Combine(home, "sessions");
            archived = activeDirectory == null ? Path.Combine(home, "archived_sessions") : null;
        }

        public Snapshot Poll(string selected) { return Poll(selected, null); }

        internal Snapshot Poll(string selected, string retain)
        {
            if (DateTime.UtcNow >= nextScan)
            {
                Scan();
                nextScan = DateTime.UtcNow.AddSeconds(5);
            }
            var chat = chats.FirstOrDefault(item => item.Id == selected);
            if (chat != null)
            {
                var detail = ReadChat(chat);
                detail.Project = chat.Title;
                if (detail.Connected && !detail.CatchingUp)
                    detail.Source = chat.Archived ? "Archived chat" : "Local activity";
                return detail;
            }
            var listing = new Snapshot {
                Project = "Codex projects", Chats = chats, TotalSessions = chats.Count, Connected = chats.Count > 0,
                Source = chats.Count == 1 ? "1 local chat" : chats.Count + " local chats",
                Problem = chats.Count == 0 ? "Local Codex chats will appear here when their session files are available." : ""
            };
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var recent = chats.Where(item => !item.Archived && teams[item.Id].Any(row => row.LastActivity >= cutoff))
                .OrderByDescending(item => teams[item.Id].Max(row => row.LastActivity)).ToList();
            var keep = new HashSet<string>(recent.Select(item => item.Id), StringComparer.Ordinal);
            if (retain != null && teams.ContainsKey(retain)) keep.Add(retain);
            foreach (var id in readers.Keys.Where(id => !keep.Contains(id)).ToList()) readers.Remove(id);
            var bestPriority = Int32.MaxValue;
            foreach (var item in recent)
            {
                var detail = ReadChat(item);
                if (!detail.Connected || detail.CatchingUp) continue;
                var activeAgents = detail.Agents.Where(agent => agent.Updated >= cutoff).ToList();
                var working = activeAgents.Count(agent => agent.State == "working");
                var attention = activeAgents.Any(agent => agent.State == "attention");
                var waiting = activeAgents.Any(agent => agent.State == "waiting");
                if (!attention && working == 0 && !waiting) continue;
                listing.ActiveChats++;
                listing.WorkingAgents += working;
                var priority = attention ? 0 : working > 0 ? 1 : 2;
                if (priority >= bestPriority) continue;
                bestPriority = priority;
                listing.ActiveChat = item;
                listing.ActiveChatStatus = attention ? "Needs your input" : working > 0 ? "Working" : "Waiting";
            }
            return listing;
        }

        private Snapshot ReadChat(ChatSummary chat)
        {
            ActivityReader reader;
            if (!readers.TryGetValue(chat.Id, out reader))
            {
                var workspace = threads[chat.Id].Workspace;
                readers[chat.Id] = reader = new ActivityReader(workspace == "" ? home : workspace, chat.Id, "", active);
            }
            return reader.Poll(new HashSet<string>(teams[chat.Id].Select(row => row.File.FullName), StringComparer.OrdinalIgnoreCase));
        }

        private void Scan()
        {
            var next = new Dictionary<string, Rollout>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in SessionFiles(active).Concat(SessionFiles(archived)))
            {
                Rollout row;
                if (!files.TryGetValue(file.FullName, out row))
                {
                    var meta = ActivityReader.ReadMetadata(file.FullName);
                    var id = Json.Text(meta, "id");
                    if (id == "") continue;
                    var subagent = ActivityReader.SubagentMetadata(meta);
                    if (Json.Get(subagent, "other") != null) continue;
                    var parent = ActivityReader.ParentThread(meta, Json.Map(subagent, "thread_spawn"));
                    var isSubagent = ActivityReader.IsSubagent(meta, subagent, parent);
                    var session = Json.Text(meta, "session_id");
                    if (isSubagent && parent == "" && session != id) parent = session;
                    row = new Rollout {
                        Id = id, Workspace = Json.Text(meta, "cwd"), Parent = parent, DeclaredRoot = session, Subagent = isSubagent,
                        Archived = archived != null && IsWithin(file.FullName, archived)
                    };
                }
                // Windows can leave LastWriteTime unchanged while Codex keeps its rollout handle open.
                if (!row.Archived && (row.File == null || row.Length != file.Length))
                {
                    var stamp = LastRolloutTimestamp(file);
                    if (row.File != null && row.Length != file.Length && stamp <= row.LastActivity) stamp = DateTime.UtcNow;
                    row.Length = file.Length;
                    row.LastActivity = stamp > file.LastWriteTimeUtc ? stamp : file.LastWriteTimeUtc;
                }
                row.File = file;
                next[file.FullName] = row;
            }
            files = next;
            threads = files.Values.GroupBy(row => row.Id).ToDictionary(group => group.Key,
                group => group.OrderByDescending(row => row.File.LastWriteTimeUtc).ThenBy(row => row.Archived).First());

            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in ReadProfileLines("session_index.jsonl"))
            {
                var entry = Json.Parse(line);
                var id = Json.Text(entry, "id");
                var title = Json.Clean(Json.Text(entry, "thread_name"), 110);
                if (id != "" && title != "") titles[id] = title;
            }
            var settings = Json.Parse(ReadProfileLines(".codex-global-state.json").FirstOrDefault() ?? "");
            var projects = (Json.Map(settings, "local-projects") ?? new Dictionary<string, object>()).Values
                .OfType<Dictionary<string, object>>()
                .Select(item => new Project {
                    Id = Json.Text(item, "id"), Name = Json.Text(item, "name"),
                    Roots = (Json.Get(item, "rootPaths") as object[] ?? new object[0]).OfType<string>().Where(path => path != "").ToArray()
                }).Where(item => item.Id != "" && item.Name != "").ToDictionary(item => item.Id);
            var assignments = Json.Map(settings, "thread-project-assignments");
            var projectless = new HashSet<string>((Json.Get(settings, "projectless-thread-ids") as object[] ?? new object[0]).OfType<string>());
            var projectRoots = projects.Values.SelectMany(project => project.Roots.Select(path => new { project, path }))
                .OrderByDescending(root => root.path.Length).ToList();
            var folderRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ChatSummary>();
            foreach (var row in threads.Values.Where(item => !item.Subagent))
            {
                var assignment = Json.Map(assignments, row.Id);
                Project project;
                projects.TryGetValue(Json.Text(assignment, "projectId"), out project);
                var noProject = projectless.Contains(row.Id) || row.Workspace == "";
                if (project == null && !noProject)
                {
                    var match = projectRoots.FirstOrDefault(root => IsWithin(row.Workspace, root.path));
                    if (match != null) project = match.project;
                    else noProject = IsGeneratedWorkspace(row.Workspace);
                }
                string folder = "";
                if (project == null && !noProject && !folderRoots.TryGetValue(row.Workspace, out folder))
                    folderRoots[row.Workspace] = folder = GitRoot(row.Workspace);
                string title;
                if (!titles.TryGetValue(row.Id, out title)) title = "Chat " + row.Id.Substring(0, Math.Min(8, row.Id.Length));
                result.Add(new ChatSummary {
                    Id = row.Id, Title = title, Updated = row.File.LastWriteTimeUtc, Archived = row.Archived,
                    ProjectKey = project != null ? "codex:" + project.Id : noProject ? "projectless" : "folder:" + folder.ToUpperInvariant(),
                    ProjectName = project != null ? project.Name : noProject ? "Projectless chats" : FolderName(folder),
                    ProjectHint = project != null ? String.Join("\n", project.Roots) : folder
                });
            }
            chats = result.OrderByDescending(chat => chat.Updated).ThenBy(chat => chat.Title).ToList();
            teams.Clear();
            var children = threads.Values.Where(row => row.Parent != "").ToLookup(row => row.Parent);
            var attributed = threads.Values.Where(row => row.Subagent && row.DeclaredRoot != "" && !ReachesChat(row))
                .ToLookup(row => row.DeclaredRoot);
            foreach (var chat in chats)
            {
                var members = new List<Rollout>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<Rollout>();
                foreach (var row in attributed[chat.Id]) pending.Push(row);
                pending.Push(threads[chat.Id]);
                while (pending.Count > 0)
                {
                    var row = pending.Pop();
                    if (!seen.Add(row.Id)) continue;
                    members.Add(row);
                    foreach (var child in children[row.Id]) pending.Push(child);
                }
                teams[chat.Id] = members;
            }
            HideEmptyDuplicates();
        }

        private void HideEmptyDuplicates()
        {
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var candidates = chats.GroupBy(chat => chat.ProjectKey + "\0" +
                threads[chat.Id].Workspace.Replace('/', '\\').TrimEnd('\\') + "\0" +
                Regex.Replace(chat.Title, @" \((?:[2-9]|[1-9]\d+)\)$", ""), StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates.Where(group => group.Count() > 1))
            {
                var profiles = group.Select(chat => new {
                    Chat = chat,
                    Content = teams[chat.Id].Count > 1 ? ChatContent.HasActivity : ReadChatContent(threads[chat.Id])
                }).ToList();
                if (!profiles.Any(profile => profile.Content == ChatContent.HasActivity)) continue;
                foreach (var profile in profiles.Where(profile => profile.Content == ChatContent.Empty))
                    hidden.Add(profile.Chat.Id);
            }
            if (hidden.Count > 0) chats = chats.Where(chat => !hidden.Contains(chat.Id)).ToList();
        }

        private static ChatContent ReadChatContent(Rollout row)
        {
            try
            {
                var length = row.File.Length;
                var written = row.File.LastWriteTimeUtc;
                if (row.ProbedLength == length && row.ProbedWrite == written) return row.Content;
                const int limit = 131072;
                byte[] bytes;
                using (var stream = new FileStream(row.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                    bytes = reader.ReadBytes(limit);
                var sample = Encoding.UTF8.GetString(bytes);
                var complete = length == bytes.Length && bytes.Length < limit;
                var end = complete ? sample.Length : sample.LastIndexOf('\n');
                var uncertain = !complete;
                var hasMetadata = false;
                if (end > 0)
                {
                    using (var lines = new StringReader(sample.Substring(0, end)))
                    {
                        string line;
                        while ((line = lines.ReadLine()) != null)
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            var item = Json.Parse(line.TrimStart('\ufeff'));
                            var type = Json.Text(item, "type");
                            if (type == "session_meta") hasMetadata = true;
                            var status = ClassifyRecord(type, Json.Map(item, "payload"));
                            if (status == ChatContent.HasActivity)
                            {
                                row.Content = status;
                                row.ProbedLength = length;
                                row.ProbedWrite = written;
                                return status;
                            }
                            uncertain |= status == ChatContent.Unknown;
                        }
                    }
                }
                row.Content = hasMetadata && !uncertain ? ChatContent.Empty : ChatContent.Unknown;
                row.ProbedLength = length;
                row.ProbedWrite = written;
                return row.Content;
            }
            catch (IOException) { return ChatContent.Unknown; }
            catch (UnauthorizedAccessException) { return ChatContent.Unknown; }
        }

        private static ChatContent ClassifyRecord(string type, Dictionary<string, object> payload)
        {
            if (payload == null) return ChatContent.Unknown;
            if (type == "session_meta" || type == "turn_context" || type == "world_state") return ChatContent.Empty;
            var kind = Json.Text(payload, "type");
            if (type == "event_msg")
            {
                if (kind == "thread_settings_applied" || kind == "turn_aborted") return ChatContent.Empty;
                if (kind == "task_started" || kind == "turn_started" || kind == "task_complete" ||
                    kind == "user_message" || kind == "agent_message" || kind == "item_started" ||
                    kind == "item_completed") return ChatContent.HasActivity;
            }
            if (type == "response_item")
            {
                if (kind == "configuration_update") return ChatContent.Empty;
                if (kind == "function_call" || kind == "custom_tool_call" || kind == "reasoning")
                    return ChatContent.HasActivity;
                if (kind == "message")
                {
                    var role = Json.Text(payload, "role");
                    if (role == "developer" || role == "system") return ChatContent.Empty;
                    if (role == "assistant") return ChatContent.HasActivity;
                    if (role == "user")
                    {
                        // Codex writes this inherited-context marker as a user message in side-chat shells.
                        var parts = Json.Get(payload, "content") as object[];
                        var part = parts != null && parts.Length == 1 ? parts[0] as Dictionary<string, object> : null;
                        var body = Json.Text(part, "text");
                        return Json.Text(part, "type") == "input_text" &&
                            body.StartsWith("Side conversation boundary.", StringComparison.Ordinal) &&
                            body.Contains("Everything before this boundary is inherited history from the parent thread.")
                            ? ChatContent.Empty : ChatContent.HasActivity;
                    }
                }
            }
            return ChatContent.Unknown;
        }

        private bool ReachesChat(Rollout row)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(row.Id))
            {
                if (!row.Subagent) return true;
                if (row.Parent == "" || !threads.TryGetValue(row.Parent, out row)) return false;
            }
            return false;
        }

        private static DateTime LastRolloutTimestamp(FileInfo file)
        {
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var length = (int)Math.Min(stream.Length, 65536);
                    stream.Seek(-length, SeekOrigin.End);
                    using (var reader = new BinaryReader(stream))
                    {
                        var tail = Encoding.UTF8.GetString(reader.ReadBytes(length));
                        var timestamp = Regex.Match(tail, @"(?m)^\{\s*""timestamp""\s*:\s*""([0-9TZ:.+-]{20,40})""", RegexOptions.RightToLeft);
                        DateTime utc;
                        if (timestamp.Success && DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc)) return utc;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return file.LastWriteTimeUtc;
        }

        private static List<FileInfo> SessionFiles(string path)
        {
            var files = new List<FileInfo>();
            if (path == null) return files;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                if (!folder.Exists) continue;
                try
                {
                    files.AddRange(folder.GetFiles("*.jsonl"));
                    foreach (var child in folder.GetDirectories())
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return files;
        }

        private IEnumerable<string> ReadProfileLines(string name)
        {
            try
            {
                using (var stream = new FileStream(Path.Combine(home, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    if (name.EndsWith(".json", StringComparison.Ordinal)) return new[] { reader.ReadToEnd() };
                    var lines = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                    return lines;
                }
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static bool IsWithin(string path, string folder)
        {
            if (String.IsNullOrEmpty(folder)) return false;
            var root = folder.Replace('/', '\\').TrimEnd('\\');
            var candidate = path.Replace('/', '\\').TrimEnd('\\');
            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsGeneratedWorkspace(string path)
        {
            var prefix = generatedWorkspaces.Replace('/', '\\').TrimEnd('\\') + "\\";
            var workspace = path.Replace('/', '\\').TrimEnd('\\');
            if (!workspace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var parts = workspace.Substring(prefix.Length).Split('\\');
            DateTime date;
            return parts.Length == 2 && parts[1] != "" &&
                DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static string GitRoot(string path)
        {
            try
            {
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
                for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
                {
                    var marker = Path.Combine(directory.FullName, ".git");
                    if (Directory.Exists(marker)) return directory.FullName;
                    if (!File.Exists(marker)) continue;
                    var pointer = File.ReadLines(marker).FirstOrDefault() ?? "";
                    if (pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        var gitDir = Path.GetFullPath(Path.Combine(directory.FullName, pointer.Substring(8).Trim()));
                        if (gitDir.StartsWith(@"\\", StringComparison.Ordinal)) return directory.FullName;
                        var common = Path.Combine(gitDir, "commondir");
                        if (File.Exists(common))
                        {
                            var relative = File.ReadLines(common).FirstOrDefault();
                            if (String.IsNullOrWhiteSpace(relative)) return directory.FullName;
                            var shared = new DirectoryInfo(Path.GetFullPath(Path.Combine(gitDir, relative.Trim())));
                            if (shared.Name == ".git" && shared.Parent != null) return shared.Parent.FullName;
                        }
                    }
                    return directory.FullName;
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            return path;
        }

        private static string FolderName(string path)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var name = trimmed.Substring(Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/')) + 1);
            return name == "" ? path : name;
        }
    }
}
