using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace Orbit
{
    public static class Json
    {
        public static Dictionary<string, object> Parse(string text)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = 4194304, RecursionLimit = 80 }.DeserializeObject(text) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        public static object Get(Dictionary<string, object> item, string key) { object value; return item != null && item.TryGetValue(key, out value) ? value : null; }
        public static string Text(Dictionary<string, object> item, string key) { return Get(item, key) as string ?? ""; }
        public static Dictionary<string, object> Map(Dictionary<string, object> item, string key) { return Get(item, key) as Dictionary<string, object>; }
        public static DateTime Time(Dictionary<string, object> item, string key, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParse(Text(item, key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed) ? parsed : fallback;
        }
        public static string Clean(string value, int max)
        {
            value = Regex.Replace(value ?? "", @"\s+", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }
    }

    public sealed class ActivityEvent
    {
        public string AgentId { get; set; }
        public string Agent { get; set; }
        public string Text { get; set; }
        public string State { get; set; }
        public DateTime At { get; set; }
        public string TimeLabel { get { return At.ToLocalTime().ToString("HH:mm"); } }
        public Brush Brush { get { return Palette.State(State); } }
    }
    public static class Palette
    {
        public static SolidColorBrush Color(string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value); brush.Freeze(); return brush; }
        public static Brush State(string state)
        {
            return Color(state == "working" ? "#A8C8F0" : state == "attention" || state == "waiting" ? "#E5BE89" : state == "error" ? "#EBA1AA" : "#B8C0CD");
        }
    }
    public sealed class Agent : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string ParentId { get; set; }
        public string Name { get; set; }
        public string Task { get; set; }
        public string State { get; set; }
        public string Role { get; set; }
        public DateTime Started { get; set; }
        public DateTime Updated { get; set; }
        public DateTime Ended { get; set; }
        public bool IsRoot { get; set; }
        public string SessionRoot { get; set; }
        public List<ActivityEvent> Events = new List<ActivityEvent>();
        public Brush AccentBrush { get { return Palette.State(State); } }
        public Brush SoftBrush { get { return Palette.Color("#20FFFFFF"); } }
        public string Glyph { get { return State == "completed" ? "\uE73E" : State == "attention" || State == "error" ? "\uE7BA" : Role == "explorer" ? "\uE721" : Role == "reviewer" ? "\uE73E" : Role == "tester" ? "\uE9D9" : IsRoot ? "\uE8D7" : "\uE943"; } }
        public string StatusLabel { get { return State == "working" ? "Working" : State == "waiting" ? "Waiting" : State == "completed" ? "Done" : State == "attention" ? "Needs you" : State == "error" ? "Error" : "Idle"; } }
        public string ChildLabel { get { return IsRoot ? "LEAD" : ""; } }
        public string AccessibleName { get { return Name + ", " + StatusLabel + ", " + Task; } }
        public string Elapsed
        {
            get
            {
                var seconds = Math.Max(0, ((Ended == DateTime.MinValue ? DateTime.UtcNow : Ended) - Started).TotalSeconds);
                return seconds >= 3600 ? ((int)(seconds / 3600)) + "h " + ((int)(seconds % 3600 / 60)) + "m" : seconds >= 60 ? ((int)(seconds / 60)) + "m " + ((int)(seconds % 60)).ToString("00") + "s" : ((int)seconds) + "s";
            }
        }
        public Agent Copy() { var copy = (Agent)MemberwiseClone(); copy.Events = new List<ActivityEvent>(Events); return copy; }
        public void Refresh(Agent next)
        {
            ParentId = next.ParentId; Name = next.Name; Task = next.Task; State = next.State; Role = next.Role; Started = next.Started; Updated = next.Updated; Ended = next.Ended; IsRoot = next.IsRoot; SessionRoot = next.SessionRoot; Events = next.Events;
            Notify();
        }
        public void Notify() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("")); }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    public sealed class SessionSummary
    {
        public string RootId = "";
        public int Agents;
        public int Working;
        public bool NeedsAttention;
        public DateTime LastWriteUtc = DateTime.MinValue;
        public bool IsFocused;
    }
    public sealed class Snapshot
    {
        public List<Agent> Agents = new List<Agent>();
        public List<ActivityEvent> Events = new List<ActivityEvent>();
        public List<SessionSummary> Sessions = new List<SessionSummary>();
        public string Project = "";
        public string Source = "Watching local activity";
        public string Problem = "";
        public string Session = "";
        public string FocusedSession = "";
        public bool Connected;
        public bool CatchingUp;
        public string NewerRoot = "";
        public int TotalAgents;
        public int TotalSessions;
        public DateTime LastWriteUtc = DateTime.MinValue;
    }

    // Byte offsets and incomplete-line buffering prevent partial UTF-8 writes from being lost.
    public sealed class JsonlTail
    {
        public long Offset;
        public bool WasReset;
        public bool HitBudget;
        private readonly List<byte> pending = new List<byte>();
        private bool discardLine;
        public List<string> Read(string path, int budget)
        {
            var lines = new List<string>();
            WasReset = false;
            HitBudget = false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < Offset) { Offset = 0; pending.Clear(); discardLine = false; WasReset = true; }
                if (Offset == 0 && stream.Length > 2097152) { Offset = stream.Length - 2097152; discardLine = true; }
                stream.Position = Offset;
                var buffer = new byte[Math.Min(65536, budget)];
                int count;
                while (budget > 0 && (count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget))) > 0)
                {
                    Offset += count; budget -= count;
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] == 10)
                        {
                            if (!discardLine && pending.Count > 0) lines.Add(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
                            pending.Clear(); discardLine = false;
                        }
                        else if (!discardLine)
                        {
                            pending.Add(buffer[i]);
                            if (pending.Count > 4194304) { pending.Clear(); discardLine = true; }
                        }
                    }
                }
                if (budget <= 0 && stream.Length > Offset) HitBudget = true;
            }
            return lines;
        }
    }

    public sealed class ActivityReader
    {
        private sealed class PendingTool
        {
            public string Name;
            public string Label;
        }
        private sealed class SessionFile
        {
            public string Path;
            public string RootId;
            public string DeclaredRootId;
            public string MetadataParentId;
            public string Workspace;
            public Agent Agent;
            public DateTime InitialStarted;
            public JsonlTail Tail = new JsonlTail();
            public readonly Dictionary<string, PendingTool> Tools = new Dictionary<string, PendingTool>();
            public readonly Dictionary<string, string> AttentionCalls = new Dictionary<string, string>();
            public string UnkeyedAttention = "";
            public string LastActivity = "";
            public readonly HashSet<string> MessageIds = new HashSet<string>();
            public readonly Queue<string> MessageOrder = new Queue<string>();
        }
        private readonly string workspace;
        private readonly string sessionFilter;
        private readonly string eventFile;
        private readonly string sessionsPath;
        private readonly Dictionary<string, SessionFile> sessions = new Dictionary<string, SessionFile>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Agent> customAgents = new Dictionary<string, Agent>();
        private readonly JsonlTail customTail = new JsonlTail();
        private DateTime discoveryAt;
        private DateTime pinnedLookupAt;
        private string pinnedDirectory = "";
        private string selectedRoot = "";
        private DateTime selectedRootAt = DateTime.MinValue;

        public ActivityReader(string project, string session, string events, string sessionDirectory)
        {
            workspace = project; sessionFilter = session; eventFile = events;
            var codexDirectory = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrEmpty(codexDirectory)) codexDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            sessionsPath = sessionDirectory ?? Path.Combine(codexDirectory, "sessions");
        }
        public Snapshot Poll()
        {
            var snapshot = new Snapshot { Project = new DirectoryInfo(workspace).Name };
            try
            {
                if (!String.IsNullOrEmpty(eventFile))
                {
                    if (!File.Exists(eventFile)) { snapshot.Source = "Waiting for event file"; snapshot.Problem = "The selected activity file is not available yet."; return snapshot; }
                    var records = customTail.Read(eventFile, 1048576);
                    if (customTail.WasReset) customAgents.Clear();
                    foreach (var line in records) ApplyCustom(Json.Parse(line));
                    snapshot.Agents = customAgents.Values.Select(a => a.Copy()).ToList();
                    snapshot.Source = "Connected to local event file"; snapshot.Connected = true;
                }
                else
                {
                    if (!Directory.Exists(sessionsPath)) { snapshot.Source = "Waiting for Codex"; snapshot.Problem = "Start a local Codex session in this project to see its team here."; return snapshot; }
                    var discoveryInterval = !String.IsNullOrEmpty(sessionFilter) ? 5 : String.IsNullOrEmpty(selectedRoot) ? 1.5 : 5;
                    if ((DateTime.UtcNow - discoveryAt).TotalSeconds > discoveryInterval) { Discover(); discoveryAt = DateTime.UtcNow; }
                    var pinned = !String.IsNullOrEmpty(sessionFilter);
                    var focus = pinned ? sessionFilter : selectedRoot;
                    var feed = pinned ? new Dictionary<string, List<SessionFile>>(StringComparer.Ordinal) : AllTeams();
                    if (pinned) feed[sessionFilter] = SelectTeam(sessionFilter);
                    var orderedKeys = new List<string>(feed.Keys);
                    orderedKeys.Sort(delegate(string a, string b)
                    {
                        var rankA = TeamRank(feed[a]);
                        var rankB = TeamRank(feed[b]);
                        if (rankA != rankB) return rankA.CompareTo(rankB);
                        var writeA = TeamWrite(feed[a]);
                        var writeB = TeamWrite(feed[b]);
                        if (writeA != writeB) return writeB.CompareTo(writeA);
                        return String.Compare(a, b, StringComparison.Ordinal);
                    });
                    var catchingUp = false;
                    var lastWrite = DateTime.MinValue;
                    foreach (var key in orderedKeys)
                    {
                        foreach (var file in feed[key])
                        {
                            try
                            {
                                var records = file.Tail.Read(file.Path, 1048576);
                                if (file.Tail.HitBudget) catchingUp = true;
                                if (file.Tail.WasReset) ResetSession(file);
                                foreach (var line in records) ApplySession(file, Json.Parse(line));
                                try { var mtime = File.GetLastWriteTimeUtc(file.Path); if (mtime > lastWrite) lastWrite = mtime; }
                                catch (IOException) { }
                                catch (UnauthorizedAccessException) { }
                            }
                            catch (IOException) { snapshot.Problem = "A session file is temporarily unavailable. Retrying automatically."; }
                            catch (UnauthorizedAccessException) { snapshot.Problem = "A session file could not be read. Check its file permissions."; }
                        }
                    }
                    orderedKeys.Sort(delegate(string a, string b)
                    {
                        var rankA = TeamRank(feed[a]);
                        var rankB = TeamRank(feed[b]);
                        if (rankA != rankB) return rankA.CompareTo(rankB);
                        var writeA = TeamWrite(feed[a]);
                        var writeB = TeamWrite(feed[b]);
                        if (writeA != writeB) return writeB.CompareTo(writeA);
                        return String.Compare(a, b, StringComparison.Ordinal);
                    });
                    if (!pinned && orderedKeys.Count > 0)
                    {
                        focus = orderedKeys[0];
                        if (focus != selectedRoot) selectedRootAt = DateTime.UtcNow;
                        selectedRoot = focus;
                    }
                    var visible = !pinned && !String.IsNullOrEmpty(focus) && feed.ContainsKey(focus) ? feed[focus] : null;
                    var orderedAgents = new List<Agent>();
                    if (pinned || visible == null)
                    {
                        foreach (var key in orderedKeys)
                        {
                            var members = feed[key].OrderByDescending(f => f.Agent.IsRoot)
                                .ThenBy(f => f.Agent.State == "attention" || f.Agent.State == "error" ? 0 : f.Agent.State == "working" ? 1 : f.Agent.State == "waiting" ? 2 : 3)
                                .ThenBy(f => f.Agent.Started).ThenBy(f => f.Agent.Id, StringComparer.Ordinal);
                            foreach (var file in members) orderedAgents.Add(file.Agent.Copy());
                        }
                    }
                    else
                    {
                        var members = visible.OrderByDescending(f => f.Agent.IsRoot)
                            .ThenBy(f => f.Agent.State == "attention" || f.Agent.State == "error" ? 0 : f.Agent.State == "working" ? 1 : f.Agent.State == "waiting" ? 2 : 3)
                            .ThenBy(f => f.Agent.Started).ThenBy(f => f.Agent.Id, StringComparer.Ordinal);
                        foreach (var file in members) orderedAgents.Add(file.Agent.Copy());
                    }
                    snapshot.Agents = orderedAgents;
                    snapshot.Session = focus;
                    snapshot.FocusedSession = focus;
                    snapshot.Connected = snapshot.Agents.Count > 0;
                    snapshot.CatchingUp = catchingUp;
                    snapshot.LastWriteUtc = lastWrite;
                    snapshot.NewerRoot = "";
                    snapshot.TotalAgents = snapshot.Agents.Count;
                    snapshot.TotalSessions = orderedKeys.Count;
                    snapshot.Sessions.Clear();
                    foreach (var key in orderedKeys)
                    {
                        var summary = new SessionSummary { RootId = key, IsFocused = key == focus, LastWriteUtc = TeamWrite(feed[key]) };
                        foreach (var file in feed[key])
                        {
                            summary.Agents++;
                            if (file.Agent.State == "working") summary.Working++;
                            if (file.Agent.State == "attention" || file.Agent.State == "error") summary.NeedsAttention = true;
                        }
                        snapshot.Sessions.Add(summary);
                    }
                    if (!snapshot.Connected) { snapshot.Source = "Listening for Codex"; snapshot.Problem = "Start a local Codex session in this project. Its agents will appear here automatically."; }
                    else if (catchingUp) snapshot.Source = "Catching up local Codex…";
                    else if (pinned)
                    {
                        snapshot.Source = "Session " + focus.Substring(0, Math.Min(8, focus.Length)) + " · " + FreshAge(lastWrite);
                    }
                    else if (orderedKeys.Count <= 1) snapshot.Source = "Synced with local Codex" + FreshSuffix(lastWrite);
                    else
                    {
                        var working = 0;
                        var needsYou = 0;
                        var waiting = 0;
                        foreach (var team in feed.Values)
                        {
                            foreach (var file in team)
                            {
                                if (file.Agent.State == "working") working++;
                                else if (file.Agent.State == "attention" || file.Agent.State == "error") needsYou++;
                                else if (file.Agent.State == "waiting") waiting++;
                            }
                        }
                        var text = orderedKeys.Count + " sessions";
                        if (working > 0) text += " · " + working + " working";
                        if (needsYou > 0) text += " · " + needsYou + " needs you";
                        else if (waiting > 0) text += " · " + waiting + " waiting";
                        snapshot.Source = text + FreshSuffix(lastWrite);
                    }
                }
                foreach (var agent in snapshot.Agents)
                {
                    // A silent log is not proof of completion. Keep that distinction visible.
                    if (agent.State == "working" && (DateTime.UtcNow - agent.Updated).TotalMinutes > 5) { agent.State = "idle"; agent.Task = "No recent activity · last seen " + agent.Updated.ToLocalTime().ToString("HH:mm"); agent.Ended = agent.Updated; }
                }
                if (!String.IsNullOrEmpty(eventFile)) snapshot.Agents = snapshot.Agents.OrderByDescending(a => a.IsRoot).ThenBy(a => a.State == "attention" || a.State == "error" ? 0 : a.State == "working" ? 1 : a.State == "waiting" ? 2 : 3).ThenBy(a => a.Started).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
                if (String.IsNullOrEmpty(eventFile)) snapshot.TotalAgents = snapshot.Agents.Count;
                snapshot.Agents = snapshot.Agents.Take(60).ToList();
                if (snapshot.TotalAgents > snapshot.Agents.Count && snapshot.Problem == "")
                {
                    snapshot.Problem = snapshot.TotalSessions > 1
                        ? "Showing " + snapshot.Agents.Count + " of " + snapshot.TotalAgents + " agents across " + snapshot.TotalSessions + " sessions; oldest completed hidden first."
                        : "Showing " + snapshot.Agents.Count + " of " + snapshot.TotalAgents + " agents; oldest completed entries hidden first.";
                }
                snapshot.Events = snapshot.Agents.SelectMany(a => a.Events).OrderByDescending(e => e.At).Take(100).ToList();
            }
            catch (IOException) { snapshot.Source = "Reconnecting to local activity"; snapshot.Problem = "Activity is temporarily unavailable. Orbit will retry automatically."; }
            catch (UnauthorizedAccessException) { snapshot.Source = "Activity folder unavailable"; snapshot.Problem = "Orbit cannot read the activity folder. Check local file permissions."; }
            return snapshot;
        }
        private void Discover()
        {
            var candidates = new List<FileInfo>();
            for (int day = -1; day <= 3; day++)
            {
                var date = DateTime.Now.AddDays(-day);
                var folder = Path.Combine(sessionsPath, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
                if (Directory.Exists(folder)) candidates.AddRange(new DirectoryInfo(folder).GetFiles("*.jsonl"));
            }
            // Also support a flat directory for a local event adapter or integration test.
            candidates.AddRange(new DirectoryInfo(sessionsPath).GetFiles("*.jsonl"));
            foreach (var file in candidates.OrderByDescending(f => f.LastWriteTimeUtc).Take(160)) DiscoverFile(file);
            DiscoverPinned();
            ResolveAncestry();
            // Follow the newest active session; show every team so nothing hides.
            // --session still pins a single tree.
            if (String.IsNullOrEmpty(sessionFilter))
            {
                var focus = PickFocus();
                if (!String.IsNullOrEmpty(focus) && focus != selectedRoot) selectedRootAt = DateTime.UtcNow;
                selectedRoot = focus;
            }
            if (sessions.Count > 200)
            {
                var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!String.IsNullOrEmpty(sessionFilter)) foreach (var member in SelectTeam(sessionFilter)) protectedPaths.Add(member.Path);
                else
                {
                    foreach (var team in AllTeams().Values)
                    {
                        if (TeamActive(team)) foreach (var member in team) protectedPaths.Add(member.Path);
                    }
                    if (!String.IsNullOrEmpty(selectedRoot)) foreach (var member in SelectTeam(selectedRoot)) protectedPaths.Add(member.Path);
                }
                foreach (var key in sessions.Where(s => !protectedPaths.Contains(s.Key)).OrderBy(s => s.Value.Agent.Updated).Select(s => s.Key).Take(sessions.Count - 160).ToList()) sessions.Remove(key);
            }
            if (skipped.Count > 2000) skipped.Clear();
        }
        private bool WorkspaceMatches(string cwd)
        {
            var normCwd = (cwd ?? "").TrimEnd('\\', '/');
            var normWs = workspace.TrimEnd('\\', '/');
            if (String.Equals(normCwd, normWs, StringComparison.OrdinalIgnoreCase)) return true;
            if (normCwd.Length > normWs.Length && normCwd.StartsWith(normWs, StringComparison.OrdinalIgnoreCase))
            {
                var sep = normCwd[normWs.Length];
                return sep == '\\' || sep == '/';
            }
            return false;
        }
        private static string FreshAge(DateTime lastWrite)
        {
            if (lastWrite == DateTime.MinValue) return "Synced with local Codex";
            var age = DateTime.UtcNow - lastWrite;
            if (age.TotalSeconds < 10) return "Synced";
            if (age.TotalMinutes < 1) return "Synced " + Math.Max(1, (int)age.TotalSeconds) + "s ago";
            if (age.TotalHours < 1) return "Synced " + Math.Max(1, (int)age.TotalMinutes) + "m ago";
            return "last write " + lastWrite.ToLocalTime().ToString("HH:mm");
        }
        private static string FreshSuffix(DateTime lastWrite)
        {
            if (lastWrite == DateTime.MinValue) return "";
            var age = DateTime.UtcNow - lastWrite;
            if (age.TotalSeconds < 10) return "";
            if (age.TotalMinutes < 1) return " · " + Math.Max(1, (int)age.TotalSeconds) + "s ago";
            if (age.TotalHours < 1) return " · " + Math.Max(1, (int)age.TotalMinutes) + "m ago";
            return " · last write " + lastWrite.ToLocalTime().ToString("HH:mm");
        }
        private void DiscoverFile(FileInfo file)
        {
            if (sessions.ContainsKey(file.FullName) || skipped.Contains(file.FullName)) return;
            Dictionary<string, object> meta;
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) meta = Json.Map(Json.Parse(reader.ReadLine() ?? ""), "payload");
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (meta == null || String.IsNullOrEmpty(Json.Text(meta, "id"))) return;
            var cwd = Json.Text(meta, "cwd");
            if (!WorkspaceMatches(cwd)) { skipped.Add(file.FullName); return; }
            var source = Json.Map(meta, "source");
            var subagent = Json.Map(source, "subagent");
            // Internal guardian/review tasks are not user-spawned agents.
            if (subagent != null && Json.Get(subagent, "other") != null) { skipped.Add(file.FullName); return; }
            var spawn = Json.Map(subagent, "thread_spawn");
            var parent = Json.Text(meta, "parent_thread_id");
            if (parent == "") parent = Json.Text(spawn, "parent_thread_id");
            var id = Json.Text(meta, "id");
            var session = Json.Text(meta, "session_id");
            var name = AgentName(meta, spawn, id, parent);
            var role = Json.Text(meta, "agent_role");
            if (role == "") role = Json.Text(spawn, "agent_role");
            var agent = new Agent { Id = id, ParentId = parent, IsRoot = parent == "", Name = Json.Clean(name, 32), Role = role, Task = "Waiting for activity", State = "idle", Started = Json.Time(meta, "timestamp", file.CreationTimeUtc), Updated = file.LastWriteTimeUtc, SessionRoot = !String.IsNullOrEmpty(session) ? session : id };
            sessions[file.FullName] = new SessionFile { Path = file.FullName, RootId = session, DeclaredRootId = session, MetadataParentId = parent, Workspace = cwd, Agent = agent, InitialStarted = agent.Started };
        }
        private static string AgentName(Dictionary<string, object> meta, Dictionary<string, object> spawn, string id, string parent)
        {
            var name = Json.Text(meta, "task_name");
            if (name == "") name = Json.Text(spawn, "task_name");
            var path = Json.Text(meta, "agent_path");
            if (path == "") path = Json.Text(spawn, "agent_path");
            if (name == "" && path != "")
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && !(parts.Length == 1 && parts[0] == "root")) name = parts[parts.Length - 1];
            }
            if (name == "") name = Json.Text(meta, "agent_nickname");
            if (name == "") name = Json.Text(spawn, "agent_nickname");
            return name == "" ? (parent == "" ? "Coordinator" : "Agent") + " " + id.Substring(0, Math.Min(8, id.Length)) : name;
        }
        private void DiscoverPinned()
        {
            if (String.IsNullOrEmpty(sessionFilter) || sessionFilter.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            var known = sessions.Values.FirstOrDefault(s => s.Agent.Id == sessionFilter);
            if (known != null) pinnedDirectory = Path.GetDirectoryName(known.Path);
            if (pinnedDirectory == "" && (DateTime.UtcNow - pinnedLookupAt).TotalSeconds > 30)
            {
                pinnedLookupAt = DateTime.UtcNow;
                // Rollout names end in the thread ID. Limit historical lookup to 400 date
                // directories and read metadata only for an exact filename suffix match.
                foreach (var folder in HistoricalFolders().Take(400))
                {
                    foreach (var path in Directory.EnumerateFiles(folder, "*" + sessionFilter + ".jsonl").Take(8)) DiscoverFile(new FileInfo(path));
                    known = sessions.Values.FirstOrDefault(s => s.Agent.Id == sessionFilter);
                    if (known != null) { pinnedDirectory = Path.GetDirectoryName(known.Path); break; }
                }
            }
            if (pinnedDirectory != "" && Directory.Exists(pinnedDirectory))
                foreach (var file in new DirectoryInfo(pinnedDirectory).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(160)) DiscoverFile(file);
        }
        private IEnumerable<string> HistoricalFolders()
        {
            yield return sessionsPath;
            foreach (var year in Directory.EnumerateDirectories(sessionsPath).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^\d{4}$")).OrderByDescending(p => p).Take(10))
                foreach (var month in Directory.EnumerateDirectories(year).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^\d{2}$")).OrderByDescending(p => p).Take(12))
                    foreach (var day in Directory.EnumerateDirectories(month).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^\d{2}$")).OrderByDescending(p => p).Take(31)) yield return day;
        }
        private Dictionary<string, SessionFile> SessionsById()
        {
            return sessions.Values.GroupBy(s => s.Agent.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(s => File.GetLastWriteTimeUtc(s.Path)).First());
        }
        private void ResolveAncestry()
        {
            var byId = SessionsById();
            foreach (var file in sessions.Values) { file.Agent.ParentId = file.MetadataParentId; file.Agent.IsRoot = file.Agent.ParentId == ""; }
            foreach (var file in byId.Values)
            {
                var chain = new List<SessionFile>();
                var positions = new Dictionary<string, int>();
                var current = file;
                while (current != null)
                {
                    int cycleAt;
                    if (positions.TryGetValue(current.Agent.Id, out cycleAt))
                    {
                        // Break one deterministic edge, retaining every agent in bad metadata.
                        var anchor = chain.Skip(cycleAt).OrderBy(s => s.Agent.Id, StringComparer.Ordinal).First();
                        var declaredRoot = chain.Skip(cycleAt).Select(s => s.DeclaredRootId).FirstOrDefault(id => id != "" && !positions.ContainsKey(id) && (!byId.ContainsKey(id) || byId[id].MetadataParentId == ""));
                        anchor.Agent.ParentId = declaredRoot ?? ""; anchor.Agent.IsRoot = anchor.Agent.ParentId == "";
                        break;
                    }
                    positions[current.Agent.Id] = chain.Count; chain.Add(current);
                    if (current.Agent.ParentId == "" || !byId.TryGetValue(current.Agent.ParentId, out current)) break;
                }
            }
            foreach (var file in sessions.Values)
            {
                var current = file;
                var seen = new HashSet<string>();
                while (seen.Add(current.Agent.Id))
                {
                    if (current.Agent.ParentId == "") { file.RootId = current.Agent.Id; break; }
                    SessionFile parent;
                    if (!byId.TryGetValue(current.Agent.ParentId, out parent))
                    {
                        file.RootId = file.DeclaredRootId != "" ? file.DeclaredRootId : current.DeclaredRootId != "" ? current.DeclaredRootId : current.Agent.ParentId;
                        break;
                    }
                    current = parent;
                }
                file.Agent.SessionRoot = !String.IsNullOrEmpty(file.RootId) ? file.RootId : file.Agent.Id;
            }
        }
        private List<SessionFile> SelectTeam(string root)
        {
            if (String.IsNullOrEmpty(root)) return new List<SessionFile>();
            var byId = SessionsById();
            return byId.Values.Where(file =>
            {
                if (file.RootId == root || file.Agent.Id == root) return true;
                var current = file;
                var seen = new HashSet<string>();
                while (seen.Add(current.Agent.Id) && current.Agent.ParentId != "")
                {
                    if (current.Agent.ParentId == root) return true;
                    if (!byId.TryGetValue(current.Agent.ParentId, out current)) break;
                }
                return false;
            }).ToList();
        }
        private Dictionary<string, List<SessionFile>> AllTeams()
        {
            var byId = SessionsById();
            var teams = new Dictionary<string, List<SessionFile>>(StringComparer.Ordinal);
            foreach (var file in byId.Values)
            {
                var key = !String.IsNullOrEmpty(file.RootId) ? file.RootId : file.Agent.Id;
                List<SessionFile> team;
                if (!teams.TryGetValue(key, out team)) { team = new List<SessionFile>(); teams[key] = team; }
                team.Add(file);
            }
            return teams;
        }
        private static bool TeamActive(List<SessionFile> team)
        {
            foreach (var file in team)
            {
                if (file.Agent.State == "working" || file.Agent.State == "waiting" || file.Agent.State == "attention" || file.Agent.State == "error") return true;
            }
            return false;
        }
        private static DateTime TeamWrite(List<SessionFile> team)
        {
            var latest = DateTime.MinValue;
            foreach (var file in team)
            {
                try { var mtime = File.GetLastWriteTimeUtc(file.Path); if (mtime > latest) latest = mtime; }
                catch (IOException) { if (file.Agent.Updated > latest) latest = file.Agent.Updated; }
                catch (UnauthorizedAccessException) { if (file.Agent.Updated > latest) latest = file.Agent.Updated; }
            }
            return latest;
        }
        private static int TeamRank(List<SessionFile> team)
        {
            var needsAttention = false;
            var working = false;
            var waiting = false;
            foreach (var file in team)
            {
                if (file.Agent.State == "attention" || file.Agent.State == "error") needsAttention = true;
                else if (file.Agent.State == "working") working = true;
                else if (file.Agent.State == "waiting") waiting = true;
            }
            return needsAttention ? 0 : working ? 1 : waiting ? 2 : 3;
        }
        private string PickFocus()
        {
            var teams = AllTeams();
            if (teams.Count == 0) return "";
            var active = new List<KeyValuePair<string, List<SessionFile>>>();
            foreach (var entry in teams) if (TeamActive(entry.Value)) active.Add(entry);
            var pool = active.Count > 0 ? active : new List<KeyValuePair<string, List<SessionFile>>>(teams);
            var best = pool[0];
            var bestWrite = TeamWrite(best.Value);
            for (int i = 1; i < pool.Count; i++)
            {
                var write = TeamWrite(pool[i].Value);
                if (write > bestWrite || (write == bestWrite && String.Compare(pool[i].Key, best.Key, StringComparison.Ordinal) < 0)) { best = pool[i]; bestWrite = write; }
            }
            return best.Key;
        }
        private static void ClearPending(SessionFile file)
        {
            file.Tools.Clear(); file.AttentionCalls.Clear(); file.UnkeyedAttention = ""; file.LastActivity = "";
        }
        private static void ResetSession(SessionFile file)
        {
            ClearPending(file);
            file.MessageIds.Clear(); file.MessageOrder.Clear();
            file.Agent.Events.Clear(); file.Agent.State = "idle"; file.Agent.Task = "Waiting for activity";
            file.Agent.Started = file.InitialStarted; file.Agent.Ended = DateTime.MinValue; file.Agent.Updated = file.InitialStarted;
        }
        private static string CorrelationId(Dictionary<string, object> payload)
        {
            var id = Json.Text(payload, "call_id");
            return id == "" ? Json.Text(payload, "request_id") : id;
        }
        private static void PendingState(SessionFile file, string state, string task)
        {
            var attention = file.UnkeyedAttention != "" ? file.UnkeyedAttention : file.AttentionCalls.Values.FirstOrDefault();
            if (!String.IsNullOrEmpty(attention)) { state = "attention"; task = attention; }
            else if (file.Tools.Count > 0)
            {
                var running = file.Tools.Values.FirstOrDefault(tool => !tool.Name.Contains("wait"));
                state = running == null ? "waiting" : "working";
                task = running == null ? "Waiting for a task" : running.Label;
            }
            file.Agent.State = state; file.Agent.Task = task; file.Agent.Ended = DateTime.MinValue;
        }
        private static void Record(Agent agent, string text, DateTime at, string state = null)
        {
            text = Json.Clean(text, 240);
            if (text == "") return;
            var activity = new ActivityEvent { AgentId = agent.Id, Agent = agent.Name, Text = text, State = state ?? agent.State, At = at };
            if (agent.Events.Count > 0 && agent.Events[agent.Events.Count - 1].Text == text)
            {
                // Snapshots share event objects: replace, never mutate an event on the reader thread.
                if (at >= agent.Events[agent.Events.Count - 1].At) agent.Events[agent.Events.Count - 1] = activity;
                return;
            }
            agent.Events.Add(activity);
            if (agent.Events.Count > 40) agent.Events.RemoveAt(0);
        }
        private static string ToolLabel(string name)
        {
            name = name.Split('.').Last();
            if (name.Contains("exec") || name.Contains("command")) return "Running a command";
            if (name.Contains("patch") || name.Contains("write_file")) return "Updating project files";
            if (name.Contains("search") || name.Contains("web")) return "Looking up references";
            if (name.Contains("spawn")) return "Delegating a task";
            if (name.Contains("wait")) return "Waiting for a task";
            if (name.Contains("read")) return "Reading project context";
            if (name.Contains("test")) return "Running checks";
            if (name.Contains("message")) return "Coordinating with the team";
            return "Using " + Json.Clean(name.Replace('_', ' '), 46);
        }
        private static string PublicText(Dictionary<string, object> item)
        {
            var content = Json.Get(item, "content");
            if (content is string) return (string)content;
            var parts = content as object[];
            if (parts == null) return "";
            return String.Join(" ", parts.OfType<Dictionary<string, object>>()
                .Where(part => new[] { "Text", "text", "output_text" }.Contains(Json.Text(part, "type")))
                .Select(part => Json.Text(part, "text")));
        }
        private static void PublicActivity(SessionFile file, string message, DateTime at)
        {
            message = Json.Clean(message, 240);
            if (message == "") return;
            file.LastActivity = Json.Clean(message, 180);
            if (file.Agent.Ended == DateTime.MinValue && file.Agent.State != "attention" && file.Agent.State != "waiting") file.Agent.Task = file.LastActivity;
            Record(file.Agent, message, at);
        }
        private static void AssistantMessage(SessionFile file, Dictionary<string, object> item, DateTime at, bool publicByDefault)
        {
            var phase = Json.Text(item, "phase");
            var channel = Json.Text(item, "channel");
            if (phase == "analysis" || channel == "analysis") return;
            var final = phase == "final_answer" || channel == "final";
            if (!final && !(phase == "commentary" || channel == "commentary" || (publicByDefault && phase == "" && channel == ""))) return;
            var message = final ? "" : PublicText(item);
            if (!final && String.IsNullOrWhiteSpace(message)) return;
            var id = Json.Text(item, "id");
            if (id != "")
            {
                if (!file.MessageIds.Add(id)) return;
                file.MessageOrder.Enqueue(id);
                if (file.MessageOrder.Count > 256) file.MessageIds.Remove(file.MessageOrder.Dequeue());
            }
            if (final)
            {
                ClearPending(file); file.Agent.State = "completed"; file.Agent.Ended = at; file.Agent.Task = "Work complete";
                Record(file.Agent, file.Agent.Task, at);
            }
            else PublicActivity(file, message, at);
        }
        private static string CommandActivity(object value)
        {
            var args = value as object[];
            var command = value as string ?? (args == null ? "" : String.Join(" ", args.OfType<string>()));
            if (args != null)
                for (int i = 0; i + 1 < args.Length; i++)
                    if (new[] { "-command", "-c", "/c" }.Contains(Convert.ToString(args[i], CultureInfo.InvariantCulture).ToLowerInvariant())) { command = args[i + 1] as string ?? ""; break; }
            if (command.Length > 32768) command = command.Substring(0, 32768);
            // Classify locally, but never put raw arguments, paths, or output in the UI.
            var start = @"(?i)(?:^|[;&|\r\n])\s*(?:&\s+)?['""]?(?:[\w .:\\/-]*[\\/])?";
            if (Regex.IsMatch(command, start + @"(?:(?:pytest|jest|vitest)\b|python(?:3|\.exe)?\s+-m\s+pytest\b|(?:dotnet|cargo|npm|pnpm|yarn|bun)\s+(?:run\s+)?test\b|build\.ps1\b[^\r\n]*\s-test\b)")) return "checks";
            if (Regex.IsMatch(command, start + @"(?:build\.ps1\b|(?:dotnet|cargo|npm|pnpm|yarn|bun)\s+(?:run\s+)?build\b)")) return "build";
            if (Regex.IsMatch(command, start + @"git\s+(status|diff|log|show)\b")) return "changes";
            if (Regex.IsMatch(command, start + @"(rg|grep|select-string)\b")) return "search";
            if (Regex.IsMatch(command, start + @"(get-content|read_mcp_resource)\b")) return "read";
            return "command";
        }
        private static string CommandLabel(string activity, bool finished)
        {
            switch (activity)
            {
                case "checks": return finished ? "Checks finished" : "Running checks";
                case "build": return finished ? "Build finished" : "Building the project";
                case "changes": return finished ? "Inspected project changes" : "Inspecting project changes";
                case "search": return finished ? "Project search finished" : "Searching project files";
                case "read": return finished ? "Read project context" : "Reading project context";
                default: return finished ? "Command finished" : "Running a command";
            }
        }
        private static string CallLabel(string name, Dictionary<string, object> payload)
        {
            if (name.EndsWith("exec_command", StringComparison.Ordinal))
            {
                var arguments = Json.Map(payload, "arguments") ?? Json.Parse(Json.Text(payload, "arguments"));
                return CommandLabel(CommandActivity(Json.Get(arguments, "cmd")), false);
            }
            if (name.Split('.').Last() == "exec")
            {
                // Only recognize explicit nested tool names; never evaluate wrapper JavaScript.
                var names = Regex.Matches(Json.Text(payload, "input"), @"\btools\.([A-Za-z_][A-Za-z0-9_]*)\s*\(")
                    .Cast<Match>().Select(match => match.Groups[1].Value).Distinct().ToList();
                if (names.Count == 1) return ToolLabel(names[0]);
                if (names.Count > 1) return "Running " + names.Count + " tool calls";
            }
            return ToolLabel(name);
        }
        private static void ItemActivity(SessionFile file, Dictionary<string, object> item, bool finished, DateTime at)
        {
            var type = Json.Text(item, "type");
            if (type == "AgentMessage") { if (finished) AssistantMessage(file, item, at, true); return; }
            var status = Json.Text(item, "status");
            var failed = status == "failed" || status == "declined";
            string label;
            if (type == "CommandExecution")
            {
                int code;
                var hasCode = Int32.TryParse(Convert.ToString(Json.Get(item, "exit_code"), CultureInfo.InvariantCulture), out code);
                failed |= hasCode && code != 0;
                var activity = CommandActivity(Json.Get(item, "command"));
                label = failed ? (activity == "checks" ? "Checks failed" : activity == "build" ? "Build failed" : "Command failed") + (hasCode ? " (exit " + code + ")" : "") : CommandLabel(activity, finished);
            }
            else if (type == "FileChange")
            {
                var changes = Json.Map(item, "changes");
                var count = changes == null ? 0 : changes.Count;
                var files = count == 0 ? "project files" : count + " project file" + (count == 1 ? "" : "s");
                label = failed ? "File update failed" : (finished && status == "completed" ? "Updated " : "Updating ") + files;
            }
            else return; // No reasoning, prompts, command output, diffs, or unknown item contents.
            if (finished) file.LastActivity = label;
            if (file.Agent.Ended == DateTime.MinValue && file.Agent.State != "attention" && file.Agent.State != "waiting") file.Agent.Task = label;
            Record(file.Agent, label, at, failed ? "error" : "working");
        }
        private void ApplySession(SessionFile file, Dictionary<string, object> row)
        {
            if (row == null) return;
            var type = Json.Text(row, "type");
            var payload = Json.Map(row, "payload");
            if (payload == null) return;
            var agent = file.Agent;
            var at = Json.Time(row, "timestamp", DateTime.UtcNow);
            var kind = Json.Text(payload, "type");
            if (type == "event_msg")
            {
                if (kind == "task_started" || kind == "turn_started") { ClearPending(file); agent.Started = at; agent.Ended = DateTime.MinValue; agent.State = "working"; agent.Task = "Starting work"; Record(agent, "Started working", at); }
                else if (kind == "task_complete" || kind == "task_completed" || kind == "turn_complete") { ClearPending(file); agent.State = "completed"; agent.Ended = at; agent.Task = "Work complete"; Record(agent, "Completed the task", at); }
                else if (kind == "turn_aborted") { ClearPending(file); agent.State = "idle"; agent.Ended = at; agent.Task = "Task interrupted"; Record(agent, agent.Task, at); }
                else if (kind == "error") { ClearPending(file); agent.State = "error"; agent.Task = "A task encountered an error"; agent.Ended = at; Record(agent, agent.Task, at); }
                else if (kind.Contains("approval") && (kind.Contains("resolved") || kind.Contains("response") || kind.Contains("approved") || kind.Contains("denied") || kind.Contains("completed")))
                {
                    var callId = CorrelationId(payload);
                    if (callId != "") file.AttentionCalls.Remove(callId); else file.UnkeyedAttention = "";
                    if (agent.Ended == DateTime.MinValue) PendingState(file, "working", "Reviewing results");
                }
                else if (kind.Contains("approval") && (kind.Contains("request") || kind.Contains("required")))
                {
                    var callId = CorrelationId(payload);
                    if (callId == "") file.UnkeyedAttention = "Approval requested in Codex";
                    else file.AttentionCalls[callId] = "Approval requested in Codex";
                    PendingState(file, "attention", "Approval requested in Codex"); Record(agent, agent.Task, at);
                }
                else if (kind == "agent_message")
                {
                    PublicActivity(file, Json.Text(payload, "message"), at);
                }
                else if (kind == "item_completed" || kind == "item_started" || kind == "item_updated") ItemActivity(file, Json.Map(payload, "item"), kind == "item_completed", at);
                // Activity is derived only from public text and allowlisted action metadata.
                if (kind != "token_count" && kind != "thread_settings_applied") agent.Updated = at;
            }
            else if (type == "response_item")
            {
                if (kind == "function_call" || kind == "custom_tool_call")
                {
                    var name = Json.Text(payload, "name");
                    var callId = Json.Text(payload, "call_id");
                    var label = CallLabel(name, payload);
                    if (callId != "") file.Tools[callId] = new PendingTool { Name = name, Label = label };
                    if (name.Contains("request_user_input") || name.Contains("request_approval"))
                    {
                        var attention = name.Contains("request_user_input") ? "Waiting for your input in Codex" : "Approval requested in Codex";
                        if (callId == "") file.UnkeyedAttention = attention; else file.AttentionCalls[callId] = attention;
                    }
                    // Bound ordinary calls without dropping unanswered input/approval requests.
                    if (file.Tools.Count > 512)
                        foreach (var key in file.Tools.Keys.Where(key => !file.AttentionCalls.ContainsKey(key)).Take(file.Tools.Count - 512).ToList()) file.Tools.Remove(key);
                    file.LastActivity = "";
                    PendingState(file, name.Contains("wait") ? "waiting" : "working", label);
                    agent.Updated = at; Record(agent, agent.Task, at);
                }
                else if (kind == "function_call_output" || kind == "custom_tool_call_output")
                {
                    // A result means the tool returned, not that the agent finished its turn.
                    var callId = Json.Text(payload, "call_id");
                    if (callId != "") { file.Tools.Remove(callId); file.AttentionCalls.Remove(callId); }
                    agent.Updated = at;
                    if (agent.Ended == DateTime.MinValue) PendingState(file, "working", file.LastActivity != "" ? file.LastActivity : "Reviewing results");
                }
                else if (kind == "message" && Json.Text(payload, "role") == "assistant")
                {
                    AssistantMessage(file, payload, at, false);
                    agent.Updated = at;
                }
            }
        }
        private void ApplyCustom(Dictionary<string, object> row)
        {
            if (row == null) return;
            var id = Json.Text(row, "agent_id");
            var kind = Json.Text(row, "type");
            if (id == "" || !new[] { "agent.started", "agent.updated", "agent.waiting", "agent.attention", "agent.completed", "agent.error" }.Contains(kind)) return;
            var at = Json.Time(row, "timestamp", DateTime.UtcNow);
            Agent agent;
            if (!customAgents.TryGetValue(id, out agent))
            {
                if (customAgents.Count >= 200) { var oldest = customAgents.Values.Where(a => a.State == "completed" || a.State == "idle").OrderBy(a => a.Updated).FirstOrDefault(); if (oldest == null) return; customAgents.Remove(oldest.Id); }
                agent = new Agent { Id = id, Name = "Agent", Task = "Starting work", Role = "builder", State = "working", Started = at, Updated = at };
                customAgents[id] = agent;
            }
            if (at < agent.Updated) return;
            if (Json.Text(row, "name") != "") agent.Name = Json.Clean(Json.Text(row, "name"), 32);
            if (Json.Text(row, "task") != "") agent.Task = Json.Clean(Json.Text(row, "task"), 180);
            if (Json.Text(row, "role") != "") agent.Role = Json.Text(row, "role");
            if (Json.Text(row, "parent_id") != "") agent.ParentId = Json.Text(row, "parent_id");
            agent.State = kind == "agent.completed" ? "completed" : kind == "agent.waiting" ? "waiting" : kind == "agent.attention" ? "attention" : kind == "agent.error" ? "error" : "working";
            if (kind == "agent.started") agent.Started = at;
            agent.Ended = agent.State == "completed" || agent.State == "error" ? at : DateTime.MinValue;
            agent.Updated = at;
            Record(agent, Json.Text(row, "message") != "" ? Json.Text(row, "message") : agent.Task, at);
        }
    }

    public sealed class DemoFeed
    {
        private readonly DateTime began = DateTime.UtcNow;
        public Snapshot Poll()
        {
            var seconds = (int)(DateTime.UtcNow - began).TotalSeconds % 96;
            var cycle = (int)(DateTime.UtcNow - began).TotalSeconds / 96;
            var origin = began.AddSeconds(cycle * 96 - 37);
            var data = new Snapshot { Project = "workspace / agent-studio", Source = "Preview · simulated activity", Connected = true, Session = "demo" };
            var explorer = new Agent { Id = "explorer", Name = "Explorer", Role = "explorer", State = "completed", Task = "Mapped the project architecture", Started = origin, Ended = origin.AddSeconds(31), Updated = origin.AddSeconds(31) };
            var builder = new Agent { Id = "builder", Name = "Builder", Role = "builder", State = seconds >= 69 ? "completed" : "working", Task = seconds < 18 ? "Shaping the interface" : seconds < 40 ? "Connecting the activity stream" : seconds < 69 ? "Polishing the finishing touches" : "Interface ready for review", Started = origin.AddSeconds(8), Updated = DateTime.UtcNow, Ended = seconds >= 69 ? origin.AddSeconds(106) : DateTime.MinValue };
            var reviewer = new Agent { Id = "reviewer", Name = "Reviewer", Role = "reviewer", State = seconds >= 38 && seconds < 53 ? "attention" : seconds >= 80 ? "completed" : "working", Task = seconds >= 38 && seconds < 53 ? "Needs your input on an approach" : seconds < 25 ? "Checking accessibility and contrast" : seconds < 65 ? "Reviewing interaction details" : "All review checks passed", Started = origin.AddSeconds(16), Updated = DateTime.UtcNow, Ended = seconds >= 80 ? origin.AddSeconds(117) : DateTime.MinValue };
            var tester = new Agent { Id = "tester", ParentId = "builder", Name = "Tests", Role = "tester", State = seconds < 25 ? "waiting" : seconds >= 87 ? "completed" : "working", Task = seconds < 25 ? "Waiting for the first build" : seconds < 55 ? "Verifying window interactions" : seconds < 87 ? "Checking the activity connection" : "All checks passed", Started = origin.AddSeconds(24), Updated = DateTime.UtcNow, Ended = seconds >= 87 ? origin.AddSeconds(124) : DateTime.MinValue };
            data.Agents.AddRange(new[] { builder, reviewer, tester, explorer });
            foreach (var agent in data.Agents)
            {
                agent.Events.Add(new ActivityEvent { AgentId = agent.Id, Agent = agent.Name, Text = "Picked up the task", State = "working", At = agent.Started });
                agent.Events.Add(new ActivityEvent { AgentId = agent.Id, Agent = agent.Name, Text = agent.Task, State = agent.State, At = agent.State == "completed" ? agent.Ended : origin.AddSeconds(37 + (seconds / 8) * 8) });
            }
            data.Events = data.Agents.SelectMany(a => a.Events).OrderByDescending(e => e.At).ToList();
            return data;
        }
    }
}
