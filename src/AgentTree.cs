using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace OrbitEvolved
{
    public sealed class TreeNode : INotifyPropertyChanged
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        public Agent Agent { get; set; }
        public string Caption { get; set; }
        public string Description { get; set; }
        private string chatUpdate;
        public string ChatUpdate { get { return chatUpdate ?? ""; } set { chatUpdate = value; } }
        public string GroupHint { get; set; }
        public ChatSummary Chat { get; set; }
        public bool CanOpenInCodex { get { return Chat != null && CodexLink.ForChat(Chat.Id) != null; } }
        public string CodexAccessibleName { get { return "Open " + Caption + " in Codex"; } }
        private static readonly System.Windows.Media.Brush ChatMuted = Palette.Color("#9BA3B0");
        private static readonly System.Windows.Media.Brush ChatAttention = Palette.State("attention");
        internal bool ChatNeedsAttention;
        public System.Windows.Media.Brush ChatDescriptionBrush { get { return ChatNeedsAttention ? ChatAttention : ChatMuted; } }
        public bool Seeded;
        public readonly ObservableCollection<TreeNode> Items = new ObservableCollection<TreeNode>();
        public ObservableCollection<TreeNode> Children { get { return Items; } }
        private bool expanded;
        public bool IsExpanded { get { return expanded; } set { if (expanded == value) return; expanded = value; Notify(); } }
        public string Title { get { return Kind == "agent" ? Agent.DisplayName : Caption; } }
        public string Subtitle { get { return Kind == "agent" ? ActivityLinks.PlainSummary(Agent.ActivityDetail) : ""; } }
        public string Status { get { return Kind == "agent" ? Agent.StatusLabel : ""; } }
        public System.Windows.Media.Brush StatusBrush { get { return Kind == "agent" ? Agent.AccentBrush : Palette.State("idle"); } }
        public string AccessibleName
        {
            get
            {
                if (Kind == "agent") return Agent.AccessibleName;
                return Kind == "chat" && !String.IsNullOrEmpty(Description)
                    ? Title + ", " + Description + (ChatUpdate == "" ? "" : ", " + ChatUpdate) : Title;
            }
        }
        public string Hint { get { return Kind == "agent" ? Agent.AccessibleName : GroupHint ?? Title; } }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Notify() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("")); }
    }

    // Keep node identity stable so background updates preserve expansion and selection.
    public sealed class AgentTree
    {
        public readonly ObservableCollection<TreeNode> Roots = new ObservableCollection<TreeNode>();
        private readonly Dictionary<string, TreeNode> nodes = new Dictionary<string, TreeNode>();
        public TreeNode Find(string key) { TreeNode node; return nodes.TryGetValue(key, out node) ? node : null; }
        private TreeNode Get(string key, string kind, HashSet<string> used)
        {
            TreeNode node;
            if (!nodes.TryGetValue(key, out node)) { node = new TreeNode { Key = key, Kind = kind }; nodes[key] = node; }
            used.Add(key); return node;
        }
        public void Clear() { nodes.Clear(); Roots.Clear(); }
        public void Refresh() { foreach (var node in nodes.Values.Where(n => n.Kind == "agent")) node.Notify(); }
        public void UpdateCatalog(IEnumerable<ChatSummary> chats)
        {
            var used = new HashSet<string>();
            var roots = new List<TreeNode>();
            foreach (var group in chats.GroupBy(chat => chat.ProjectKey))
            {
                var first = group.First();
                var project = Get("project:" + group.Key, "project", used);
                project.Caption = first.ProjectName;
                project.GroupHint = first.ProjectHint == "" ? first.ProjectName : first.ProjectHint;
                if (!project.Seeded) { project.IsExpanded = roots.Count == 0; project.Seeded = true; }
                var items = new List<TreeNode>();
                foreach (var chat in group)
                {
                    var node = Get("chat:" + chat.Id, "chat", used);
                    node.Chat = chat;
                    node.Caption = chat.Title;
                    node.Description = chat.Updated.ToLocalTime().ToString("dd MMM yyyy · HH:mm") + (chat.Archived ? " · Archived" : "");
                    node.GroupHint = node.Caption + "\n" + chat.Id;
                    node.Notify();
                    items.Add(node);
                }
                project.Description = items.Count == 1 ? "1 chat" : items.Count + " chats";
                Sync(project.Items, items);
                project.Notify();
                roots.Add(project);
            }
            Sync(Roots, roots);
            foreach (var key in nodes.Keys.Where(key => !used.Contains(key)).ToList()) nodes.Remove(key);
        }
        internal void UpdateActiveCatalog(IEnumerable<ActiveChatEntry> active)
        {
            var used = new HashSet<string>();
            var rows = new List<TreeNode>();
            foreach (var entry in active)
            {
                var chat = entry.Chat;
                var row = Get("chat:" + chat.Id, "chat", used);
                row.Chat = chat;
                row.Caption = chat.Title;
                row.Description = entry.Status + " · " + chat.ProjectName;
                row.ChatNeedsAttention = entry.NeedsAttention;
                row.ChatUpdate = entry.Update;
                row.GroupHint = row.Caption + "\n" + row.Description + (row.ChatUpdate == "" ? "" : "\n" + row.ChatUpdate);
                row.Notify();
                rows.Add(row);
            }
            Sync(Roots, rows);
            foreach (var key in nodes.Keys.Where(key => !used.Contains(key)).ToList()) nodes.Remove(key);
        }

        public void Update(IEnumerable<Agent> source, string session, string project)
        {
            var all = source.GroupBy(a => a.Id).Select(g => g.First()).OrderBy(a => a.Started).ThenBy(a => a.Id).ToList();
            var lookup = all.ToDictionary(a => a.Id);
            var used = new HashSet<string>();
            var parents = new Dictionary<string, string>();
            foreach (var agent in all)
            {
                var parent = agent.IsRoot ? "" : agent.ParentId ?? "";
                var seen = new HashSet<string> { agent.Id };
                var cursor = parent;
                while (cursor != "" && lookup.ContainsKey(cursor))
                {
                    if (!seen.Add(cursor)) { parent = ""; break; }
                    cursor = lookup[cursor].ParentId ?? "";
                }
                parents[agent.Id] = lookup.ContainsKey(parent) ? parent : "";
                var node = Get("agent:" + agent.Id, "agent", used); node.Agent = agent;
            }
            foreach (var agent in all)
            {
                var node = nodes["agent:" + agent.Id];
                var children = all.Where(a => parents[a.Id] == agent.Id).Select(a => nodes["agent:" + a.Id]).ToList();
                Sync(node.Items, children);
                if (!node.Seeded && children.Count > 0) { node.IsExpanded = true; node.Seeded = true; }
                node.Notify();
            }
            var roots = all.Where(a => parents[a.Id] == "")
                .OrderBy(a => a.State == "attention" || a.State == "error" ? 0 : a.State == "working" ? 1 : a.State == "waiting" ? 2 : 3)
                .ThenByDescending(a => a.Started).ThenBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => nodes["agent:" + a.Id]).ToList();
            var allRootsAreLeads = roots.Count > 0 && roots.All(n => n.Agent.IsRoot);
            if (!allRootsAreLeads && roots.Count > 0 && !(roots.Count == 1 && roots[0].Agent.IsRoot))
            {
                var sessionNode = Get("session:" + session + ":" + project, "session", used);
                sessionNode.Caption = "Session";
                if (!sessionNode.Seeded) { sessionNode.IsExpanded = true; sessionNode.Seeded = true; }
                Sync(sessionNode.Items, roots); sessionNode.Notify(); roots = new List<TreeNode> { sessionNode };
            }
            Sync(Roots, roots);
            foreach (var key in nodes.Keys.Where(k => !used.Contains(k)).ToList()) nodes.Remove(key);
        }
        private static void Sync(ObservableCollection<TreeNode> target, List<TreeNode> next)
        {
            foreach (var removed in target.Where(n => !next.Contains(n)).ToList()) target.Remove(removed);
            for (int i = 0; i < next.Count; i++)
            {
                var index = target.IndexOf(next[i]);
                if (index < 0) target.Insert(i, next[i]); else if (index != i) target.Move(index, i);
            }
        }
    }
}
