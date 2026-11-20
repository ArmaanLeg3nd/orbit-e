using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbitEvolved
{
    internal enum AgentGraphIssue { None, MissingParent, Cycle }

    internal sealed class AgentGraphNode
    {
        public Agent Agent;
        public string ParentId;
        public AgentGraphIssue Issue;
        public int Depth;
        public double Row;
        public bool CanCollapse;
        public int FoldedCount;
        public readonly List<AgentGraphNode> Children = new List<AgentGraphNode>();
    }

    internal sealed class AgentGraphLayout
    {
        public readonly List<AgentGraphNode> Nodes = new List<AgentGraphNode>();
        public int ColumnCount;
        public double RowCount;
        public int EdgeCount;

        public static List<Agent> OrderedAgents(IEnumerable<Agent> source)
        {
            return source.Where(agent => agent != null && !String.IsNullOrEmpty(agent.Id))
                .GroupBy(agent => agent.Id, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(agent => agent.Started).ThenBy(agent => agent.Id, StringComparer.Ordinal).ToList();
        }

        public static AgentGraphLayout Create(IEnumerable<Agent> source)
        {
            return FromOrdered(OrderedAgents(source));
        }

        public static AgentGraphLayout FromOrdered(List<Agent> ordered, ISet<string> folded = null, string selected = null)
        {
            var layout = new AgentGraphLayout();
            var byId = ordered.ToDictionary(agent => agent.Id, agent => new AgentGraphNode { Agent = agent }, StringComparer.Ordinal);
            foreach (var node in byId.Values)
            {
                var parentId = node.Agent.RecordedParentId ?? node.Agent.ParentId;
                if (String.IsNullOrEmpty(parentId))
                {
                    if (!String.IsNullOrEmpty(node.Agent.ParentId)) node.Issue = AgentGraphIssue.MissingParent;
                }
                else if (byId.ContainsKey(parentId)) node.ParentId = parentId;
                else node.Issue = AgentGraphIssue.MissingParent;
            }
            RemoveCycles(byId);

            var roots = new List<AgentGraphNode>();
            foreach (var agent in ordered)
            {
                var node = byId[agent.Id];
                if (node.ParentId == null) roots.Add(node);
                else
                {
                    byId[node.ParentId].Children.Add(node);
                }
            }

            var subtreeSizes = FindFinishedBranches(roots);
            var protectedPath = new HashSet<string>(StringComparer.Ordinal);
            AgentGraphNode retained;
            if (selected != null && byId.TryGetValue(selected, out retained))
                for (var parent = retained.ParentId; parent != null; parent = byId[parent].ParentId)
                    protectedPath.Add(parent);

            var nextRow = 0.0;
            foreach (var root in roots.OrderByDescending(node => node.Agent.IsRoot))
            {
                if (nextRow > 0) nextRow += 0.4;
                var pending = new Stack<AgentGraphNode>();
                var parents = new Stack<AgentGraphNode>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    layout.Nodes.Add(node);
                    layout.ColumnCount = Math.Max(layout.ColumnCount, node.Depth + 1);
                    if (folded != null && node.CanCollapse && folded.Contains(node.Agent.Id) && !protectedPath.Contains(node.Agent.Id))
                    {
                        node.FoldedCount = subtreeSizes[node];
                        node.Children.Clear();
                    }
                    layout.EdgeCount += node.Children.Count;
                    if (node.Children.Count == 0) node.Row = nextRow++;
                    else parents.Push(node);
                    for (int index = node.Children.Count - 1; index >= 0; index--)
                    {
                        var child = node.Children[index];
                        child.Depth = node.Depth + 1;
                        pending.Push(child);
                    }
                }
                while (parents.Count > 0)
                {
                    var parent = parents.Pop();
                    parent.Row = (parent.Children[0].Row + parent.Children[parent.Children.Count - 1].Row) / 2;
                }
            }
            layout.RowCount = nextRow;
            return layout;
        }

        private static Dictionary<AgentGraphNode, int> FindFinishedBranches(IEnumerable<AgentGraphNode> roots)
        {
            var pending = new Stack<AgentGraphNode>(roots);
            var order = new Stack<AgentGraphNode>();
            while (pending.Count != 0)
            {
                var node = pending.Pop();
                order.Push(node);
                foreach (var child in node.Children) pending.Push(child);
            }
            var finished = new Dictionary<AgentGraphNode, int>();
            while (order.Count != 0)
            {
                var node = order.Pop();
                var done = node.Agent.State == "completed" && node.Issue == AgentGraphIssue.None &&
                    node.Children.All(finished.ContainsKey);
                if (!done) continue;
                finished[node] = 1 + node.Children.Sum(child => finished[child]);
                node.CanCollapse = node.Children.Count != 0;
            }
            return finished;
        }

        private static void RemoveCycles(Dictionary<string, AgentGraphNode> byId)
        {
            var examined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in byId.Values)
            {
                var node = start;
                var path = new List<AgentGraphNode>();
                var indexInPath = new Dictionary<string, int>(StringComparer.Ordinal);
                while (node != null && !examined.Contains(node.Agent.Id))
                {
                    int cycleStart;
                    if (indexInPath.TryGetValue(node.Agent.Id, out cycleStart))
                    {
                        for (int index = cycleStart; index < path.Count; index++)
                        {
                            path[index].ParentId = null;
                            path[index].Issue = AgentGraphIssue.Cycle;
                        }
                        break;
                    }
                    indexInPath[node.Agent.Id] = path.Count;
                    path.Add(node);
                    node = node.ParentId == null ? null : byId[node.ParentId];
                }
                foreach (var item in path) examined.Add(item.Agent.Id);
            }
        }
    }
}
