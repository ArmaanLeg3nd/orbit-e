using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Orbit
{
    public static class ActivityReaderChecks
    {
        private static readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public static void Run(Action<bool, string> check, string tempRoot)
        {
            var root = Path.Combine(tempRoot, "reader-regressions");
            Directory.CreateDirectory(root);
            CheckAncestry(check, root);
            CheckNames(check, root);
            CheckPendingCalls(check, root);
            CheckHistoricalPin(check, root);
        }

        private static void CheckAncestry(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "ancestry");
            Session(folder, "root", "", "", root, null);
            Session(folder, "child", "root", "", root, null);
            Session(folder, "grandchild", "child", "", root, null);
            Session(folder, "orphan", "missing-parent", "root", root, null);
            Session(folder, "orphan-leaf", "orphan", "", root, null);
            var reader = new ActivityReader(root, "root", "", folder);
            var snapshot = reader.Poll();
            check(snapshot.Agents.Count == 5 && snapshot.Agents.Single(a => a.Id == "grandchild").ParentId == "child", "Session ancestry includes grandchildren without explicit session IDs");
            check(snapshot.Agents.Any(a => a.Id == "orphan" && a.ParentId == "missing-parent") && snapshot.Agents.Any(a => a.Id == "orphan-leaf"), "Missing-parent branches retain their declared session and descendants");
            var childTree = new ActivityReader(root, "child", "", folder).Poll();
            check(childTree.Agents.Count == 2 && childTree.Agents.Any(a => a.Id == "grandchild"), "Pinning a child includes its descendants without including siblings");

            var orphanFolder = Path.Combine(root, "orphan-only");
            Session(orphanFolder, "only-child", "absent-root", "", root, null);
            Session(orphanFolder, "only-grandchild", "only-child", "", root, null);
            var orphanTree = new ActivityReader(root, "", "", orphanFolder).Poll();
            check(orphanTree.Connected && orphanTree.Session == "absent-root" && orphanTree.Agents.Count == 2, "Automatic selection can show an orphan tree before its parent rollout arrives");

            var cycleFolder = Path.Combine(root, "cycle");
            Session(cycleFolder, "cycle-a", "cycle-b", "", root, null);
            Session(cycleFolder, "cycle-b", "cycle-a", "", root, null);
            Session(cycleFolder, "cycle-leaf", "cycle-b", "", root, null);
            var cycleTree = new ActivityReader(root, "cycle-a", "", cycleFolder).Poll();
            check(cycleTree.Agents.Count == 3 && cycleTree.Agents.Single(a => a.Id == "cycle-a").ParentId == "" && cycleTree.Agents.Single(a => a.Id == "cycle-b").ParentId == "cycle-a", "A malformed ancestry cycle is broken deterministically without dropping agents");

            var declaredCycleFolder = Path.Combine(root, "declared-cycle");
            Session(declaredCycleFolder, "root", "", "root", root, null);
            Session(declaredCycleFolder, "cycle-a", "cycle-b", "root", root, null);
            Session(declaredCycleFolder, "cycle-b", "cycle-a", "root", root, null);
            var declaredCycle = new ActivityReader(root, "root", "", declaredCycleFolder).Poll();
            check(declaredCycle.Agents.Count == 3 && declaredCycle.Agents.Single(a => a.Id == "cycle-a").ParentId == "root", "Cycle recovery retains a recorded root session when available");
        }

        private static void CheckNames(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "names");
            Session(folder, "root", "", "", root, null);
            Session(folder, "explicit", "root", "", root, new Dictionary<string, object> { { "task_name", "Audit session reader" }, { "agent_path", "/root/reader_review" }, { "agent_nickname", "Darwin" } });
            Session(folder, "path", "root", "", root, new Dictionary<string, object> { { "source", new { subagent = new { thread_spawn = new { parent_thread_id = "root", agent_path = "/root/reviewer/nested_checks/", agent_nickname = "Franklin" } } } } });
            Session(folder, "nickname", "root", "", root, new Dictionary<string, object> { { "agent_nickname", "Zeno" } });
            Session(folder, "12345678-rest", "root", "", root, null);
            var names = new ActivityReader(root, "root", "", folder).Poll().Agents.ToDictionary(a => a.Id, a => a.Name);
            check(names["explicit"] == "Audit session reader", "An explicit task name takes precedence over the agent path and nickname");
            check(names["path"] == "nested_checks", "Nested spawn metadata supplies the assigned agent path name");
            check(names["nickname"] == "Zeno" && names["12345678-rest"] == "Agent 12345678", "Names fall back to recorded nicknames and then a stable ID");
        }

        private static void CheckPendingCalls(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "pending");
            var path = Session(folder, "root", "", "root", root, null);
            var reader = new ActivityReader(root, "root", "", folder);
            reader.Poll();
            Call(path, "command-1", "functions.exec_command");
            Call(path, "input-1", "functions.request_user_input");
            Result(path, "command-1");
            check(reader.Poll().Agents.Single().State == "attention", "An unrelated command result cannot clear a pending input request");
            Call(path, "command-2", "functions.exec_command");
            check(reader.Poll().Agents.Single().State == "attention", "A new ordinary tool call cannot hide a pending input request");
            Call(path, "input-2", "functions.request_user_input");
            Result(path, "input-1");
            check(reader.Poll().Agents.Single().State == "attention", "Resolving one input request preserves another outstanding request");
            Result(path, "input-2");
            check(reader.Poll().Agents.Single().State == "working", "The matching final input result resumes outstanding work");
            Call(path, "wait-1", "collaboration.wait_agent");
            Result(path, "command-2");
            check(reader.Poll().Agents.Single().State == "waiting", "A pending wait remains visible after another tool returns");
            Result(path, "wait-1");

            Event(path, new { type = "approval_requested", call_id = "approval-1" });
            Call(path, "command-3", "functions.exec_command");
            Result(path, "command-3");
            check(reader.Poll().Agents.Single().State == "attention", "An unrelated result cannot clear a keyed approval request");
            Event(path, new { type = "approval_requested", call_id = "approval-2" });
            Event(path, new { type = "approval_resolved", call_id = "approval-1" });
            check(reader.Poll().Agents.Single().State == "attention", "An approval response resolves only the matching call ID");
            Event(path, new { type = "approval_resolved", call_id = "approval-2" });
            check(reader.Poll().Agents.Single().State == "working", "Resolving the last approval request resumes work");

            Event(path, new { type = "approval_required" });
            Result(path, "unknown-call");
            check(reader.Poll().Agents.Single().State == "attention", "An approval without a correlation ID survives unrelated results");
            Event(path, new { type = "approval_resolved" });
            check(reader.Poll().Agents.Single().State == "working", "An explicit unkeyed approval resolution clears unkeyed attention");
            Event(path, new { type = "task_complete" });
            Result(path, "late-result");
            check(reader.Poll().Agents.Single().State == "completed", "Late tool results do not revive a completed turn");

            Event(path, new { type = "task_started" });
            Call(path, "old-input", "functions.request_user_input");
            check(reader.Poll().Agents.Single().State == "attention", "A new turn can acquire its own pending input request");
            File.WriteAllText(path, "", new UTF8Encoding(false));
            Session(folder, "root", "", "root", root, null);
            var reset = reader.Poll().Agents.Single();
            check(reset.State == "working" && reset.Events.Count == 1, "Truncated session logs clear stale requests and prior event history before replay");
        }

        private static void CheckHistoricalPin(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "history");
            var historical = Path.Combine(folder, "2024", "01", "02");
            Session(historical, "old-root", "", "", root, null);
            Session(historical, "old-child", "old-root", "", root, null);
            var today = DateTime.Now;
            var recent = Path.Combine(folder, today.ToString("yyyy"), today.ToString("MM"), today.ToString("dd"));
            Session(recent, "recent-root", "", "", root, null);
            var snapshot = new ActivityReader(root, "old-root", "", folder).Poll();
            check(snapshot.Session == "old-root" && snapshot.Agents.Count == 2 && snapshot.Agents.Any(a => a.Id == "old-child"), "An explicit session pin discovers an older rollout and its same-day descendants");
        }

        private static string Session(string folder, string id, string parent, string session, string workspace, Dictionary<string, object> extra)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "rollout-" + id + ".jsonl");
            var meta = new Dictionary<string, object> { { "id", id }, { "cwd", workspace }, { "timestamp", DateTime.UtcNow.ToString("o") }, { "source", "cli" } };
            if (parent != "") meta["parent_thread_id"] = parent;
            if (session != "") meta["session_id"] = session;
            if (extra != null) foreach (var entry in extra) meta[entry.Key] = entry.Value;
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "session_meta", payload = meta });
            Event(path, new { type = "task_started" });
            return path;
        }
        private static void Call(string path, string id, string name)
        {
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "response_item", payload = new { type = "function_call", name = name, call_id = id } });
        }
        private static void Result(string path, string id)
        {
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "response_item", payload = new { type = "function_call_output", call_id = id, output = "fixture" } });
        }
        private static void Event(string path, object payload)
        {
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "event_msg", payload = payload });
        }
        private static void Append(string path, object row)
        {
            File.AppendAllText(path, serializer.Serialize(row) + "\n", new UTF8Encoding(false));
        }
    }
}
