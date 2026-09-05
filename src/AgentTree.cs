using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Orbit
{
    public sealed class TreeNode : INotifyPropertyChanged
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        public Agent Agent { get; set; }
        public ActivityEvent Activity { get; set; }
        public string Caption { get; set; }
        public bool Seeded;
        public readonly ObservableCollection<TreeNode> Items = new ObservableCollection<TreeNode>();
        public ObservableCollection<TreeNode> Children { get { return Items; } }
        private bool expanded;
        public bool IsExpanded { get { return expanded; } set { if (expanded == value) return; expanded = value; Notify(); } }
        public string Title { get { return Kind == "agent" ? Agent.DisplayName : Kind == "event" ? Activity.Text : Caption; } }
        public string Subtitle { get { return Kind == "agent" ? Agent.Task : ""; } }
        public string Status { get { return Kind == "agent" ? Agent.StatusLabel : ""; } }
        public System.Windows.Media.Brush StatusBrush { get { return Kind == "agent" ? Agent.AccentBrush : Palette.State("idle"); } }
        public string Elapsed { get { return Kind == "agent" ? Agent.Elapsed : Kind == "event" ? Activity.At.ToLocalTime().ToString("HH:mm:ss") : ""; } }
        public string AccessibleName { get { return Kind == "agent" ? Agent.AccessibleName : Title; } }
        public int ReplyCount { get { return Children.Count(c => c.Kind == "agent"); } }
        public bool HasReplies { get { return ReplyCount > 0; } }
        public string ReplyLabel { get { return ReplyCount == 1 ? "1 reply" : ReplyCount + " replies"; } }
        public string Hint { get { return Kind == "agent" ? Agent.Name + "\n" + Agent.Task + "\nLast update " + (Agent.Updated == DateTime.MinValue ? "unknown" : Agent.Updated.ToLocalTime().ToString("HH:mm:ss")) + " · " + Agent.Id : Kind == "event" ? Activity.Text : Title; } }
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
                var hasAgents = children.Count > 0;
                foreach (var activity in agent.Events
                    .GroupBy(e => e.At.Ticks + "\n" + e.Text).Select(g => g.First())
                    .OrderByDescending(e => e.At).Take(5))
                {
                    var key = "event:" + agent.Id + ":" + activity.At.Ticks + ":" + activity.Text;
                    var item = Get(key, "event", used); item.Activity = activity; item.Notify(); children.Add(item);
                }
                Sync(node.Items, children);
                if (!node.Seeded) { node.IsExpanded = agent.IsRoot || hasAgents || agent.State == "working" || agent.State == "attention" || agent.State == "error" || agent.State == "waiting"; node.Seeded = true; }
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
