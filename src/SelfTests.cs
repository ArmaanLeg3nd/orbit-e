using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    public static class SelfTests
    {
        private static readonly List<string> report = new List<string>();
        private static readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private static int count;
        private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); report.Add("PASS: " + label); count++; }
        public static int Run()
        {
            int exit = 0;
            var root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "selftest-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(root);
            try
            {
                Check(Json.Parse("{broken") == null, "Malformed JSON is ignored");
                Check(Json.Clean(" a\n b ", 30) == "a b", "Activity text is normalized");
                var tailPath = Path.Combine(root, "tail.jsonl");
                File.WriteAllText(tailPath, "{\"hello\":\"world\"}\n{\"partial\":", new UTF8Encoding(false));
                var tail = new JsonlTail();
                Check(tail.Read(tailPath, 65536).Count == 1, "Partial JSON lines are held until complete");
                Check(tail.Read(tailPath, 65536).Count == 0, "Unchanged files do not duplicate events");
                File.AppendAllText(tailPath, "true}\n", new UTF8Encoding(false));
                Check(tail.Read(tailPath, 65536).Single() == "{\"partial\":true}", "Partial writes are reassembled exactly");
                File.WriteAllText(tailPath, "{}\n", new UTF8Encoding(false));
                Check(tail.Read(tailPath, 65536).Single() == "{}", "Truncated files recover from the beginning");
                var unicodePath = Path.Combine(root, "unicode.jsonl");
                var encoded = Encoding.UTF8.GetBytes("{\"text\":\"café ✓\"}\n");
                var split = Array.IndexOf(encoded, (byte)0xE2) + 1;
                File.WriteAllBytes(unicodePath, encoded.Take(split).ToArray());
                var unicode = new JsonlTail();
                Check(unicode.Read(unicodePath, 65536).Count == 0, "Split Unicode code points are buffered");
                using (var append = new FileStream(unicodePath, FileMode.Append)) append.Write(encoded, split, encoded.Length - split);
                Check(unicode.Read(unicodePath, 65536).Single().Contains("café ✓"), "Unicode survives split writes");

                var events = Path.Combine(root, "events.jsonl");
                var now = DateTime.UtcNow;
                WriteEvent(events, "agent.started", "builder", now, "Builder", "Build the interface", "");
                var reader = new ActivityReader(root, "", events, null);
                var snapshot = reader.Poll();
                Check(snapshot.Connected && snapshot.Agents.Single().State == "working", "Custom event stream connects and starts an agent");
                WriteEvent(events, "agent.waiting", "builder", now.AddSeconds(1), "Builder", "Waiting for review", "");
                Check(reader.Poll().Agents.Single().State == "waiting", "Waiting is distinct from completion");
                WriteEvent(events, "agent.attention", "builder", now.AddSeconds(2), "Builder", "Needs a choice", "");
                Check(reader.Poll().Agents.Single().State == "attention", "Attention requests are surfaced");
                WriteEvent(events, "agent.completed", "builder", now.AddSeconds(3), "Builder", "Finished", "");
                snapshot = reader.Poll();
                Check(snapshot.Agents.Single().State == "completed" && snapshot.Agents.Single().Ended != DateTime.MinValue, "Completion stops the elapsed timer");
                WriteEvent(events, "agent.updated", "builder", now.AddSeconds(1), "Builder", "Old update", "");
                Check(reader.Poll().Agents.Single().State == "completed", "Out-of-order events cannot revive finished work");
                WriteEvent(events, "agent.started", "tests", now.AddSeconds(4), "Tests", "Checking", "builder");
                Check(reader.Poll().Agents.Single(a => a.Id == "tests").ParentId == "builder", "Nested agent relationships are retained");
                File.AppendAllText(events, "{bad json}\n", new UTF8Encoding(false));
                Check(reader.Poll().Agents.Count == 2, "Malformed records do not interrupt a live feed");

                var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
                WriteSession(Path.Combine(sessions, "root.jsonl"), "root", "", "root", root, "cli", now.AddMinutes(-2));
                WriteSession(Path.Combine(sessions, "child.jsonl"), "child", "root", "root", root, new { subagent = new { thread_spawn = new { parent_thread_id = "root", agent_nickname = "Explorer" } } }, now.AddMinutes(-1));
                WriteSession(Path.Combine(sessions, "guardian.jsonl"), "guardian", "root", "root", root, new { subagent = new { other = "guardian" } }, now);
                WriteSession(Path.Combine(sessions, "other.jsonl"), "other", "", "other", root + "-unrelated", "cli", now);
                var codex = new ActivityReader(root, "root", "", sessions);
                var local = codex.Poll();
                Check(local.Agents.Count == 2, "Codex reader scopes to the workspace and excludes internal guardian tasks");
                Check(local.Agents.Any(a => a.Name == "Explorer" && a.ParentId == "root"), "Codex child identity is read from session metadata");
                AppendJson(Path.Combine(sessions, "child.jsonl"), new { timestamp = now.ToString("o"), type = "event_msg", payload = new { type = "task_complete" } });
                Check(codex.Poll().Agents.Single(a => a.Id == "child").State == "completed", "Codex task completion is recognized incrementally");
                AppendJson(Path.Combine(sessions, "root.jsonl"), new { timestamp = now.ToString("o"), type = "response_item", payload = new { type = "function_call", name = "functions.request_user_input", call_id = "call_1" } });
                Check(codex.Poll().Agents.Single(a => a.Id == "root").State == "attention", "Codex user-input calls surface as attention");
                AppendJson(Path.Combine(sessions, "root.jsonl"), new { timestamp = now.ToString("o"), type = "response_item", payload = new { type = "function_call_output", call_id = "call_1", output = "not displayed" } });
                Check(codex.Poll().Agents.Single(a => a.Id == "root").State == "working", "Tool results resume agent activity without marking a turn complete");

                ActivityReaderChecks.Run(Check, root);
                var demo = new DemoFeed().Poll();
                Check(demo.Agents.Count == 4 && demo.Source.Contains("simulated"), "Preview data is explicitly labeled");
                var tree = new AgentTree();
                tree.Update(demo.Agents, demo.Session, demo.Project);
                Check(tree.Roots.Count == 1, "The tree presents a single session root");
                Check(tree.Find("agent:builder").Children.Any(n => n.Key == "agent:tester"), "Subagents nest under the agent that spawned them");
                Check(tree.Find("agent:builder").Children.Any(n => n.Kind == "event") && tree.Find("agent:builder").Children.Where(n => n.Kind == "event").All(n => n.Activity.AgentId == "builder") && !Flatten(tree.Roots).Any(n => n.Kind == "activity"), "Recent events appear directly under their agent without an Activity dropdown");
                tree.Find("agent:builder").IsExpanded = false;
                var savedBranch = tree.Find("agent:builder");
                var savedEvent = savedBranch.Children.First(n => n.Kind == "event");
                tree.Update(demo.Agents.Select(a => a.Copy()), demo.Session, demo.Project);
                Check(!tree.Find("agent:builder").IsExpanded && Object.ReferenceEquals(savedBranch, tree.Find("agent:builder")) && Object.ReferenceEquals(savedEvent, tree.Find(savedEvent.Key)), "Live updates preserve agent expansion and inline event identity");
                var historyAgent = demo.Agents.Single(a => a.Id == "builder").Copy();
                historyAgent.Events.AddRange(Enumerable.Range(1, 8).Select(i => new ActivityEvent { AgentId = historyAgent.Id, Agent = historyAgent.Name, Text = "Step " + i, At = DateTime.UtcNow.AddSeconds(i) }));
                var historyCount = historyAgent.Events.Count;
                tree.Update(new[] { historyAgent }, "history", "test");
                var recentEvents = tree.Find("agent:builder").Children.Where(n => n.Kind == "event").ToList();
                Check(recentEvents.Count == 5 && recentEvents.First().Activity.Text == "Step 8" && recentEvents.Last().Activity.Text == "Step 4" && historyAgent.Events.Count == historyCount, "Inline events show the latest five while preserving full detail history");
                var cyclic = new[] {
                    new Agent { Id = "a", ParentId = "b", Name = "A", State = "working" },
                    new Agent { Id = "b", ParentId = "a", Name = "B", State = "working" },
                    new Agent { Id = "orphan", ParentId = "missing", Name = "Orphan", State = "working" }
                };
                tree.Update(cyclic, "cycles", "test");
                Check(Flatten(tree.Roots).Count(n => n.Kind == "agent") == 3, "Cycles and unavailable parents never hide an agent");
                tree.Update(new Agent[0], "", "");
                Check(tree.Roots.Count == 0, "An empty snapshot clears stale tree branches");
                var botTree = new AgentTree();
                botTree.Update(demo.Agents, demo.Session, demo.Project);
                var botBuilder = botTree.Find("agent:builder");
                Check(botBuilder.ReplyCount == 1 && botBuilder.HasReplies && botBuilder.ReplyLabel == "1 reply", "Bot cards count nested subagents as replies");
                Check(botTree.Find("agent:builder").Title == "/Builder" && new Agent { Id = "x", Name = "Session 17:17", IsRoot = true }.DisplayName == "/root", "Leads show as /root, subagents with a slash prefix");
                var actRoot = new Agent { Id = "aroot", Name = "Root", State = "working", Task = "Leading", Started = DateTime.UtcNow.AddMinutes(-2), Updated = DateTime.UtcNow };
                var actSub = new Agent { Id = "asub", ParentId = "aroot", Name = "Sub", State = "working", Task = "Helping", Started = DateTime.UtcNow.AddMinutes(-1), Updated = DateTime.UtcNow };
                actSub.Events.Add(new ActivityEvent { AgentId = "asub", Agent = "Sub", Text = "Live progress", State = "working", At = DateTime.UtcNow });
                var liveTree = new AgentTree();
                liveTree.Update(new[] { actRoot, actSub }, "live", "test");
                Check(liveTree.Find("agent:asub").IsExpanded, "Active subagents auto-expand so live activity is visible");
                Check(botTree.Find("agent:explorer").ReplyCount == 0 && !botTree.Find("agent:explorer").HasReplies, "Leaf bots hide the replies toggle");

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
                {
                    var widget = new Widget(new Options(new[] { "--demo", "--workspace", root, "--events", events }), signal);
                    var liveWindow = widget.Window;
                    liveWindow.Show(); Pump(400);
                    var nativeTree = (TreeView)liveWindow.FindName("AgentTreeView");
                    Check(liveWindow.IsVisible && Flatten(nativeTree.Items.Cast<TreeNode>()).Count(n => n.Kind == "agent") == 4, "Native tree starts with all four preview agents");
                    Check(liveWindow.Resources.Contains("SlimBar"), "Slim scrollbar style replaces the wide system bar");
                    Check(System.Windows.Controls.VirtualizingStackPanel.GetIsVirtualizing(nativeTree), "Agent feed virtualizes bot cards");
                    Check(demo.Agents.All(a => !String.IsNullOrEmpty(a.StatusLabel)), "Agent rows show plain colored status text");
                    Check(((SolidColorBrush)nativeTree.Foreground).Color.R > 220, "Native tree text does not inherit the system's black foreground");
                    Check(liveWindow.AllowsTransparency && liveWindow.WindowStyle == WindowStyle.None && liveWindow.Topmost && !liveWindow.ShowInTaskbar, "Widget remains a frameless transparent topmost native window");
                    var glass = (LinearGradientBrush)((Border)liveWindow.FindName("Surface")).Background;
                    Check(glass.GradientStops.All(g => g.Color.A < 230 && g.Color.R == g.Color.G && g.Color.G == g.Color.B), "Glass surface is genuinely translucent and monochrome");
                    Check(Math.Abs(liveWindow.Width - 380) < 1 && Math.Abs(liveWindow.Height - 484) < 1 && ((Grid)liveWindow.FindName("ExpandedView")).Margin.Left == 11, "Window and internal padding are compact (" + liveWindow.Width + " x " + liveWindow.Height + ")");
                    Render(liveWindow, Path.Combine(root, "expanded.png"));

                    var liveNodes = Flatten(nativeTree.Items.Cast<TreeNode>()).ToList();
                    var builderNode = liveNodes.Single(n => n.Key == "agent:builder");
                    builderNode.IsExpanded = true; Pump(100);
                    Check(builderNode.Children.Any(n => n.Kind == "event") && builderNode.Children.Where(n => n.Kind == "event").All(n => n.Activity.AgentId == "builder") && !liveNodes.Any(n => n.Kind == "activity"), "One agent expansion reveals its subagents and activity directly");
                    Render(liveWindow, Path.Combine(root, "tree-activity.png"));
                    Pump(1600);
                    Check(Object.ReferenceEquals(builderNode, Flatten(nativeTree.Items.Cast<TreeNode>()).Single(n => n.Key == "agent:builder")) && builderNode.IsExpanded, "A background poll preserves the open agent branch and inline activity");

                    var rowButton = FindAgentButton(nativeTree, "builder");
                    Check(rowButton != null, "Agent details have an accessible native button");
                    rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, rowButton)); Pump(230);
                    Check(((FrameworkElement)liveWindow.FindName("DetailView")).Visibility == Visibility.Visible && ((TextBlock)liveWindow.FindName("DetailName")).Text == "/Builder", "Agent detail button opens the correct branch");
                    Render(liveWindow, Path.Combine(root, "detail.png"));
                    Press(liveWindow, "BackButton"); Press(liveWindow, "SettingsButton"); Pump(230);
                    Check(((FrameworkElement)liveWindow.FindName("SettingsView")).Visibility == Visibility.Visible, "Settings open inside the same window");
                    Check(((SolidColorBrush)((Button)liveWindow.FindName("DemoButton")).Background).Color.A > ((SolidColorBrush)((Button)liveWindow.FindName("LiveButton")).Background).Color.A, "Source selection styling matches Preview mode");
                    Render(liveWindow, Path.Combine(root, "settings.png"));
                    Press(liveWindow, "SettingsBackButton");
                    var right = liveWindow.Left + liveWindow.Width; var bottom = liveWindow.Top + liveWindow.Height;
                    Press(liveWindow, "CollapseButton"); Pump(400);
                    Check(liveWindow.Width == 260 && liveWindow.Height == 76 && ((FrameworkElement)liveWindow.FindName("PillView")).Visibility == Visibility.Visible, "Animated collapse reaches the smaller status pill");
                    Check(Math.Abs(liveWindow.Left + liveWindow.Width - right) < 1 && Math.Abs(liveWindow.Top + liveWindow.Height - bottom) < 1, "Collapse preserves the anchored screen corner");
                    Check(((Button)liveWindow.FindName("ExpandButton")).Focusable, "The collapsed pill is a keyboard-accessible native button");
                    Render(liveWindow, Path.Combine(root, "pill.png"));
                    Press(liveWindow, "ExpandButton"); Pump(600);
                    Check(liveWindow.Width == 380, "The pill's invoke action expands the widget");
                    Press(liveWindow, "CollapseButton");
                    // Exercise a request arriving during the animation, not after it.
                    Press(liveWindow, "ExpandButton"); Pump(800);
                    Check(liveWindow.Width == 380, "Expansion requested during collapse is queued until the animation finishes");
                    Press(liveWindow, "CollapseButton"); Pump(400);
                    signal.Set(); Pump(1000);
                    Check(liveWindow.Width == 380 && !((FrameworkElement)liveWindow.FindName("PillView")).IsVisible, "Existing-instance wake signal expands the widget");
                    Press(liveWindow, "SettingsButton"); Press(liveWindow, "LiveButton"); Pump(1000);
                    Check(((TextBlock)liveWindow.FindName("SourceLabel")).Text == "Connected to local event file" && Flatten(nativeTree.Items.Cast<TreeNode>()).Count(n => n.Kind == "agent") == 2, "Live mode replaces preview agents with file-backed agents and identifies the source in the footer");
                    Press(liveWindow, "CollapseButton"); Pump(400);
                    WriteEvent(events, "agent.attention", "tests", DateTime.UtcNow.AddSeconds(15), "Tests", "Review needed", "builder");
                    Pump(2100);
                    Check(liveWindow.Width == 380 && ((TextBlock)liveWindow.FindName("SummaryTitle")).Text == "Needs attention", "A new live attention event expands the widget automatically");
                    Render(liveWindow, Path.Combine(root, "attention.png"));
                    Check(((TextBlock)liveWindow.FindName("SourceLabel")).Text == "Connected to local event file", "Live data source is shown accurately");
                    File.WriteAllText(events, "", new UTF8Encoding(false)); Pump(2600);
                    Check(((FrameworkElement)liveWindow.FindName("EmptyState")).IsVisible && nativeTree.Items.Count == 0, "An empty live stream shows a useful empty state");
                    Render(liveWindow, Path.Combine(root, "empty.png"));
                    liveWindow.Close();
                }
                report.Add("\n" + count + " checks passed.\nArtifacts: " + root);
            }
            catch (Exception ex) { report.Add(ex.ToString()); exit = 1; }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.txt"), report);
            return exit;
        }
        private static void AppendJson(string path, object record) { File.AppendAllText(path, serializer.Serialize(record) + "\n", new UTF8Encoding(false)); }
        private static void Press(Window window, string name) { var button = (Button)window.FindName(name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
        private static IEnumerable<TreeNode> Flatten(IEnumerable<TreeNode> nodes)
        {
            foreach (var node in nodes) { yield return node; foreach (var child in Flatten(node.Children)) yield return child; }
        }
        private static Button FindAgentButton(DependencyObject root, string id)
        {
            var button = root as Button;
            if (button != null && button.Tag is Agent && ((Agent)button.Tag).Id == id) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = FindAgentButton(VisualTreeHelper.GetChild(root, i), id); if (found != null) return found; }
            return null;
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static void Render(Window window, string path)
        {
            window.UpdateLayout();
            var surface = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        private static void WriteEvent(string path, string type, string id, DateTime at, string name, string task, string parent)
        { AppendJson(path, new { type = type, agent_id = id, timestamp = at.ToString("o"), name = name, task = task, parent_id = parent }); }
        private static void WriteSession(string path, string id, string parent, string session, string cwd, object source, DateTime at)
        {
            AppendJson(path, new { timestamp = at.ToString("o"), type = "session_meta", payload = new { id = id, parent_thread_id = parent, session_id = session, cwd = cwd, source = source, timestamp = at.ToString("o") } });
            AppendJson(path, new { timestamp = at.ToString("o"), type = "event_msg", payload = new { type = "task_started" } });
        }
    }
}
