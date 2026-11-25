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

namespace OrbitEvolved
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
                Check(tree.Find("agent:builder").Children.All(n => n.Kind == "agent"), "Chat trees show agents without inline activity rows");
                tree.Find("agent:builder").IsExpanded = false;
                var savedBranch = tree.Find("agent:builder");
                tree.Update(demo.Agents.Select(a => a.Copy()), demo.Session, demo.Project);
                Check(!tree.Find("agent:builder").IsExpanded && Object.ReferenceEquals(savedBranch, tree.Find("agent:builder")), "Live updates preserve agent expansion and row identity");
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
                Check(botBuilder.Children.Count == 1 && botBuilder.IsExpanded, "Branches with subagents open by default");
                var finished = new Agent { Name = "Reviewer", State = "completed", Task = "Work complete" };
                finished.Events.Add(new ActivityEvent { Text = "Reviewed two files" });
                finished.Events.Add(new ActivityEvent { Text = "Completed the task" });
                Check(finished.ActivitySummary == "Reviewed two files" && finished.Events.Count == 2,
                    "Completed rows show meaningful work and keep lifecycle entries in the recorded log");
                Check(botTree.Find("agent:builder").Title == "/Builder" && new Agent { Id = "x", Name = "Session 17:17", IsRoot = true }.DisplayName == "/root", "Leads show as /root, subagents with a slash prefix");
                var actRoot = new Agent { Id = "aroot", Name = "Root", State = "working", Task = "Leading", Started = DateTime.UtcNow.AddMinutes(-2), Updated = DateTime.UtcNow };
                var actSub = new Agent { Id = "asub", ParentId = "aroot", Name = "Sub", State = "working", Task = "Helping", Started = DateTime.UtcNow.AddMinutes(-1), Updated = DateTime.UtcNow };
                actSub.Events.Add(new ActivityEvent { AgentId = "asub", Agent = "Sub", Text = "Live progress", State = "working", At = DateTime.UtcNow });
                var liveTree = new AgentTree();
                liveTree.Update(new[] { actRoot, actSub }, "live", "test");
                Check(!liveTree.Find("agent:asub").IsExpanded && actSub.Events.Count == 1, "Leaf agents stay compact and retain their recorded activity");
                var newChild = new Agent { Id = "new-child", ParentId = "asub", Name = "Child", State = "working" };
                liveTree.Update(new[] { actRoot, actSub, newChild }, "live", "test");
                Check(liveTree.Find("agent:asub").IsExpanded, "A new subagent reveals its branch when the first child arrives");

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var localPath = Path.Combine(root, "observatory (draft).mjs");
                File.WriteAllText(localPath, "// synthetic fixture");
                var absoluteLink = "[booking planner](</" + localPath.Replace('\\', '/') + ":12>)";
                var linkBlock = new ActivityTextBlock { Workspace = root, Body = "Built " + absoluteLink + ".\n\n" + new string('x', 260) + " [source](<observatory (draft).mjs#L4>)" };
                var references = linkBlock.Inlines.OfType<System.Windows.Documents.Hyperlink>().ToList();
                Check(references.Count == 2 && (string)references[0].ToolTip == localPath + ":12" &&
                      (string)references[1].ToolTip == localPath + ":4" && !linkBlock.Text.Contains(root),
                    "Windows file links and relative line references render by label after the old text limit");
                Check(ActivityLinks.Resolve("file:///C:/Notes/plan%20draft.mjs:20:3", root).Reference == @"C:\Notes\plan draft.mjs:20" &&
                      ActivityLinks.Resolve(@"\\server\share\file.txt", root) == null &&
                      ActivityLinks.Resolve("https://example.test/file.md", root) == null &&
                      ActivityLinks.Resolve(@"C:\Notes\file.txt:stream", root) == null,
                    "The local link resolver handles encoded paths and rejects URLs, network shares, and alternate streams");
                var tableBlock = new ActivityTextBlock {
                    Workspace = root,
                    Body = "| Role | Effort | Observed result |\n| --- | --- | --- |\n| Optimizer | Medium | " + absoluteLink + " |\n| Reviewer | Low | Passed |\n\n- Verify the public results"
                };
                var table = tableBlock.Inlines.OfType<System.Windows.Documents.InlineUIContainer>()
                    .Select(item => item.Child).OfType<ActivityMarkdownTable>().Single();
                Check(table.ColumnHeaders.SequenceEqual(new[] { "Role", "Effort", "Observed result" }) && table.DataRowCount == 2,
                    "Structured activity renders a native table with separate headings and rows");
                var variedTables = new ActivityTextBlock {
                    Body = "| Agent | State |\n| --- | --- |\nPlanner\n| Coordinator |\n| Reviewer | Done | extra |\n| Builder | Working |\n\n" +
                           "| Result |\n| --- |\n| Verified |\n\nA normal paragraph."
                };
                var variations = variedTables.Inlines.OfType<System.Windows.Documents.InlineUIContainer>()
                    .Select(item => item.Child).OfType<ActivityMarkdownTable>().ToArray();
                Check(variations.Length == 2 && variations[0].DataRowCount == 4 && variations[0].Children.Count == 10 &&
                      variations[1].ColumnHeaders.SequenceEqual(new[] { "Result" }) && variations[1].DataRowCount == 1,
                    "Uneven rows and single-column tables stay structured without consuming surrounding text");
                var graphData = new[] {
                    new Agent { Id = "lead", IsRoot = true, Name = "Lead", State = "completed", Result = "Integrated the scheduler and CLI." },
                    new Agent { Id = "coordinator", ParentId = "lead", Name = "Coordinator", State = "completed", Task = "Assigned parser and optimizer work." },
                    new Agent { Id = "parser", ParentId = "coordinator", Name = "Input parser", State = "completed", Task = "Validated input and normalized dates." },
                    new Agent { Id = "optimizer", ParentId = "coordinator", Name = "Optimizer", State = "working", Task = "Checking the schedule against an independent reference." },
                    new Agent { Id = "reviewer", ParentId = "lead", Name = "Reviewer", State = "waiting", Task = "Reviewed the parser and is waiting on optimization checks." }
                };
                graphData[0].Events.Add(new ActivityEvent {
                    Text = "Integrated the scheduler and CLI.",
                    Detail = "Integrated the scheduler and CLI.\n\n| Check | Result |\n| --- | --- |\n| Exhaustive reference | Matched all 120 cases |\n| Parser review | Approved |",
                    At = DateTime.UtcNow
                });
                graphData[0].Events.Add(new ActivityEvent { Text = "The parser and optimizer use the same module contract.", At = DateTime.UtcNow.AddMinutes(-1) });
                var layout = AgentGraphLayout.Create(graphData);
                Check(layout.Nodes.Count() == 5 && layout.Nodes.Count(node => !String.IsNullOrEmpty(node.ParentId)) == 4 &&
                      layout.Nodes.Single(node => node.Agent.Id == "optimizer").Depth == 2,
                    "Workflow graph preserves nested delegation and its four real parent edges");
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
                {
                    var startupTestKey = @"Software\Orbit E(volved) self-test " + Guid.NewGuid().ToString("N");
                    var settings = new Options(new[] { "--demo", "--workspace", root, "--events", events })
                    {
                        Startup = new StartupRegistration(startupTestKey)
                    };
                    var legacyFolder = Directory.CreateDirectory(Path.Combine(root, Product.LegacyDataDirectory));
                    var legacyPreferences = Path.Combine(legacyFolder.FullName, "preferences.json");
                    var legacyData = "{\"attention\":false}";
                    File.WriteAllText(legacyPreferences, legacyData);
                    var widget = new Widget(settings, signal);
                    var liveWindow = widget.Window;
                    liveWindow.Show(); Pump(400);
                    Check(((CheckBox)liveWindow.FindName("AttentionCheck")).IsChecked == false,
                        "Widget reads preferences from the previous settings directory");
                    var previewGraph = new AgentGraphWindow { Owner = liveWindow };
                    previewGraph.Update("Observatory booking planner", graphData, root);
                    previewGraph.Show(); Pump(60);
                    var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(liveWindow).Handle).WorkingArea;
                    var center = previewGraph.PointToScreen(new Point(previewGraph.ActualWidth / 2, previewGraph.ActualHeight / 2));
                    Check(Math.Abs(center.X - (screen.Left + screen.Width / 2.0)) < 3 &&
                          Math.Abs(center.Y - (screen.Top + screen.Height / 2.0)) < 3,
                        "The graph opens centered in the widget monitor's usable area");
                    var frame = ((Grid)previewGraph.Content).Children.OfType<Grid>().Single();
                    var graphGlass = frame.Children.OfType<Border>().Single();
                    var compactGlass = (Border)liveWindow.FindName("Surface");
                    Check(previewGraph.AllowsTransparency && graphGlass.CornerRadius.TopLeft == 15 &&
                          ReferenceEquals(graphGlass.Background, compactGlass.Background) &&
                          ReferenceEquals(graphGlass.BorderBrush, compactGlass.BorderBrush),
                        "The graph uses the same rounded translucent glass as the widget");
                    Check(!SystemParameters.ClientAreaAnimation ||
                          DependencyPropertyHelper.GetValueSource(previewGraph, UIElement.OpacityProperty).IsAnimated,
                        "The graph animates its entrance when motion is enabled");
                    var corner = previewGraph.PointToScreen(new Point(previewGraph.ActualWidth - 2, previewGraph.ActualHeight - 2));
                    var cornerParameter = (int)(short)corner.X & 0xffff | ((int)(short)corner.Y & 0xffff) << 16;
                    var target = new System.Windows.Interop.WindowInteropHelper(previewGraph).Handle;
                    Check(SendMessage(target, 0x0084, IntPtr.Zero, new IntPtr(cornerParameter)).ToInt32() == 17,
                        "The translucent graph retains native resize hit testing at its corners");
                    Pump(320);
                    var viewport = FindVisual<ScrollViewer>(previewGraph, view =>
                        System.Windows.Automation.AutomationProperties.GetName(view) == "Agent delegation graph");
                    var anchor = new Point(viewport.ViewportWidth / 2, viewport.ViewportHeight / 2);
                    previewGraph.SetZoom(1.5, anchor); Pump(120);
                    Check(Math.Abs(previewGraph.Zoom - 1.5) < 0.001 && viewport.ScrollableWidth > 0,
                        "Zooming enlarges the graph and its scrollable canvas");
                    var busyGraph = graphData.Concat(Enumerable.Range(1, 12).Select(i => new Agent {
                        Id = "worker-" + i, ParentId = "lead", Name = "Worker " + i, Task = "Checking work."
                    })).ToList();
                    previewGraph.Update("Observatory booking planner", busyGraph, root); Pump(120);
                    previewGraph.FitGraph(); Pump(120);
                    Check(previewGraph.Zoom < 1 && viewport.ScrollableHeight < 1 && viewport.ScrollableWidth < 1,
                        "Fit brings all nodes into view for a tall graph");
                    previewGraph.Update("Observatory booking planner", graphData, root);
                    previewGraph.SetZoom(1, anchor); Pump(100);
                    previewGraph.SelectAgent("reviewer");
                    Check(previewGraph.HighlightedEdges == 1, "Selecting a leaf highlights its recorded route");
                    var parentLine = FindVisual<TextBlock>(previewGraph, block => block.Inlines.OfType<System.Windows.Documents.Hyperlink>()
                        .Any(link => (string)link.ToolTip == "Focus /root"));
                    var parentLink = parentLine.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single();
                    parentLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent, parentLink));
                    Check(previewGraph.SelectedAgentId == "lead" && previewGraph.HighlightedEdges == 4,
                        "Inspector links jump to related nodes and highlight the new branch");
                    previewGraph.SelectAgent("coordinator"); Pump(230);
                    Render(previewGraph, Path.Combine(root, "workflow-graph.png"));
                    previewGraph.Close();
                    var nativeTree = (TreeView)liveWindow.FindName("AgentTreeView");
                    Check(liveWindow.IsVisible && Flatten(nativeTree.Items.Cast<TreeNode>()).Count(n => n.Kind == "agent") == 4, "Native tree starts with all four preview agents");
                    var panelSpinner = (FrameworkElement)liveWindow.FindName("PanelSpinner");
                    Check(panelSpinner.IsVisible && ((RotateTransform)panelSpinner.RenderTransform).HasAnimatedProperties == SystemParameters.ClientAreaAnimation,
                        "Working agents show the spinner and respect the system motion setting");
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
                    Check(builderNode.Children.Count == 1 && builderNode.Children[0].Key == "agent:tester", "Expanding an agent reveals just its subagents");
                    Pump(1600);
                    Check(Object.ReferenceEquals(builderNode, Flatten(nativeTree.Items.Cast<TreeNode>()).Single(n => n.Key == "agent:builder")) && builderNode.IsExpanded, "A background poll preserves the open agent branch");

                    var rowButton = FindAgentButton(nativeTree, "builder");
                    Check(rowButton != null, "Agent details have an accessible native button");
                    rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, rowButton)); Pump(230);
                    Check(((FrameworkElement)liveWindow.FindName("DetailView")).Visibility == Visibility.Visible && ((TextBlock)liveWindow.FindName("DetailName")).Text == "/Builder", "Agent detail button opens the correct branch");
                    var summary = ((ActivityTextBlock)liveWindow.FindName("DetailTask")).Body;
                    var history = ((ItemsControl)liveWindow.FindName("DetailEvents")).Items.Cast<ActivityEvent>().ToList();
                    Check(summary != "" && history.Any(item => item.Text == "Picked up the task") && history.All(item => item.Text != summary),
                        "Agent details show earlier activity once and keep the current task above it");
                    Check(liveWindow.FindName("DetailElapsed") == null && liveWindow.FindName("DetailParent") == null,
                        "Agent details omit elapsed time and redundant parent metadata");
                    Render(liveWindow, Path.Combine(root, "detail.png"));
                    Press(liveWindow, "BackButton"); Press(liveWindow, "SettingsButton"); Pump(230);
                    Check(((FrameworkElement)liveWindow.FindName("SettingsView")).Visibility == Visibility.Visible, "Settings open inside the same window");
                    Check(!((RotateTransform)panelSpinner.RenderTransform).HasAnimatedProperties,
                        "The spinner stops animating when its view is hidden");
                    var attention = (CheckBox)liveWindow.FindName("AttentionCheck");
                    attention.IsChecked = true;
                    attention.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, attention));
                    var preferences = Path.Combine(root, Product.DataDirectory, "preferences.json");
                    Check((bool)Json.Get(Json.Parse(File.ReadAllText(preferences)), "attention") &&
                          File.ReadAllText(legacyPreferences) == legacyData,
                        "Widget saves preferences to the renamed directory and preserves the earlier preferences");
                    Check(liveWindow.Title.StartsWith(Product.Name + " · ", StringComparison.Ordinal) &&
                          (string)((Button)liveWindow.FindName("QuitButton")).Content == "Quit " + Product.Name,
                        "The widget title and quit button use the product name");
                    try
                    {
                        var startup = (CheckBox)liveWindow.FindName("StartupCheck");
                        Check(startup.IsEnabled && startup.IsChecked == false, "Start on Startup is off without a registration");
                        startup.IsChecked = true;
                        startup.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, startup));
                        string command;
                        Check(startup.IsChecked == true && settings.Startup.TryRead(out command) &&
                              command == "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"",
                            "Enabling startup registers the exact quoted executable path");
                        Press(liveWindow, "SettingsBackButton");
                        Press(liveWindow, "SettingsButton");
                        Check(startup.IsChecked == true, "The startup toggle reloads its registered state from Windows");
                        startup.IsChecked = false;
                        startup.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent, startup));
                        Check(startup.IsChecked == false && settings.Startup.TryRead(out command) && command == null,
                            "Disabling startup removes the registration");
                    }
                    finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(startupTestKey, false); }
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
                CheckProjectCatalog(root);
                CheckQualityOfLifeCatalog(root);
                CheckCompletionPill(root);
                CheckTaskbarBridge(root);
                CheckQualityOfLifeGraph(root);
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
        private static Button FindChatButton(DependencyObject root, string id)
        {
            var button = root as Button;
            if (button != null && button.Tag is ChatSummary && ((ChatSummary)button.Tag).Id == id) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindChatButton(VisualTreeHelper.GetChild(root, i), id);
                if (found != null) return found;
            }
            return null;
        }
        private static T FindVisual<T>(DependencyObject root, Func<T, bool> matches) where T : DependencyObject
        {
            var node = root as T;
            if (node != null && matches(node)) return node;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var found = FindVisual(VisualTreeHelper.GetChild(root, index), matches);
                if (found != null) return found;
            }
            return null;
        }
        private static void CheckQualityOfLifeCatalog(string root)
        {
            var home = Path.Combine(root, "qol-catalog");
            var folder = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
            var archived = Directory.CreateDirectory(Path.Combine(home, "archived_sessions")).FullName;
            var projectA = Path.Combine(root, "Climate");
            var projectB = Path.Combine(root, "Observatory");
            AppendJson(Path.Combine(home, ".codex-global-state.json"), new Dictionary<string, object> {
                { "local-projects", new {
                    climate = new { id = "climate", name = "Climate", rootPaths = new[] { projectA } },
                    observatory = new { id = "observatory", name = "Observatory", rootPaths = new[] { projectB } }
                } }
            });
            var inputId = "01a00000-0000-7000-8000-000000000001";
            var workingId = "01a00000-0000-7000-8000-000000000002";
            var waitingId = "01a00000-0000-7000-8000-000000000003";
            var archivedId = "01a00000-0000-7000-8000-000000000004";
            var fixture = new[] {
                Tuple.Create("approval", inputId, projectA, "Collect measurements"),
                Tuple.Create("working", workingId, projectB, "Build schedule"),
                Tuple.Create("waiting", waitingId, projectA, "Check sensors"),
                Tuple.Create("archive", archivedId, projectB, "Old schedule")
            };
            var now = DateTime.UtcNow;
            foreach (var chat in fixture)
            {
                var sessionFolder = chat.Item2 == archivedId ? archived : folder;
                WriteSession(Path.Combine(sessionFolder, chat.Item1 + ".jsonl"), chat.Item2, "", "", chat.Item3, "cli", now);
                AppendJson(Path.Combine(home, "session_index.jsonl"), new { id = chat.Item2, thread_name = chat.Item4 });
            }
            var input = Path.Combine(folder, "approval.jsonl");
            AppendJson(input, new { timestamp = now.ToString("o"), type = "response_item",
                payload = new { type = "function_call", name = "functions.request_user_input", call_id = "ask" } });
            AppendJson(Path.Combine(folder, "waiting.jsonl"), new { timestamp = now.ToString("o"), type = "response_item",
                payload = new { type = "function_call", name = "collaboration.wait_agent", call_id = "wait" } });
            var reviewerLog = Path.Combine(folder, "reviewer.jsonl");
            WriteSession(reviewerLog, "qol-reviewer", workingId, workingId, projectB,
                new { subagent = new { thread_spawn = new { agent_nickname = "Reviewer" } } }, now);
            AppendJson(reviewerLog, new { timestamp = now.AddMilliseconds(50).ToString("o"), type = "event_msg", payload = new {
                type = "agent_message", message = "Checking the **parser**."
            } });
            AppendJson(reviewerLog, new { timestamp = now.AddMilliseconds(100).ToString("o"), type = "event_msg", payload = new {
                type = "item_completed", item = new { type = "CommandExecution", status = "completed",
                    parsed_cmd = new[] { new { type = "read", path = Path.Combine(projectB, "routine.txt") } } }
            } });
            var clock = DateTime.UtcNow.AddSeconds(1);
            var catalog = new ChatCatalog(home, null, root, () => clock);
            var active = catalog.Poll(null);
            Check(active.ActiveChatEntries.Select(entry => entry.Chat.Id).SequenceEqual(new[] { inputId, workingId, waitingId }) &&
                  active.ActiveChatEntries.Select(entry => entry.Status).SequenceEqual(new[] { "Needs your input", "Working", "Waiting" }) &&
                  active.ActiveChatEntries.Select(entry => entry.Chat.ProjectKey).Distinct().Count() == 2,
                "Active chats span local projects, prioritize requests and exclude archives");
            Check(active.ActiveChats == 3 && active.WorkingAgents == 2 && active.ActiveChat.Id == inputId,
                "Active list prioritizes input and distinguishes counts of chats and agents");
            Check(active.ActiveChatEntries.Single(entry => entry.Chat.Id == workingId).Update == "Reviewer · Checking the parser." &&
                  active.ActiveChatEntries.Single(entry => entry.Chat.Id == inputId).Update == "Waiting for your input in Codex",
                "Active updates identify the reporting agent, skip later routine reads, and surface pending input");
            Check(CodexLink.ForChat(null) == null && CodexLink.ForChat("https://invalid.example") == null &&
                  CodexLink.ForChat(workingId + "?other") == null,
                "Codex links accept only a local chat UUID");
            var opened = new List<Uri>();
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                var settings = new Options(new[] { "--workspace", root, "--all-projects" }) {
                    CatalogHome = home, CodexLauncher = uri => { opened.Add(uri); return true; }
                };
                var window = new Widget(settings, signal).Window;
                try
                {
                    window.Show(); Pump(500);
                    var tree = (TreeView)window.FindName("AgentTreeView");
                    Check(tree.Items.Cast<TreeNode>().Select(row => row.Chat.Id).SequenceEqual(new[] { inputId, workingId, waitingId }),
                        "The widget opens in Active and shows all three chats in priority order");
                    Check(FindVisual<TextBlock>(tree, text => text.IsVisible && text.Text == "Reviewer · Checking the parser.") != null,
                        "The active chat row renders its single meaningful update");
                    Press(window, "CollapseButton"); Pump(450);
                    Check(((TextBlock)window.FindName("PillTitle")).Text == "1 needs you · 1 working" &&
                          ((TextBlock)window.FindName("PillSubtitle")).Text == "1 waiting",
                        "The collapsed pill summarizes multiple chats by status and counts chats instead of agents");
                    Render(window, Path.Combine(root, "active-overview-pill.png"));
                    Press(window, "ExpandButton"); Pump(700);
                    Check(((TextBlock)window.FindName("ProjectLabel")).Text == "Collect measurements",
                        "The collapsed overview opens the chat that needs input first");
                    Press(window, "SourceButton"); Pump(500);
                    var rowLink = FindVisual<Button>(tree, button => {
                        var chat = button.Tag as ChatSummary;
                        return button.IsVisible && chat != null && chat.Id == workingId && (string)button.CommandParameter == "codex";
                    });
                    Check(rowLink != null, "A valid catalog chat exposes Open in Codex on its row");
                    rowLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, rowLink));
                    Check(opened.Single().AbsoluteUri == "codex://threads/" + workingId,
                        "The chat row launches the exact local Codex deep link");
                    Press(window, "AllChatsButton"); Pump(150);
                    Check(tree.Items.Cast<TreeNode>().All(row => row.Kind == "project") &&
                          Flatten(tree.Items.Cast<TreeNode>()).Any(row => row.Chat != null && row.Chat.Id == archivedId),
                        "All restores the project groups and archived chats");
                    Press(window, "ActiveChatsButton"); Pump(150);
                    Render(window, Path.Combine(root, "qol-active-chats.png"));
                    var target = FindVisual<Button>(tree, button => {
                        var chat = button.Tag as ChatSummary;
                        return button.IsVisible && chat != null && chat.Id == waitingId && button.CommandParameter == null;
                    });
                    Check(target != null, "Each active row exposes a workflow action");
                    target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, target)); Pump(700);
                    var graph = window.OwnedWindows.OfType<AgentGraphWindow>().Single();
                    Check(graph.IsVisible && graph.SelectedAgentId == waitingId, "An active row opens its own graph directly");
                    var graphLink = FindVisual<Button>(graph, button => button.Content as string == "Open in Codex");
                    graphLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, graphLink));
                    Check(opened.Last().AbsoluteUri == "codex://threads/" + waitingId,
                        "The graph uses the chat it displays for its Codex deep link");
                    Press(window, "ChatFilesButton"); Pump(180);
                    Check(FindVisual<TextBlock>(graph, text => text.IsVisible && text.Text == "No file edits recorded for this chat.") != null,
                        "The chat's Files shortcut opens the graph's empty files view");
                    graph.Close();
                }
                finally { window.Close(); }
            }
            var workingLog = Path.Combine(folder, "working.jsonl");
            AppendJson(workingLog, new { timestamp = now.AddMinutes(-10).ToString("o"), type = "event_msg",
                payload = new { type = "task_started" } });
            AppendJson(reviewerLog, new { timestamp = now.AddMinutes(-10).ToString("o"), type = "event_msg",
                payload = new { type = "task_started" } });
            Check(catalog.Poll(null).ActiveChatEntries.Single(entry => entry.Chat.Id == workingId).Status == "No recent activity",
                "Previously observed silent work remains listed without an unverified Working claim");
            var finishedAt = DateTime.UtcNow;
            AppendJson(input, new { timestamp = finishedAt.ToString("o"), type = "event_msg", payload = new { type = "item_completed",
                item = new { type = "AgentMessage", phase = "final_answer", content = "Collected **12** measurements." } } });
            clock = finishedAt.AddSeconds(1);
            var finished = catalog.Poll(null);
            Check(finished.ActiveChats == 2 && finished.ActiveChat.Id != inputId &&
                  finished.ActiveChatEntries.Last().Chat.Id == inputId && finished.ActiveChatEntries.Last().Status == "Done" &&
                  finished.ActiveChatEntries.Last().Update == "Collected 12 measurements.",
                "A completed chat briefly stays as Done with its public result but no longer counts as active");
            clock = finishedAt.AddMinutes(2);
            Check(catalog.Poll(null).ActiveChatEntries.All(entry => entry.Chat.Id != inputId),
                "Done expires two minutes after the recorded completion");
        }

        private static void CheckTaskbarBridge(string root)
        {
            var name = TaskbarBridge.Instance + ".Test." + Guid.NewGuid().ToString("N");
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var bridge = new TaskbarBridge(name))
            using (var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name + ".Status"))
            using (var view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
            {
                bridge.Publish("Done", "Reviewed résumé ✓");
                var fields = new ushort[512];
                view.ReadArray(4, fields, 0, fields.Length);
                Check(view.ReadInt32(0) > 0 && view.ReadInt32(0) % 2 == 0 &&
                      new string(fields.Take(256).TakeWhile(code => code != 0).Select(code => (char)code).ToArray()) == "Done" &&
                      new string(fields.Skip(256).TakeWhile(code => code != 0).Select(code => (char)code).ToArray()) == "Reviewed résumé ✓",
                    "Taskbar publishes complete UTF-16 status records, including completion text");
                var revision = view.ReadInt32(0);
                bridge.Publish("Done", "Reviewed résumé ✓");
                Check(view.ReadInt32(0) == revision, "An unchanged taskbar status does not rewrite the shared record");
                Check(view.ReadUInt16(4 + 255 * 2) == 1, "Done clears the native activity spinner");
                bridge.Publish("Working", "Checking output", true);
                Check(view.ReadUInt16(4 + 255 * 2) == 3, "The taskbar status enables the spinner for working agents");
                bridge.SetAnimations(false);
                Check(view.ReadUInt16(4 + 255 * 2) == 2, "Turning off animation keeps an accessible static activity indicator");
                var widget = new Widget(new Options(new[] { "--taskbar", "--demo", "--workspace", root }), signal, bridge);
                var panel = widget.Window;
                try
                {
                    Check(!panel.IsVisible, "Taskbar mode starts without a floating window");
                    signal.Set(); Pump(200);
                    Check(panel.IsVisible && !panel.ShowInTaskbar && !((FrameworkElement)panel.FindName("PillView")).IsVisible,
                        "A taskbar click opens only the anchored panel");
                    Press(panel, "SettingsButton");
                    panel.UpdateLayout();
                    Check(((FrameworkElement)panel.FindName("TaskbarPositionSettings")).IsVisible,
                        "Panel offsets are available in taskbar settings");
                    var left = panel.Left;
                    var top = panel.Top;
                    ((Slider)panel.FindName("PanelHorizontalOffset")).Value = 32;
                    ((Slider)panel.FindName("PanelTaskbarGap")).Value = 24;
                    Press(panel, "ApplyPanelPosition");
                    var savedPosition = Json.Parse(File.ReadAllText(Path.Combine(root, Product.DataDirectory, "taskbar-panel.json")));
                    Check(Math.Abs(panel.Left - left - 24) < 1 && Math.Abs(Math.Abs(panel.Top - top) - 16) < 1 &&
                        (int)Json.Get(savedPosition, "horizontalOffset") == 32 && (int)Json.Get(savedPosition, "taskbarGap") == 24,
                        "Panel offset settings move the open panel and save both values");
                    Press(panel, "ResetPanelPosition");
                    Check(Math.Abs(panel.Left - left) < 1 && Math.Abs(panel.Top - top) < 1,
                        "Reset restores the default expanded panel position");
                    signal.Set(); Pump(200);
                    Check(!panel.IsVisible, "A second taskbar click closes the expanded panel");
                    signal.Set(); Pump(200);
                    Check(panel.IsVisible, "The taskbar click can reopen the dismissed panel");
                    var otherWindow = new Window { Width = 120, Height = 80, ShowInTaskbar = false, ShowActivated = false };
                    try
                    {
                        otherWindow.Show();
                        panel.Activate(); Pump(100);
                        otherWindow.Activate(); Pump(180);
                        Check(!panel.IsVisible, "Moving focus outside the taskbar panel dismisses it");
                    }
                    finally { otherWindow.Close(); }
                    signal.Set(); Pump(200);
                    Press(panel, "CollapseButton"); Pump(150);
                    Check(!panel.IsVisible, "Closing the taskbar panel hides it instead of showing the movable pill");
                }
                finally { panel.Close(); }
                using (var stop = EventWaitHandle.OpenExisting(name + ".Stop")) stop.Set();
                Check(bridge.StopRequested, "The Windhawk unload signal is scoped to the taskbar companion");
            }
        }

        private static void CheckCompletionPill(string root)
        {
            var home = Path.Combine(root, "completion-pill");
            var folder = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
            var path = Path.Combine(folder, "run.jsonl");
            var id = "01a00000-0000-7000-8000-000000000025";
            var finished = DateTime.UtcNow.AddSeconds(-2);
            var started = finished.AddMinutes(-1);
            WriteSession(path, id, "", id, root, "cli", started);
            var child = Path.Combine(folder, "child.jsonl");
            WriteSession(child, "completion-child", id, id, root, "cli", started.AddSeconds(5));
            AppendJson(Path.Combine(home, "session_index.jsonl"), new { id = id, thread_name = "Finish the parser" });
            WritePublicResult(path, finished, "Parser ready.");
            var clock = finished.AddSeconds(1);
            var catalog = new ChatCatalog(home, null, root, () => clock);
            var snapshot = catalog.Poll(null);
            var done = snapshot.ActiveChatEntries.Single();
            Check(done.Activity == ChatActivity.Done, "An older child status cannot keep a finished lead active");
            var state = new ChatPillState();
            state.Observe(snapshot);
            clock = finished.AddMinutes(3);
            var expired = catalog.Poll(null);
            Check(expired.ActiveChatEntries.Count == 0, "An old completion leaves the Active list");
            state.Observe(expired);
            Check(state.Target == done, "A displayed Done pill holds past the Active list timeout");
            Check(state.Acknowledge(done) && state.Target == null && state.IsAcknowledged(done),
                "Opening the Done pill acknowledges its turn immediately");
            var updatedTimestamp = new ActiveChatEntry { Chat = done.Chat, Activity = ChatActivity.Done,
                StartedAt = started, CompletedAt = finished.AddSeconds(4) };
            snapshot.ActiveChatEntries = new List<ActiveChatEntry> { updatedTimestamp };
            state.Observe(snapshot);
            Check(state.Target == null, "A follow-up completion record cannot revive an acknowledged turn");
            var nextRun = new ActiveChatEntry { Chat = done.Chat, Activity = ChatActivity.Done,
                StartedAt = finished.AddMinutes(1), CompletedAt = finished.AddMinutes(2) };
            snapshot.ActiveChatEntries = new List<ActiveChatEntry> { nextRun };
            state.Observe(snapshot);
            Check(state.Target == nextRun, "A later turn in the same chat gets its own Done notification");
            var newChat = new ChatSummary { Id = "new-work" };
            snapshot.ActiveChatEntries.Insert(0, new ActiveChatEntry { Chat = newChat, Activity = ChatActivity.Working });
            state.Observe(snapshot);
            Check(state.Target.Chat == newChat, "New work replaces the Done pill");
            snapshot.ActiveChatEntries.RemoveAt(0);
            state.Observe(snapshot);
            Check(state.Target == null, "A superseded completion does not resurface");

            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                var options = new Options(new[] { "--workspace", root, "--all-projects" }) { CatalogHome = home };
                var window = new Widget(options, signal).Window;
                try
                {
                    window.Show(); Pump(550);
                    Press(window, "CollapseButton"); Pump(450);
                    var title = (TextBlock)window.FindName("PillTitle");
                    var result = (TextBlock)window.FindName("PillSubtitle");
                    Check(title.Text == "Done" && result.Text == "Parser ready.",
                        "The collapsed widget displays Done with the completed chat result");
                    Render(window, Path.Combine(root, "done-pill.png"));
                    Press(window, "ExpandButton"); Pump(700);
                    Check(((TextBlock)window.FindName("ProjectLabel")).Text == "Finish the parser",
                        "Opening Done jumps into the completed chat");
                    Press(window, "CollapseButton"); Pump(1900);
                    Check(title.Text == "No active chats",
                        "Collapsing after opening Done returns to idle across a background poll");
                    Press(window, "ExpandButton"); Pump(500);
                    var rows = (TreeView)window.FindName("AgentTreeView");
                    Check(rows.Items.Count == 0 && ((FrameworkElement)window.FindName("CatalogTools")).IsVisible,
                        "The acknowledged chat is gone from Active when the idle pill opens");
                    Press(window, "CollapseButton"); Pump(450);
                    var restart = DateTime.UtcNow;
                    AppendJson(path, new { timestamp = restart.ToString("o"), type = "event_msg", payload = new { type = "task_started" } });
                    WritePublicResult(path, restart.AddMilliseconds(10), "Second result ready.");
                    Pump(2200);
                    Check(title.Text == "Done" && result.Text == "Second result ready.",
                        "The widget shows a new completion from the same chat");
                }
                finally { window.Close(); }
            }
        }

        private static void WritePublicResult(string path, DateTime at, string content)
        {
            AppendJson(path, new { timestamp = at.ToString("o"), type = "event_msg", payload = new {
                type = "item_completed", item = new { type = "AgentMessage", phase = "final_answer", content = content }
            } });
        }

        private static void CheckQualityOfLifeGraph(string root)
        {
            var agents = new[] {
                new Agent { Id = "root", IsRoot = true, State = "working", Workspace = root },
                new Agent { Id = "branch", Name = "Builder", ParentId = "root", State = "completed", Workspace = root },
                new Agent { Id = "nested", Name = "Nested reviewer", ParentId = "branch", State = "completed", Workspace = root },
                new Agent { Id = "active", Name = "Verifier", ParentId = "root", State = "attention", Workspace = root }
            };
            agents[1].ChangedFiles.Add(Path.Combine(root, "build", "Script.ps1"));
            agents[2].ChangedFiles.Add(Path.Combine(root, "review.md"));
            var folds = new HashSet<string>(new[] { "branch", "root" });
            var collapsed = AgentGraphLayout.FromOrdered(AgentGraphLayout.OrderedAgents(agents), folds);
            Check(collapsed.Nodes.Count == 3 && collapsed.EdgeCount == 2 && collapsed.Nodes.Any(node => node.Agent.Id == "active") &&
                  collapsed.Nodes.Single(node => node.Agent.Id == "branch").FoldedCount == 2,
                "Only fully completed branches fold and their summary counts the branch root");
            var retained = AgentGraphLayout.FromOrdered(AgentGraphLayout.OrderedAgents(agents), folds, "nested");
            Check(retained.Nodes.Any(node => node.Agent.Id == "nested"), "Folding never hides the selected node");
            var graph = new AgentGraphWindow { GentleMotion = false, Width = 690, Height = 510 };
            try
            {
                graph.Update("Quality of life fixture", agents, root);
                graph.Show(); Pump(170);
                graph.SelectAgent("branch");
                var fold = FindVisual<Button>(graph, button => button.Content as string == "Collapse branch");
                fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, fold)); Pump(70);
                Check(graph.NodeCount == 3 && graph.SelectedAgentId == "branch", "The native fold control leaves the selected summary visible");
                graph.Update("Quality of life fixture", agents, root);
                Check(graph.NodeCount == 3 && graph.SelectedAgentId == "branch", "A live refresh preserves the folded branch and selection");
                Render(graph, Path.Combine(root, "qol-collapsed-branch.png"));

                fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, fold));
                Check(graph.NodeCount == 4 && graph.SelectedAgentId == "branch",
                    "Expanding a branch restores the nested agents and keeps the lead selected");
                fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, fold));
                agents[2].State = "working";
                graph.Update("Quality of life fixture", agents, root);
                Check(graph.NodeCount == 4 && graph.SelectedAgentId == "branch", "A folded branch unfolds when a descendant starts working again");
                graph.ShowFiles(); Pump(110);
                var nested = FindVisual<TextBlock>(graph, text => text.IsVisible && text.Inlines.OfType<System.Windows.Documents.Hyperlink>()
                    .Any(link => (string)link.ToolTip == Path.Combine(root, "review.md")));
                var script = FindVisual<TextBlock>(graph, text => text.IsVisible && text.Inlines.OfType<System.Windows.Documents.Hyperlink>()
                    .Any(link => (string)link.ToolTip == Path.Combine(root, "build", "Script.ps1")));
                Check(nested != null && script != null &&
                      FindVisual<TextBlock>(graph, text => text.IsVisible && text.Text == "/Nested reviewer") != null,
                    "Files view groups clickable recorded edits by agent across the whole chat");
                Render(graph, Path.Combine(root, "qol-files-changed.png"));
            }
            finally { graph.Close(); }
        }

        private static void CheckProjectCatalog(string root)
        {
            var home = Path.Combine(root, "catalog");
            var recent = Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "11", "20")).FullName;
            var old = Directory.CreateDirectory(Path.Combine(home, "sessions", "2025", "01", "02")).FullName;
            var archived = Directory.CreateDirectory(Path.Combine(home, "archived_sessions")).FullName;
            var first = Path.Combine(root, "Field Notes");
            var second = Path.Combine(root, "Weather Station");
            Directory.CreateDirectory(Path.Combine(second, ".git"));
            var now = DateTime.UtcNow.AddMinutes(-3);
            WriteSession(Path.Combine(old, "first.jsonl"), "catalog-main", "", "", first, "cli", now);
            WriteSession(Path.Combine(recent, "child.jsonl"), "catalog-child", "catalog-main", "", second, "cli", now);
            var agentLog = Path.Combine(old, "first.jsonl");
            AppendJson(agentLog, new { timestamp = now.ToString("o"), type = "event_msg", payload = new { type = "item_completed",
                item = new { type = "CommandExecution", status = "completed", exit_code = 0, parsed_cmd = new[] { new { type = "read", path = Path.Combine(first, "brief.md") } } } } });
            var reportLink = "Built the [booking planner](<plan.mjs:12>).\n\n" +
                "| Role | Effort | Observed result |\n| --- | --- | --- |\n" +
                "| Coordinator | Medium | Assigned work against a shared contract. |\n" +
                "| Optimizer | Medium | Matched the reference on all 120 cases. |\n" +
                "| Reviewer | Low | Approved both revisions. |\n\n" +
                "The CLI selected **A, C, E** for a score of 18.";
            AppendJson(agentLog, new { timestamp = now.ToString("o"), type = "event_msg", payload = new { type = "item_completed",
                item = new { type = "AgentMessage", phase = "final_answer", content = reportLink } } });
            AppendJson(Path.Combine(old, "first.jsonl"), new { timestamp = now.ToString("o"), type = "event_msg", payload = new { type = "task_complete" } });
            AppendJson(Path.Combine(recent, "child.jsonl"), new { timestamp = now.ToString("o"), type = "event_msg", payload = new { type = "task_complete" } });
            WriteSession(Path.Combine(recent, "second.jsonl"), "catalog-second", "", "", second, "cli", now.AddMinutes(-10));
            WriteSession(Path.Combine(archived, "archived.jsonl"), "catalog-archived", "", "", first, "cli", now);
            WriteSession(Path.Combine(recent, "loose.jsonl"), "catalog-loose", "", "", first, "cli", now.AddMinutes(-10));
            File.SetLastWriteTimeUtc(Path.Combine(recent, "second.jsonl"), now.AddMinutes(-5));
            File.SetLastWriteTimeUtc(Path.Combine(recent, "loose.jsonl"), now.AddMinutes(-10));
            File.SetLastWriteTimeUtc(Path.Combine(archived, "archived.jsonl"), now.AddMinutes(-15));
            var index = Path.Combine(home, "session_index.jsonl");
            AppendJson(index, new { id = "catalog-main", thread_name = "Organize the research notes" });
            AppendJson(index, new { id = "catalog-second", thread_name = "Check the weather station readings" });
            AppendJson(index, new { id = "catalog-loose", thread_name = "Draft a quick checklist" });
            AppendJson(index, new { id = "catalog-archived", thread_name = "Review last month’s notes" });
            AppendJson(Path.Combine(home, ".codex-global-state.json"), new Dictionary<string, object> {
                { "local-projects", new { field = new { id = "field", name = "Field Notes", rootPaths = new[] { first } } } },
                { "thread-project-assignments", new Dictionary<string, object> {
                    { "catalog-main", new { projectKind = "local", projectId = "field" } },
                    { "catalog-explicit", new { projectKind = "local", projectId = "field" } }
                } },
                { "projectless-thread-ids", new[] { "catalog-loose" } }
            });
            var catalog = new ChatCatalog(home, null);
            var listing = catalog.Poll(null).Chats;
            Check(listing.Count == 4 && listing.Select(chat => chat.ProjectKey).Distinct().Count() == 3,
                "Catalog groups chats across dates and folders, without listing children as chats");
            Check(listing.Single(chat => chat.Id == "catalog-main").Title == "Organize the research notes" &&
                listing.Single(chat => chat.Id == "catalog-archived").Archived &&
                listing.Single(chat => chat.Id == "catalog-second").ProjectName == "Weather Station" &&
                listing.Single(chat => chat.Id == "catalog-loose").ProjectName == "Projectless chats",
                "Catalog picks up desktop titles, archived chats, folder names, and projectless assignments");
            Check(catalog.Poll("catalog-main").Agents.Any(agent => agent.Id == "catalog-child" && agent.ParentId == "catalog-main"),
                "Selected chats include subagents from other date folders and workspaces");
            Check(catalog.Poll("catalog-archived").Agents.Single().Id == "catalog-archived",
                "Archived chat activity opens from the catalog");
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                var settings = new Options(new[] { "--workspace", root, "--all-projects" }) { CatalogHome = home };
                var window = new Widget(settings, signal).Window;
                window.Show(); Pump(500);
                Press(window, "AllChatsButton"); Pump(150);
                var tree = (TreeView)window.FindName("AgentTreeView");
                Check(tree.Items.Count == 3, "All shows one row per project group");
                Render(window, Path.Combine(root, "projects.png"));
                var button = FindChatButton(tree, "catalog-main");
                Check(button != null, "A chat in the projects view has a clickable row");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                Pump(500);
                Check(Flatten(tree.Items.Cast<TreeNode>()).Count(node => node.Kind == "agent") == 2,
                    "Clicking a chat switches the widget to that chat's agent tree");
                Check(((TextBlock)window.FindName("SourceLabel")).Text == "Local activity",
                    "The chat footer omits session IDs and the time since its last update");
                Press(window, "GraphButton"); Pump(350);
                var graphWindow = window.OwnedWindows.OfType<AgentGraphWindow>().Single();
                Check(graphWindow.IsVisible && graphWindow.ActualWidth > window.ActualWidth &&
                      graphWindow.NodeCount == 2 && graphWindow.EdgeCount == 1,
                    "A chat opens its workflow in a larger owned Windows popup");
                Check(graphWindow.SelectedAgentId == "catalog-main" && graphWindow.SelectedActivity == reportLink,
                    "The graph inspector starts on the lead and preserves its full formatted result");
                Check(graphWindow.SelectAgent("catalog-child"), "A subagent node can be selected for inspection");
                var rootButton = FindAgentButton(tree, "catalog-main");
                rootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, rootButton)); Pump(200);
                var detail = (ActivityTextBlock)window.FindName("DetailTask");
                var eventsView = (ItemsControl)window.FindName("DetailEvents");
                Check(detail.Body == reportLink && detail.Inlines.OfType<System.Windows.Documents.Hyperlink>().Count() == 1 &&
                      eventsView.Items.Count == 0 && ((Button)window.FindName("ToggleReadsButton")).IsVisible,
                    "The expanded result preserves formatting and file reads start hidden");
                Render(window, Path.Combine(root, "formatted-agent-details.png"));
                Press(window, "ToggleReadsButton"); Pump(100);
                Check(eventsView.Items.Cast<ActivityEvent>().Single().Body.Contains("[brief.md]"),
                    "Showing routine reads restores their clickable file reference");
                Press(window, "BackButton");
                Press(window, "SourceButton"); Pump(500);
                Check(tree.Items.Count == 3 && ((TreeNode)tree.Items[0]).IsExpanded,
                    "Projects navigation returns to the grouped list and preserves expansion");
                Press(window, "CollapseButton"); Pump(400);
                Check(((TextBlock)window.FindName("PillTitle")).Text == "No active chats", "The minimized project list reports when no chats are active");
                AppendJson(Path.Combine(recent, "child.jsonl"),
                    new { timestamp = DateTime.UtcNow.ToString("o"), type = "event_msg", payload = new { type = "task_started" } });
                AppendJson(Path.Combine(recent, "child.jsonl"),
                    new { timestamp = DateTime.UtcNow.ToString("o"), type = "event_msg", payload = new { type = "item_completed",
                        item = new { type = "AgentMessage", phase = "commentary", content = "Checking overnight schedule boundaries." } } });
                File.SetLastWriteTimeUtc(Path.Combine(old, "first.jsonl"), now.AddHours(-1));
                File.SetLastWriteTimeUtc(Path.Combine(recent, "child.jsonl"), now.AddHours(-1));
                Check(new ChatCatalog(home, null).Poll(null).ActiveChat.Id == "catalog-main",
                    "Recent rollout events beat stale Windows file write times when detecting active chats");
                Pump(1800);
                Check(((TextBlock)window.FindName("PillTitle")).Text == "1 agent working" &&
                      ((TextBlock)window.FindName("PillSubtitle")).Text == "Organize the research notes",
                    "The collapsed project list detects activity in a subagent and names its chat");
                Check(graphWindow.IsVisible && graphWindow.SelectedAgentId == "catalog-child" &&
                      graphWindow.SelectedActivity == "Checking overnight schedule boundaries.",
                    "An open workflow retains its selected node and receives updates while the widget shows projects");
                using (File.Open(Path.Combine(recent, "child.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Pump(1800);
                    Check(graphWindow.Title.Contains("Some activity unavailable") && graphWindow.SelectedAgentId == "catalog-child",
                        "A partial rollout read is disclosed without losing the selected graph node");
                }
                Pump(1800);
                Check(!graphWindow.Title.Contains("Some activity unavailable"), "The graph clears its read warning when activity is available again");
                Render(window, Path.Combine(root, "active-chat-pill.png"));
                Press(window, "ExpandButton"); Pump(700);
                Check(((TextBlock)window.FindName("ProjectLabel")).Text == "Organize the research notes" &&
                      Flatten(tree.Items.Cast<TreeNode>()).Any(node => node.Key == "agent:catalog-child"),
                    "Opening the active projects pill jumps straight to the matching agent tree");
                Press(window, "SourceButton"); Pump(500);
                foreach (var group in tree.Items.Cast<TreeNode>()) group.IsExpanded = true;
                Pump(100);
                var secondChat = FindChatButton(tree, "catalog-second");
                secondChat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, secondChat)); Pump(500);
                Press(window, "GraphButton"); Pump(300);
                var otherGraph = window.OwnedWindows.OfType<AgentGraphWindow>().Single();
                Check(!graphWindow.IsVisible && !Object.ReferenceEquals(otherGraph, graphWindow) &&
                      otherGraph.SelectedAgentId == "catalog-second",
                    "Opening a different chat's workflow starts with that chat's own root and viewport");
                otherGraph.Close();
                window.Close();
            }
            var generated = Path.Combine(root, "Documents", "Codex");
            WriteSession(Path.Combine(recent, "hey-new.jsonl"), "hey-new", "", "", Path.Combine(generated, "2026-11-20", "hey"), "cli", now);
            WriteSession(Path.Combine(old, "hey-old.jsonl"), "hey-old", "", "", Path.Combine(generated, "2026-11-19", "hey"), "cli", now);
            WriteSession(Path.Combine(recent, "hey-explicit.jsonl"), "catalog-explicit", "", "", Path.Combine(generated, "2026-11-20", "hey-explicit"), "cli", now);
            WriteSession(Path.Combine(recent, "hey-invalid.jsonl"), "hey-invalid", "", "", Path.Combine(generated, "2026-02-30", "hey"), "cli", now);
            var withGenerated = new ChatCatalog(home, null, generated).Poll(null).Chats;
            Check(withGenerated.Where(chat => chat.ProjectKey == "projectless").Select(chat => chat.Id)
                    .OrderBy(id => id).SequenceEqual(new[] { "catalog-loose", "hey-new", "hey-old" }),
                "Codex-generated dated workspaces share the projectless group");
            Check(withGenerated.Single(chat => chat.Id == "catalog-explicit").ProjectKey == "codex:field" &&
                  withGenerated.Single(chat => chat.Id == "hey-invalid").ProjectKey != "projectless",
                "Explicit projects and folders outside the generated format retain their groups");
            WriteSession(Path.Combine(recent, "orphan.jsonl"), "orphan", "missing", "catalog-main", first, "cli", now);
            WriteSession(Path.Combine(recent, "orphan-child.jsonl"), "orphan-child", "orphan", "", first, "cli", now);
            WriteSession(Path.Combine(recent, "cycle-a.jsonl"), "cycle-a", "cycle-b", "catalog-main", first, "cli", now);
            WriteSession(Path.Combine(recent, "cycle-b.jsonl"), "cycle-b", "cycle-a", "", first, "cli", now);
            WriteSession(Path.Combine(recent, "attributed.jsonl"), "attributed", "", "catalog-main", first,
                new { subagent = new { thread_spawn = new { } } }, now);
            var damaged = new ChatCatalog(home, null).Poll("catalog-main");
            var damagedLayout = AgentGraphLayout.Create(damaged.Agents);
            Check(damagedLayout.Nodes.Count == 7 && damagedLayout.EdgeCount == 2 &&
                  damagedLayout.Nodes.Single(node => node.Agent.Id == "orphan-child").ParentId == "orphan",
                "The catalog keeps attributed orphan and cycle branches while the graph draws only their recorded valid edges");

            var duplicate = Path.Combine(recent, "side-shell.jsonl");
            AppendJson(duplicate, new { type = "session_meta", payload = new { id = "side-shell", cwd = first } });
            AppendJson(duplicate, new { type = "response_item", payload = new {
                type = "message", role = "user", content = new[] { new { type = "input_text",
                    text = "Side conversation boundary.\n\nEverything before this boundary is inherited history from the parent thread." } }
            } });
            AppendJson(duplicate, new { type = "event_msg", payload = new { type = "turn_aborted" } });
            var archivedShell = Path.Combine(archived, "archived-shell.jsonl");
            AppendJson(archivedShell, new { type = "session_meta", payload = new { id = "archived-shell", cwd = first } });
            AppendJson(archivedShell, new { type = "event_msg", payload = new { type = "thread_settings_applied" } });
            var separate = Path.Combine(recent, "different-request.jsonl");
            AppendJson(separate, new { type = "session_meta", payload = new { id = "different-request", cwd = first } });
            AppendJson(separate, new { type = "response_item", payload = new {
                type = "message", role = "user", content = new[] { new { type = "input_text", text = "Check a different set of research notes." } }
            } });
            AppendJson(Path.Combine(recent, "unrelated-workspace.jsonl"),
                new { type = "session_meta", payload = new { id = "unrelated-workspace", cwd = second } });
            AppendJson(index, new { id = "side-shell", thread_name = "Organize the research notes" });
            AppendJson(index, new { id = "archived-shell", thread_name = "Organize the research notes (2)" });
            AppendJson(index, new { id = "different-request", thread_name = "Organize the research notes" });
            AppendJson(index, new { id = "unrelated-workspace", thread_name = "Organize the research notes" });
            var deduped = new ChatCatalog(home, null).Poll(null).Chats.Select(chat => chat.Id).ToList();
            Check(!deduped.Contains("side-shell") && !deduped.Contains("archived-shell") &&
                  deduped.Contains("catalog-main") && deduped.Contains("different-request") && deduped.Contains("unrelated-workspace"),
                "Duplicate setup shells are hidden while separate conversations and workspaces remain listed");
            Check(File.Exists(duplicate) && File.Exists(archivedShell), "Deduplication leaves the rollout files in place");
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
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
