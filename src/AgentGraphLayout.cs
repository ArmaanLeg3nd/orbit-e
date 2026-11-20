using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbit
{
    internal enum AgentGraphIssue { None, MissingParent, Cycle }

    internal sealed class AgentGraphNode
    {
        public Agent Agent;
        public string ParentId;
        public AgentGraphIssue Issue;
        public int Depth;
        public double Row;
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

        public static AgentGraphLayout FromOrdered(List<Agent> ordered)
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
                    layout.EdgeCount++;
                }
            }

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
