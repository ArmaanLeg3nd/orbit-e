using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace OrbitEvolved
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
            CheckPublicActivity(check, root);
            CheckActionActivity(check, root);
            CheckHistoricalPin(check, root);
            CheckFreshSelection(check, root);
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
            var loneRootFolder = Path.Combine(root, "names-root-path");
            Session(loneRootFolder, "with-root-path", "", "", root, new Dictionary<string, object> { { "agent_path", "/root" } });
            var rootPathName = new ActivityReader(root, "", "", loneRootFolder).Poll().Agents.Single(a => a.Id == "with-root-path").Name;
            check(rootPathName == "root", "An assigned root path names the lead root");
            check(names["path"] == "nested_checks", "Nested spawn metadata supplies the assigned agent path name");
            check(names["nickname"] == "Zeno" && names["12345678-rest"] == "Agent 12345678", "Names fall back to recorded nicknames and then a stable ID");
            var loneFolder = Path.Combine(root, "names-lone-root");
            Session(loneFolder, "lone", "", "", root, null);
            var lone = new ActivityReader(root, "", "", loneFolder).Poll().Agents.Single(a => a.Id == "lone");
            DateTime loneAt;
            check(lone.Name.StartsWith("Session ") && DateTime.TryParse(lone.Name.Substring(8), out loneAt) && (DateTime.Now - loneAt).TotalMinutes < 5, "A nameless lead shows its session start time instead of a Coordinator ID");
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

        private static void CheckPublicActivity(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "public-activity");
            var path = Session(folder, "root", "", "root", root, null);
            var reader = new ActivityReader(root, "root", "", folder);
            reader.Poll();
            var at = DateTime.UtcNow;
            var message = new { type = "AgentMessage", id = "progress-1", phase = "commentary", content = new[] { new { type = "Text", text = "Verifying the activity parser." } } };
            Item(path, message, at);
            var first = reader.Poll().Agents.Single();
            check(first.Task == "Verifying the activity parser." && first.Events.Last().At == at, "Completed public AgentMessage items update main-agent activity");
            Append(path, new { timestamp = at.AddSeconds(1).ToString("o"), type = "response_item", payload = new { type = "message", id = "progress-1", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text = "Verifying the activity parser." } } } });
            var mirrored = reader.Poll().Agents.Single();
            check(mirrored.Events.Count == first.Events.Count && mirrored.Events.Last().At == at, "Mirrored public messages with the same ID are not replayed");
            Append(path, new { timestamp = at.AddSeconds(2).ToString("o"), type = "response_item", payload = new { type = "message", id = "progress-2", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text = "Verifying the activity parser." } } } });
            var repeated = reader.Poll().Agents.Single();
            check(repeated.Events.Count == first.Events.Count && repeated.Events.Last().At == at.AddSeconds(2), "Repeated activity refreshes its timestamp without adding a redundant row");
            check(first.Events.Last().At == at && !Object.ReferenceEquals(first.Events.Last(), repeated.Events.Last()), "Refreshing a repeated event leaves prior UI snapshots immutable");
            Event(path, new { type = "item_completed", item = new { type = "Reasoning", summary_text = "private reasoning", raw_content = "private reasoning" } });
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "response_item", payload = new { type = "message", role = "assistant", channel = "analysis", content = new[] { new { type = "output_text", text = "private reasoning" } } } });
            Append(path, new { timestamp = DateTime.UtcNow.ToString("o"), type = "response_item", payload = new { type = "message", role = "user", phase = "commentary", content = new[] { new { type = "output_text", text = "private user prompt" } } } });
            Item(path, new { type = "UserMessage", content = "private user prompt" }, DateTime.UtcNow);
            check(reader.Poll().Agents.Single().Events.Count == repeated.Events.Count, "Reasoning, analysis-channel text, and user prompts never enter activity");
            Append(path, new { timestamp = at.AddSeconds(3).ToString("o"), type = "response_item", payload = new { type = "message", role = "assistant", channel = "commentary", content = new object[] { new { type = "output_text", text = "Reviewing" }, new { type = "reasoning_text", text = "private reasoning" }, new { type = "text", text = "the README." } } } });
            check(reader.Poll().Agents.Single().Task == "Reviewing the README.", "Commentary content joins only public text parts");
            Item(path, new { type = "AgentMessage", id = "empty-first", phase = "commentary", content = "" }, at.AddSeconds(4));
            Item(path, new { type = "AgentMessage", id = "empty-first", phase = "commentary", content = "Progress after an empty representation." }, at.AddSeconds(5));
            check(reader.Poll().Agents.Single().Task == "Progress after an empty representation.", "Empty message representations do not suppress later public content with the same ID");
            Item(path, new { type = "AgentMessage", phase = "commentary", channel = "analysis", content = "private reasoning" }, at.AddSeconds(6));
            check(reader.Poll().Agents.Single().Task == "Progress after an empty representation.", "An explicit analysis channel is excluded even with conflicting phase metadata");
            Call(path, "input", "functions.request_user_input");
            Item(path, new { type = "AgentMessage", phase = "commentary", content = "An update while awaiting your choice." }, DateTime.UtcNow);
            var attention = reader.Poll().Agents.Single();
            check(attention.State == "attention" && attention.Task == "Waiting for your input in Codex" && attention.Events.Last().Text.Contains("An update"), "Public progress is recorded without hiding a pending input request");
            Result(path, "input");
            Call(path, "wait", "collaboration.wait_agent");
            Event(path, new { type = "agent_message", message = "The team is reviewing." });
            var waiting = reader.Poll().Agents.Single();
            check(waiting.State == "waiting" && waiting.Task == "Waiting for a task" && waiting.Events.Last().Text == "The team is reviewing.", "Legacy public messages preserve an outstanding wait");
            Result(path, "wait");
            Item(path, new { type = "AgentMessage", phase = "final_answer", content = "Finished." }, DateTime.UtcNow);
            var finished = reader.Poll().Agents.Single();
            check(finished.State == "completed" && finished.ActivitySummary == "Finished.", "Public final answers close the turn and surface the result");
            Item(path, new { type = "AgentMessage", phase = "commentary", content = "Late progress." }, DateTime.UtcNow);
            var done = reader.Poll().Agents.Single();
            check(done.State == "completed" && done.ActivitySummary == "Finished.", "Late progress does not replace a completed agent's result");
        }

        private static void CheckActionActivity(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "action-activity");
            var path = Session(folder, "root", "", "root", root, null);
            var reader = new ActivityReader(root, "root", "", folder);
            var before = reader.Poll().Agents.Single();
            var at = DateTime.UtcNow;
            Append(path, new { timestamp = at.ToString("o"), type = "response_item", payload = new { type = "custom_tool_call", name = "exec", call_id = "wrapper", input = "await tools.apply_patch('private patch');" } });
            check(reader.Poll().Agents.Single().Events.Count == before.Events.Count, "Wrapper calls do not add generic activity");
            Item(path, new { type = "FileChange", id = "edit", status = "completed", changes = new Dictionary<string, object> { { @"C:\private-path\Agent.cs", new { type = "update", diff = "private diff" } }, { "src/Widget.xaml", new { type = "update", diff = "private diff" } } } }, at.AddSeconds(1));
            Result(path, "wrapper");
            var edited = reader.Poll().Agents.Single();
            check(edited.Task == "Updated Agent.cs, Widget.xaml" && edited.State == "working", "Completed edits identify files and survive the outer wrapper result");
            check(edited.ChangedFiles.SetEquals(new[] { @"C:\private-path\Agent.cs", Path.Combine(root, "src", "Widget.xaml") }),
                "Completed file changes retain all normalized paths independent of the activity labels");
            Event(path, new { type = "item_started", item = new { type = "FileChange", status = "in_progress",
                changes = new Dictionary<string, object> { { "pending.txt", new { type = "update" } } } } });
            Item(path, new { type = "FileChange", status = "failed",
                changes = new Dictionary<string, object> { { "failed.txt", new { type = "update" } } } }, at.AddSeconds(1));
            Item(path, new { type = "FileChange", status = "completed",
                changes = new Dictionary<string, object> {
                    { "SRC/Widget.xaml", new { type = "update" } },
                    { "Makefile", new { type = "add" } },
                    { "literal%20.md", new { type = "add" } },
                    { "https://example.invalid/remote.md", new { type = "add" } }
                } }, at.AddSeconds(1));
            var changed = reader.Poll().Agents.Single().ChangedFiles;
            check(changed.Count == 4 && changed.Contains(Path.Combine(root, "Makefile")) &&
                changed.Contains(Path.Combine(root, "literal%20.md")) &&
                changed.All(file => !file.Contains("pending.txt") && !file.Contains("failed.txt") && !file.Contains("example.invalid")),
                "Recorded edits are deduplicated and include extensionless names while incomplete, failed, and remote entries stay out");
            check(edited.ChangedFiles.Count == 2, "A previously returned agent snapshot does not gain later file edits");
            Append(path, new { timestamp = at.AddSeconds(2).ToString("o"), type = "response_item", payload = new { type = "function_call", name = "functions.exec_command", call_id = "test", arguments = serializer.Serialize(new { cmd = ".\\build.ps1 -Test -Token private-token" }) } });
            var running = reader.Poll().Agents.Single();
            check(running.Task == "Running tests with build.ps1", "Recognized test calls show the runner");
            Item(path, new { type = "CommandExecution", command = new[] { "powershell.exe", "-NoProfile", "-Command", ".\\build.ps1 -Test" }, status = "completed", exit_code = 0, aggregated_output = "private output\n42 checks passed." }, at.AddSeconds(3));
            Result(path, "test");
            var checkedAgent = reader.Poll().Agents.Single();
            check(checkedAgent.Task == "42 checks passed · build.ps1" && checkedAgent.State == "working" && checkedAgent.Ended == DateTime.MinValue, "Test results show verified counts without completing the agent");
            Item(path, new { type = "CommandExecution", command = "pytest", status = "completed", exit_code = 1, stderr = "private output" }, at.AddSeconds(4));
            var failed = reader.Poll().Agents.Single();
            check(failed.Task == "Tests failed · pytest" && failed.Events.Last().State == "error" && failed.State == "working", "A failed test names its runner without ending the agent's work");
            Item(path, new { type = "FileChange", status = "failed", changes = new Dictionary<string, object>() }, at.AddSeconds(5));
            check(reader.Poll().Agents.Single().Events.Last().Text == "File update failed", "Failed file edits are not described as successful updates");
            Item(path, new { type = "CommandExecution", command = "Get-Content build.ps1", parsed_cmd = new[] { new { type = "read", path = "private-path/build.ps1" } }, status = "completed", exit_code = 0 }, at.AddSeconds(6));
            check(reader.Poll().Agents.Single().Task == "Read build.ps1", "A structured read identifies the file without exposing its full path");
            Item(path, new { type = "CommandExecution", command = "Write-Output 'pytest'", status = "completed", exit_code = 0 }, at.AddSeconds(7));
            check(reader.Poll().Agents.Single().Task == "Read build.ps1", "A test-runner name in an unrelated argument does not replace real activity");
            Item(path, new { type = "CommandExecution", command = "git diff --stat", status = "completed", exit_code = 0 }, at.AddSeconds(8));
            check(reader.Poll().Agents.Single().Task == "Read build.ps1", "A command without a specific recorded result leaves the previous update visible");
            Event(path, new { type = "item_started", item = new { type = "CommandExecution", command = "dotnet build", status = "in_progress" } });
            check(reader.Poll().Agents.Single().Task == "Building with dotnet build", "Started build items name the build command without claiming success");
            Call(path, "input", "functions.request_user_input");
            Item(path, new { type = "CommandExecution", command = "dotnet build", status = "completed", exit_code = 0 }, at.AddSeconds(9));
            var attention = reader.Poll().Agents.Single();
            check(attention.State == "attention" && attention.Task == "Waiting for your input in Codex", "Completed action metadata does not dismiss pending attention");
            check(attention.Events.All(e => !e.Text.Contains("private")), "Tool summaries omit raw commands, full paths, arbitrary output, and diffs");
            Result(path, "input");
            Call(path, "repeat-1", "functions.exec_command");
            var old = reader.Poll().Agents.Single();
            Append(path, new { timestamp = old.Events.Last().At.AddSeconds(2).ToString("o"), type = "response_item", payload = new { type = "function_call", name = "functions.exec_command", call_id = "repeat-2" } });
            var repeated = reader.Poll().Agents.Single();
            check(repeated.Events.Count == old.Events.Count && repeated.Events.Last().At == old.Events.Last().At, "Generic commands leave meaningful activity intact");
            Event(path, new { type = "task_complete" });
            Item(path, new { type = "CommandExecution", command = "dotnet build", status = "completed", exit_code = 0 }, DateTime.UtcNow);
            var done = reader.Poll().Agents.Single();
            check(done.State == "completed" && done.Task == "Work complete", "Late rich tool items cannot revive a completed turn");
            File.WriteAllText(path, "", new UTF8Encoding(false));
            check(reader.Poll().Agents.All(agent => agent.ChangedFiles.Count == 0),
                "A truncated rollout does not retain stale file edits");
        }

        private static void Item(string path, object item, DateTime at)
        {
            Append(path, new { timestamp = at.ToString("o"), type = "event_msg", payload = new { type = "item_completed", item = item } });
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

        private static void CheckFreshSelection(Action<bool, string> check, string root)
        {
            var folder = Path.Combine(root, "fresh");
            Session(folder, "first-root", "", "", root, null);
            var reader = new ActivityReader(root, "", "", folder);
            var first = reader.Poll();
            check(first.Session == "first-root" && first.FocusedSession == "first-root" && first.TotalSessions == 1, "Automatic selection focuses the first active team");
            Thread.Sleep(1600);
            Session(folder, "second-root", "", "", root, null);
            // Stable teams rediscover every 5s; wait so the newer rollout is seen.
            Thread.Sleep(5200);
            var second = reader.Poll();
            check(second.TotalSessions == 2 && second.Sessions.Count == 2, "All active sessions are tracked while the view focuses one");
            check(second.Session == "second-root" && second.FocusedSession == "second-root", "A newly created session takes focus");
            check(second.Agents.Count == 1 && second.Agents[0].Id == "second-root", "The focused team shows its own agents");
            check(second.Sessions[0].RootId == "second-root", "Session summaries order the newest active team first");
            check(second.Source.Contains("2 sessions"), "Freshness source counts visible sessions");
            check(second.LastWriteUtc != DateTime.MinValue && !second.CatchingUp, "Freshness tracks the last local write and backlog state");

            var scopeFolder = Path.Combine(root, "scope");
            Session(scopeFolder, "sub-root", "", "", Path.Combine(root, "sub"), null);
            var scoped = new ActivityReader(root, "", "", scopeFolder).Poll();
            check(scoped.Connected && scoped.FocusedSession == "sub-root", "Sessions launched in a workspace subdirectory join the team");
            var otherFolder = Path.Combine(root, "scope-other");
            Session(otherFolder, "other-root", "", "", root + "-unrelated", null);
            var other = new ActivityReader(root, "", "", otherFolder).Poll();
            check(!other.Connected, "A sibling directory with a shared prefix does not join the team");
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
