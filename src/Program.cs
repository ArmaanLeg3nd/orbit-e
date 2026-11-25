using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace OrbitEvolved
{
    public sealed class Options
    {
        public bool Demo;
        public bool Test;
        public bool AllProjects;
        internal bool Taskbar, OpenTaskbar;
        public string Workspace;
        internal string CatalogHome = ActivityReader.CodexHome();
        public string Session = "";
        public string Events = "";
        public string Sessions;
        internal StartupRegistration Startup = new StartupRegistration();
        internal Func<Uri, bool> CodexLauncher = CodexLink.Open;
        public Options(string[] args)
        {
            var location = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            Workspace = location.Name == "bin" ? location.Parent.FullName : location.FullName;
            var workspaceSelected = false;
            var allProjectsRequested = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--demo") Demo = true;
                else if (args[i] == "--live") Demo = false;
                else if (args[i] == "--all-projects") allProjectsRequested = true;
                else if (args[i] == "--self-test") Test = true;
                else if (args[i] == "--taskbar") Taskbar = true;
                else if (args[i] == "--taskbar-open") { Taskbar = true; OpenTaskbar = true; }
                else if (args[i] == "--workspace" && i + 1 < args.Length) { Workspace = Path.GetFullPath(args[++i]); workspaceSelected = true; }
                else if (args[i] == "--session" && i + 1 < args.Length) Session = args[++i];
                else if (args[i] == "--events" && i + 1 < args.Length) Events = Path.GetFullPath(args[++i]);
                else if (args[i] == "--sessions-dir" && i + 1 < args.Length) Sessions = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
            }
            AllProjects = Session == "" && Events == "" && (allProjectsRequested || !workspaceSelected);
        }
    }
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var options = new Options(args);
                if (options.Test) return SelfTests.Run();
                bool created;
                var instance = options.Taskbar ? TaskbarBridge.Instance : @"Local\OrbitEvolved.AgentWidget";
                using (var mutex = new Mutex(true, instance, out created))
                using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Show"))
                {
                    if (!created)
                    {
                        if (!options.Taskbar || options.OpenTaskbar) wake.Set();
                        return 0;
                    }
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    using (var bridge = options.Taskbar ? new TaskbarBridge() : null)
                    {
                        var widget = new Widget(options, wake, bridge);
                        if (options.Taskbar)
                        {
                            app.MainWindow = widget.Window;
                            if (options.OpenTaskbar) wake.Set();
                            app.Run();
                        }
                        else app.Run(widget.Window);
                        GC.KeepAlive(widget);
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orbit-evolved-error.log"), ex.ToString()); } catch (IOException) { }
                MessageBox.Show(Product.Name + " could not start.\n\n" + ex.Message, Product.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class Widget
    {
        public readonly Window Window;
        private readonly Options options;
        private readonly EventWaitHandle wake;
        private readonly TaskbarBridge bridge;
        private readonly ActivityReader reader;
        private readonly ChatCatalog catalog;
        private readonly DemoFeed demo = new DemoFeed();
        private readonly ObservableCollection<Agent> agents = new ObservableCollection<Agent>();
        private readonly AgentTree tree = new AgentTree();
        private readonly AgentTree catalogTree = new AgentTree();
        private List<ChatSummary> displayedCatalog;
        private readonly AgentTree activeCatalog = new AgentTree();
        private readonly ChatPillState pillState = new ChatPillState();
        private Snapshot catalogSnapshot;
        private bool activeOnly = true;
        private string selectedChat;
        private string openGraphForChat;
        private AgentGraphWindow graph;
        private string graphChatId;
        private Snapshot graphSnapshot;
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly Drawing.Icon trayIcon;
        private Snapshot current = new Snapshot();
        private bool preview;
        private bool collapsed;
        private bool polling;
        private bool closing;
        private bool animating;
        private bool expandRequested;
        private bool gentleMotion = true;
        private bool popAttention = true;
        private int corner = 3;
        private int tick;
        private int generation;
        private string detailId;
        private bool showFileReads;
        private readonly HashSet<string> attentionSeen = new HashSet<string>();
        private Point dragStart;
        private bool dragMoved;

        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        private void Visible(string name, bool visible) { Find<FrameworkElement>(name).Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

        public Widget(Options settings, EventWaitHandle signal) : this(settings, signal, null) { }

        internal Widget(Options settings, EventWaitHandle signal, TaskbarBridge bridge)
        {
            options = settings; wake = signal; preview = settings.Demo;
            this.bridge = bridge;
            reader = new ActivityReader(options.Workspace, options.Session, options.Events, options.Sessions);
            if (options.AllProjects) catalog = new ChatCatalog(options.CatalogHome, options.Sessions);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OrbitEvolved.Widget.xaml")) Window = (Window)XamlReader.Load(stream);
            LoadPreferences();
            Find<TreeView>("AgentTreeView").ItemsSource = tree.Roots;
            Find<TreeView>("AgentTreeView").AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e)
            {
                var button = e.OriginalSource as Button;
                if (button != null && button.Tag is Agent) OpenDetail(((Agent)button.Tag).Id);
                var chat = button == null ? null : button.Tag as ChatSummary;
                if (chat == null) return;
                if ((string)button.CommandParameter == "codex") OpenInCodex(chat.Id);
                else SelectChat(chat.Id, activeOnly);
            }));
            Click("CollapseButton", ToggleCollapsed);
            Click("ExpandButton", delegate { if (!dragMoved) OpenFromPill(); });
            Click("SettingsButton", ShowSettings);
            Click("SourceButton", delegate { if (preview) SetPreview(false); else if (selectedChat != null) SelectChat(null); else ShowSettings(); });
            Click("GraphButton", OpenGraph);
            Click("ChatFilesButton", delegate { OpenGraph(); if (graph != null) graph.ShowFiles(); });
            Click("AllChatsButton", delegate { activeOnly = false; RefreshCatalog(current); });
            Click("ActiveChatsButton", delegate { activeOnly = true; RefreshCatalog(current); });
            Click("SettingsBackButton", Back);
            Click("BackButton", Back);
            Click("PreviewButton", delegate { SetPreview(true); Back(); });
            Click("LiveButton", delegate { SetPreview(false); Back(); });
            Click("DemoButton", delegate { SetPreview(true); Back(); });
            Click("CopyButton", CopyActivity);
            Click("ToggleReadsButton", delegate { showFileReads = !showFileReads; RefreshDetail(); });
            Click("QuitButton", Quit);
            Click("TopLeftButton", delegate { corner = 0; PlaceCorner(); SavePreferences(); });
            Click("TopRightButton", delegate { corner = 1; PlaceCorner(); SavePreferences(); });
            Click("BottomLeftButton", delegate { corner = 2; PlaceCorner(); SavePreferences(); });
            Click("BottomRightButton", delegate { corner = 3; PlaceCorner(); SavePreferences(); });
            Find<CheckBox>("TopmostCheck").IsChecked = Window.Topmost;
            Find<CheckBox>("MotionCheck").IsChecked = gentleMotion;
            Find<CheckBox>("AttentionCheck").IsChecked = popAttention;
            Find<CheckBox>("TopmostCheck").Click += delegate { Window.Topmost = Find<CheckBox>("TopmostCheck").IsChecked == true; SavePreferences(); };
            Find<CheckBox>("MotionCheck").Click += delegate
            {
                gentleMotion = Find<CheckBox>("MotionCheck").IsChecked == true;
                if (graph != null) graph.GentleMotion = gentleMotion;
                SavePreferences();
            };
            Find<CheckBox>("AttentionCheck").Click += delegate { popAttention = Find<CheckBox>("AttentionCheck").IsChecked == true; SavePreferences(); };
            Find<CheckBox>("StartupCheck").Click += delegate
            {
                var enabled = Find<CheckBox>("StartupCheck").IsChecked == true;
                RefreshStartupSetting(options.Startup.SetEnabled(enabled));
            };
            if (options.Taskbar)
            {
                Find<Grid>("DragHeader").Cursor = Cursors.Arrow;
                Find<Button>("CollapseButton").ToolTip = "Close taskbar panel (Esc)";
                System.Windows.Automation.AutomationProperties.SetName(Find<Button>("CollapseButton"), "Close taskbar panel");
                Visible("CornerSettings", false);
                Visible("StartupCheck", false);
                Visible("AttentionCheck", false);
                Window.Deactivated += delegate { if (graph == null && !closing) HideTaskbarPanel(); };
            }
            else Find<Grid>("DragHeader").MouseLeftButtonDown += HeaderDrag;
            var pill = Find<Button>("ExpandButton");
            pill.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(Window); dragMoved = false; };
            pill.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || !pill.IsMouseCaptured || animating) return;
                var position = e.GetPosition(Window);
                if (Math.Abs(position.X - dragStart.X) + Math.Abs(position.Y - dragStart.Y) < 6) return;
                dragMoved = true; pill.ReleaseMouseCapture();
                try { Window.DragMove(); } catch (InvalidOperationException) { }
                SnapAfterDrag();
            };
            Window.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { if (Find<Grid>("SettingsView").Visibility == Visibility.Visible || detailId != null) Back(); else if (!collapsed) ToggleCollapsed(); e.Handled = true; }
                else if ((e.Key == Key.Enter || e.Key == Key.Space) && collapsed) { OpenFromPill(); e.Handled = true; }
                else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control) { Quit(); e.Handled = true; }
            };
            Window.Closing += delegate { closing = true; timer.Stop(); if (tray != null) tray.Dispose(); trayIcon.Dispose(); };
            Window.SourceInitialized += delegate { PlaceCorner(); };
            Window.Loaded += delegate
            {
                PlaceCorner();
                if (gentleMotion) { Window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))); }
                Poll();
            };
            trayIcon = MakeIcon();
            Window.Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            if (!options.Taskbar)
            {
                tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Product.Name + " · Agent activity", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show " + Product.Name, null, delegate { Window.Dispatcher.BeginInvoke(new Action(ShowWidget)); });
            menu.Items.Add("Collapse / expand", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { Window.Show(); ToggleCollapsed(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Live activity", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { SetPreview(false); ShowWidget(); })); });
            menu.Items.Add("Preview animation", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { SetPreview(true); ShowWidget(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Hide widget", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { Window.Hide(); })); });
            menu.Items.Add("Quit " + Product.Name, null, delegate { Window.Dispatcher.BeginInvoke(new Action(Quit)); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Window.Dispatcher.BeginInvoke(new Action(ShowWidget)); };
            }
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += delegate
            {
                if (bridge != null && bridge.StopRequested) { Quit(); return; }
                if (wake.WaitOne(0))
                {
                    if (options.Taskbar) OpenTaskbarPanel();
                    else ShowWidget();
                }
                tick++;
                if (tick % 3 == 0) Poll();
            };
            timer.Start();
            SetModeLabels();
            StyleSelections();
            Poll();
        }
        private void SetModeLabels()
        {
            Find<Button>("SourceButton").Content = preview ? "Go live" : selectedChat != null ? "← Projects" : String.IsNullOrEmpty(options.Events) ? "Codex" : "File";
            Find<Button>("LiveButton").Content = String.IsNullOrEmpty(options.Events) ? "Live · Codex" : "Live · File";
            Text("SettingsSource", preview ? "Simulated agent activity." : "Read-only activity from local sessions.");
            StyleSelections();
        }
        private void StyleSelections()
        {
            Find<Button>("LiveButton").Background = Palette.Color(!preview ? "#30FFFFFF" : "#0CFFFFFF");
            Find<Button>("DemoButton").Background = Palette.Color(preview ? "#30FFFFFF" : "#0CFFFFFF");
            var corners = new[] { "TopLeftButton", "TopRightButton", "BottomLeftButton", "BottomRightButton" };
            for (int i = 0; i < corners.Length; i++) Find<Button>(corners[i]).Background = Palette.Color(i == corner ? "#30FFFFFF" : "#0CFFFFFF");
        }
        private void SetPreview(bool enabled)
        {
            if (preview != enabled && graph != null) graph.Close();
            preview = enabled; selectedChat = null; openGraphForChat = null; generation++; detailId = null; attentionSeen.Clear(); agents.Clear(); tree.Clear();
            SetModeLabels(); Poll();
        }
        private void SelectChat(string id, bool showGraph = false)
        {
            if (catalogSnapshot != null && id != null)
            {
                var entry = pillState.Target;
                if (entry == null || entry.Chat.Id != id)
                    entry = catalogSnapshot.ActiveChatEntries.FirstOrDefault(item => item.Chat.Id == id);
                if (pillState.Acknowledge(entry)) UpdateCatalogPill(catalogSnapshot);
            }
            selectedChat = id;
            openGraphForChat = showGraph ? id : null;
            generation++; detailId = null; attentionSeen.Clear(); agents.Clear(); tree.Clear();
            Find<TreeView>("AgentTreeView").ItemsSource = id == null ? catalogTree.Roots : tree.Roots;
            Visible("GraphButton", false);
            Visible("ChatFilesButton", false);
            Visible("CatalogTools", false);
            Visible("EmptyState", false);
            Text("SummaryTitle", "Loading…");
            Text("SourceLabel", "Reading local activity…");
            SetModeLabels();
            Poll();
        }
        private void Poll()
        {
            if (closing) return;
            if (preview) { Apply(demo.Poll()); return; }
            if (polling) return;
            polling = true;
            int version = generation;
            var chat = selectedChat;
            var graphChat = graphChatId;
            Task.Factory.StartNew(delegate
            {
                var main = catalog == null ? reader.Poll() : catalog.Poll(chat, graphChat);
                var extra = catalog != null && graphChat != null && graphChat != chat ? catalog.Poll(graphChat) : null;
                var overview = options.Taskbar && catalog != null && main.Chats == null ? catalog.Poll(null, chat, graphChat) : null;
                return Tuple.Create(main, extra, overview);
            }).ContinueWith(task =>
            {
                if (closing || Window.Dispatcher.HasShutdownStarted) { var observed = task.Exception; return; }
                Window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    polling = false;
                    if (closing || version != generation || preview) return;
                    if (task.IsFaulted)
                    {
                        var observed = task.Exception;
                        foreach (var agent in agents) { agent.State = "idle"; agent.Task = "Activity unavailable · retrying"; agent.Ended = agent.Updated; agent.Notify(); }
                        Text("SourceLabel", "Activity unavailable · retrying"); Text("SummaryTitle", "Reconnecting"); Text("PillTitle", "Activity unavailable"); tree.Refresh();
                        if (detailId != null) RefreshDetail();
                        if (graph != null && graphChatId == graphChat) ShowGraphUnavailable(false);
                    }
                    else
                    {
                        Apply(task.Result.Item1);
                        if (task.Result.Item3 != null)
                        {
                            catalogSnapshot = task.Result.Item3;
                            pillState.Observe(catalogSnapshot);
                            UpdateCatalogPill(catalogSnapshot);
                        }
                        if (graph != null && graphChatId == graphChat)
                        {
                            if (task.Result.Item2 != null) UpdateGraph(task.Result.Item2);
                            else if (chat != null && graphChat == chat && task.Result.Item1.Chats != null)
                                ShowGraphUnavailable(true);
                        }
                    }
                }));
            });
        }
        private void Apply(Snapshot snapshot)
        {
            current = snapshot;
            var treeView = Find<TreeView>("AgentTreeView");
            if (snapshot.Chats != null)
            {
                Visible("GraphButton", false);
                Visible("ChatFilesButton", false);
                Visible("CatalogTools", true);
                if (selectedChat != null) { selectedChat = null; openGraphForChat = null; SetModeLabels(); }
                if (!Object.ReferenceEquals(displayedCatalog, snapshot.Chats))
                {
                    catalogTree.UpdateCatalog(snapshot.Chats);
                    displayedCatalog = snapshot.Chats;
                }
                catalogSnapshot = snapshot;
                pillState.Observe(snapshot);
                RefreshCatalog(snapshot);
                Text("ProjectLabel", snapshot.Project);
                Text("SummaryTitle", snapshot.ActiveChats > 0
                    ? snapshot.ActiveChats + (snapshot.ActiveChats == 1 ? " active chat" : " active chats")
                    : activeOnly ? "Watching local chats" : catalogTree.Roots.Count == 1 ? "1 project group" : catalogTree.Roots.Count + " project groups");
                Text("SourceLabel", snapshot.Source);
                UpdateCatalogPill(snapshot);
                return;
            }
            treeView.ItemsSource = tree.Roots;
            Visible("CatalogTools", false);
            Visible("PreviewButton", true);
            Visible("GraphButton", preview || selectedChat != null || snapshot.Agents.Count > 0);
            Visible("ChatFilesButton", selectedChat != null);
            var expand = Find<Button>("ExpandButton");
            expand.ToolTip = null;
            System.Windows.Automation.AutomationProperties.SetName(expand, "Expand " + Product.Name + " agent activity");
            System.Windows.Automation.AutomationProperties.SetName(treeView, "Session, agents, and activity tree");
            if (!preview && snapshot.Problem != "" && snapshot.Agents.Count == 0 && agents.Count > 0)
            {
                // Do not present stale records as still working after a read failure.
                foreach (var agent in agents) { agent.State = "idle"; agent.Task = "Activity unavailable · reconnecting"; agent.Ended = agent.Updated; agent.Notify(); }
            }
            else
            {
                foreach (var removed in agents.Where(a => !snapshot.Agents.Any(n => n.Id == a.Id)).ToList()) agents.Remove(removed);
                for (int i = 0; i < snapshot.Agents.Count; i++)
                {
                    var next = snapshot.Agents[i];
                    var existing = agents.FirstOrDefault(a => a.Id == next.Id);
                    if (existing == null) agents.Insert(Math.Min(i, agents.Count), next);
                    else { existing.Refresh(next); if (agents.IndexOf(existing) != i) agents.Move(agents.IndexOf(existing), i); }
                    var attentionKey = next.Id + ":" + next.State;
                    if (next.State == "attention" || next.State == "error")
                    {
                        if (attentionSeen.Add(attentionKey) && collapsed && popAttention && !preview) RequestExpand();
                    }
                    else { attentionSeen.Remove(next.Id + ":attention"); attentionSeen.Remove(next.Id + ":error"); }
                }
            }
            var working = agents.Count(a => a.State == "working");
            var waiting = agents.Count(a => a.State == "waiting" || a.State == "attention" || a.State == "error");
            var done = agents.Count(a => a.State == "completed");
            var title = waiting > 0 && agents.Any(a => a.State == "attention" || a.State == "error") ? "Your team needs you" : working > 0 ? working + (working == 1 ? " agent working" : " agents working") : agents.Count > 0 && done == agents.Count ? "All work wrapped up" : waiting > 0 ? "Waiting on the next step" : agents.Count > 0 ? "Your team is quiet" : "Ready for your team";
            Text("SummaryTitle", agents.Any(a => a.State == "attention" || a.State == "error") ? "Needs attention" : working > 0 ? working + " working" : waiting > 0 ? waiting + " waiting" : done > 0 ? done + " done" : "Waiting for agents");
            Text("PillTitle", title); Text("ProjectLabel", snapshot.Project);
            Text("SourceLabel", snapshot.Source);
            Find<TextBlock>("SourceLabel").ToolTip = snapshot.Problem == "" ? (preview ? "Simulated events for exploring the widget." : "Read-only local activity. No data leaves your computer.") : snapshot.Problem;
            var sessionCount = preview ? 0 : snapshot.TotalSessions;
            Text("PillSubtitle", preview ? "Preview · simulated activity" : sessionCount > 1 ? sessionCount + " sessions · " + working + " working" : waiting > 0 ? waiting + " waiting · " + done + " done" : done > 0 ? done + " completed · " + snapshot.Project : Product.Name + " · " + snapshot.Project);
            Visible("EmptyState", agents.Count == 0);
            Text("EmptyHeading", "No agents yet");
            Text("EmptyDescription", snapshot.Problem != "" ? snapshot.Problem : "Agents will appear as they start working.");
            tree.Update(agents, !String.IsNullOrEmpty(snapshot.FocusedSession) ? snapshot.FocusedSession : snapshot.Session, snapshot.Project);
            if (detailId != null) RefreshDetail();
            if (openGraphForChat != null && openGraphForChat == selectedChat)
            {
                openGraphForChat = null;
                OpenGraph();
            }
            else if (graph != null && graphChatId == selectedChat) UpdateGraph(snapshot);
            if (tray != null) tray.Text = Json.Clean(Product.Name + " · " + (preview ? "Preview · " : "") + title, 63);
            if (bridge != null && (preview || catalog == null)) bridge.Publish(title, snapshot.Project);
        }
        private void UpdateCatalogPill(Snapshot snapshot)
        {
            var entries = snapshot.ActiveChatEntries;
            var target = pillState.Target;
            var title = target == null ? "No active chats" : target.Status;
            var subtitle = target == null ? snapshot.Project : target.Chat.Title;
            if (target != null && target.Activity == ChatActivity.Done && !String.IsNullOrWhiteSpace(target.Update))
                subtitle = target.Update;
            var accessibleStatus = title;
            if (snapshot.ActiveChats > 1)
            {
                var parts = new[] {
                    StatusCount(entries.Count(entry => entry.NeedsAttention), "needs you", "need you"),
                    StatusCount(entries.Count(entry => entry.Activity == ChatActivity.Working), "working", "working"),
                    StatusCount(entries.Count(entry => entry.Activity == ChatActivity.Waiting), "waiting", "waiting"),
                    StatusCount(entries.Count(entry => entry.Activity == ChatActivity.Quiet), "quiet", "quiet")
                }.Where(part => part != "").ToList();
                title = String.Join(" · ", parts.Take(2));
                subtitle = parts.Count > 2 ? String.Join(" · ", parts.Skip(2)) : snapshot.ActiveChats + " active chats";
                accessibleStatus = String.Join(", ", parts);
            }
            else if (target != null && target.Activity == ChatActivity.Working)
            {
                title = StatusCount(target.WorkingAgents, "agent working", "agents working");
                accessibleStatus = title;
            }
            Text("PillTitle", title);
            Text("PillSubtitle", subtitle);
            var button = Find<Button>("ExpandButton");
            var targetLabel = target == null ? "Open Codex projects" : "Open " + target.Chat.Title + ", " + accessibleStatus;
            if (target != null && target.Activity == ChatActivity.Done && subtitle != target.Chat.Title)
                targetLabel += ", " + subtitle;
            button.ToolTip = targetLabel;
            System.Windows.Automation.AutomationProperties.SetName(button, targetLabel);
            if (tray != null) tray.Text = Json.Clean(Product.Name + " · " + title, 63);
            if (bridge != null) bridge.Publish(title, subtitle);
        }
        private static string StatusCount(int count, string singular, string plural)
        {
            return count == 0 ? "" : count + " " + (count == 1 ? singular : plural);
        }
        private void RefreshCatalog(Snapshot snapshot)
        {
            if (snapshot.Chats == null) return;
            if (activeOnly) activeCatalog.UpdateActiveCatalog(snapshot.ActiveChatEntries.Where(entry => !pillState.IsAcknowledged(entry)));
            var treeView = Find<TreeView>("AgentTreeView");
            treeView.ItemsSource = activeOnly ? activeCatalog.Roots : catalogTree.Roots;
            Find<Button>("AllChatsButton").Background = Palette.Color(activeOnly ? "#0CFFFFFF" : "#30FFFFFF");
            Find<Button>("ActiveChatsButton").Background = Palette.Color(activeOnly ? "#30FFFFFF" : "#0CFFFFFF");
            Find<Button>("ActiveChatsButton").Content = snapshot.ActiveChats > 0 ? "Active (" + snapshot.ActiveChats + ")" : "Active";
            System.Windows.Automation.AutomationProperties.SetName(treeView, activeOnly ? "Active chats" : "Codex projects and chats");
            Text("EmptyHeading", activeOnly && snapshot.Chats.Count > 0 ? "No active chats" : "No local chats yet");
            Text("EmptyDescription", snapshot.Problem);
            Visible("PreviewButton", snapshot.Chats.Count == 0);
            Visible("EmptyState", activeOnly ? activeCatalog.Roots.Count == 0 : snapshot.Chats.Count == 0);
        }
        private void OpenInCodex(string id)
        {
            if (!options.CodexLauncher(CodexLink.ForChat(id)))
                MessageBox.Show(Window, "Codex Desktop could not open this chat.", Product.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void OpenGraph()
        {
            if (current.Chats != null) return;
            if (graph != null && graphChatId != selectedChat) graph.Close();
            if (graph == null)
            {
                graph = new AgentGraphWindow { Owner = Window, GentleMotion = gentleMotion };
                graph.Closed += delegate { graph = null; graphChatId = null; graphSnapshot = null; };
            }
            graphChatId = selectedChat;
            var codexChat = graphChatId ?? (!preview && options.Events == "" ? current.FocusedSession : null);
            if (String.IsNullOrEmpty(codexChat) && !preview && options.Events == "") codexChat = current.Session;
            var chatId = CodexLink.ForChat(codexChat) == null ? null : codexChat;
            graph.SetOpenCodex(chatId == null ? (Action)null : delegate { OpenInCodex(chatId); });
            UpdateGraph(current);
            if (!graph.IsVisible) graph.Show();
            if (graph.WindowState == WindowState.Minimized) graph.WindowState = WindowState.Normal;
            graph.Activate();
        }
        private void UpdateGraph(Snapshot snapshot)
        {
            if (snapshot.Chats != null) { ShowGraphUnavailable(true); return; }
            if (!snapshot.Connected && snapshot.Problem != "" && graphSnapshot != null) { ShowGraphUnavailable(false); return; }
            graphSnapshot = snapshot;
            var title = snapshot.Project;
            if (snapshot.Problem != "") title = "Some activity unavailable · " + title;
            else if (snapshot.CatchingUp) title = "Catching up · " + title;
            graph.Update(title, snapshot.Agents, options.Workspace);
        }
        private void ShowGraphUnavailable(bool removed)
        {
            if (graphSnapshot == null) return;
            var title = (removed ? "Chat unavailable · " : "Reconnecting · ") + graphSnapshot.Project;
            graph.Update(title, removed ? Enumerable.Empty<Agent>() : graphSnapshot.Agents, options.Workspace);
        }
        private void OpenDetail(string id)
        {
            Find<Button>("CopyButton").Content = "Copy activity";
            showFileReads = false;
            Find<ScrollViewer>("DetailScroll").ScrollToTop();
            detailId = id; Visible("ExpandedView", false); Visible("SettingsView", false); Visible("DetailView", true); RefreshDetail();
            Find<Button>("BackButton").Focus(); FadeIn(Find<Grid>("DetailView"));
        }
        private void RefreshDetail()
        {
            var agent = agents.FirstOrDefault(a => a.Id == detailId);
            if (agent == null) { Back(); return; }
            var summary = agent.ActivitySummary;
            Text("DetailName", agent.DisplayName);
            Text("DetailStatus", agent.StatusLabel);
            var workspace = String.IsNullOrEmpty(agent.Workspace) ? options.Workspace : agent.Workspace;
            var task = Find<ActivityTextBlock>("DetailTask");
            task.Workspace = workspace;
            task.Body = agent.ActivityDetail;
            Find<TextBlock>("DetailStatus").Foreground = agent.AccentBrush;
            var events = agent.Events.Where(entry => !entry.IsLifecycle).OrderByDescending(entry => entry.At).ToList();
            var currentEvent = events.FindIndex(entry => entry.Text == summary || Json.Clean(entry.Text, 180) == summary);
            if (currentEvent >= 0) events.RemoveAt(currentEvent);
            var reads = events.Count(entry => entry.FileRead);
            Visible("ToggleReadsButton", reads > 0);
            Find<Button>("ToggleReadsButton").Content = showFileReads ? "Hide file reads" : "Show file reads (" + reads + ")";
            if (!showFileReads) events.RemoveAll(entry => entry.FileRead);
            Visible("DetailTask", summary != "");
            Visible("DetailHistory", events.Count > 0);
            var list = Find<ItemsControl>("DetailEvents");
            list.Tag = workspace;
            var shown = list.ItemsSource as IEnumerable<ActivityEvent>;
            if (shown == null || !shown.SequenceEqual(events)) list.ItemsSource = events;
        }
        private void Back()
        {
            detailId = null; Visible("ExpandedView", !collapsed); Visible("DetailView", false); Visible("SettingsView", false);
            Find<Button>("SettingsButton").Focus();
        }
        private void RefreshStartupSetting(string error)
        {
            string command;
            var available = options.Startup.TryRead(out command);
            var toggle = Find<CheckBox>("StartupCheck");
            toggle.IsEnabled = available;
            toggle.IsChecked = available && !String.IsNullOrEmpty(command);
            var hint = error ?? (!available ? "Windows startup settings are unavailable."
                : toggle.IsChecked == true && command != options.Startup.Command
                    ? "Another copy is registered. Turn this off and back on to use this copy."
                    : "");
            Text("StartupStatus", hint);
            Visible("StartupStatus", hint != "");
        }

        private void ShowSettings()
        {
            detailId = null; Visible("ExpandedView", false); Visible("DetailView", false); Visible("SettingsView", true);
            RefreshStartupSetting(null);
            Find<Button>("SettingsBackButton").Focus(); FadeIn(Find<Grid>("SettingsView"));
        }
        private void CopyActivity()
        {
            var agent = agents.FirstOrDefault(a => a.Id == detailId);
            if (agent == null) return;
            var text = agent.Name + " · " + agent.StatusLabel + "\r\n" + agent.ActivityDetail + "\r\n\r\n" + String.Join("\r\n", agent.Events.Select(e => e.At.ToLocalTime().ToString("HH:mm:ss") + "  " + e.Body));
            try { Clipboard.SetText((preview ? "[Preview data]\r\n" : "") + text); Find<Button>("CopyButton").Content = "Copied ✓"; }
            catch (System.Runtime.InteropServices.ExternalException) { Find<Button>("CopyButton").Content = "Clipboard busy · try again"; }
        }
        private void FadeIn(UIElement target)
        {
            if (gentleMotion) target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        private void HeaderDrag(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source != sender) { if (source is Button) return; source = VisualTreeHelper.GetParent(source); }
            if (animating) return;
            try { Window.DragMove(); } catch (InvalidOperationException) { }
            SnapAfterDrag();
        }
        private Rect WorkArea()
        {
            var handle = new WindowInteropHelper(Window).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
            var screen = (options.Taskbar ? Forms.Screen.PrimaryScreen : Forms.Screen.FromHandle(handle)).WorkingArea;
            var source = PresentationSource.FromVisual(Window);
            var matrix = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            return new Rect(matrix.Transform(new Point(screen.Left, screen.Top)), matrix.Transform(new Point(screen.Right, screen.Bottom)));
        }
        private void PlaceCorner()
        {
            if (animating) return;
            var area = WorkArea();
            var x = options.Taskbar || corner % 2 == 0 ? area.Left + 8 : area.Right - Window.Width - 8;
            var top = options.Taskbar ? TaskbarAtTop() : corner < 2;
            var y = top ? area.Top + 8 : area.Bottom - Window.Height - 8;
            Window.Left = Math.Max(area.Left, x); Window.Top = Math.Max(area.Top, y);
            StyleSelections();
        }
        private static bool TaskbarAtTop()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            NativeRect bounds;
            var screen = Forms.Screen.PrimaryScreen.Bounds;
            return taskbar != IntPtr.Zero && GetWindowRect(taskbar, out bounds) &&
                bounds.Top < screen.Top + screen.Height / 2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

        private void SnapAfterDrag()
        {
            var area = WorkArea();
            corner = (Window.Left + Window.Width / 2 < area.Left + area.Width / 2 ? 0 : 1) + (Window.Top + Window.Height / 2 < area.Top + area.Height / 2 ? 0 : 2);
            if (Math.Min(Math.Abs(Window.Left - area.Left), Math.Abs(Window.Left + Window.Width - area.Right)) < 60 || Math.Min(Math.Abs(Window.Top - area.Top), Math.Abs(Window.Top + Window.Height - area.Bottom)) < 60) PlaceCorner();
            Window.Left = Math.Max(area.Left, Math.Min(area.Right - Window.Width, Window.Left));
            Window.Top = Math.Max(area.Top, Math.Min(area.Bottom - Window.Height, Window.Top));
            SavePreferences();
        }
        private void ToggleCollapsed()
        {
            if (options.Taskbar) { HideTaskbarPanel(); return; }
            if (animating) return;
            collapsed = !collapsed; detailId = null;
            if (collapsed && !preview && catalog != null && selectedChat != null)
            {
                if (catalogSnapshot != null) UpdateCatalogPill(catalogSnapshot);
                SelectChat(null);
            }
            double width = collapsed ? 260 : 380;
            double height = collapsed ? 76 : 484;
            var area = WorkArea();
            var left = Math.Max(area.Left, Math.Min(area.Right - width, corner % 2 == 1 ? Window.Left + Window.Width - width : Window.Left));
            var top = Math.Max(area.Top, Math.Min(area.Bottom - height, corner >= 2 ? Window.Top + Window.Height - height : Window.Top));
            Visible("ExpandedView", !collapsed); Visible("SettingsView", false); Visible("DetailView", false); Visible("PillView", collapsed);
            Find<Border>("Surface").CornerRadius = new CornerRadius(collapsed ? 13 : 15);
            if (!gentleMotion) { Window.Width = width; Window.Height = height; Window.Left = left; Window.Top = top; return; }
            animating = true;
            AnimateWindow(FrameworkElement.WidthProperty, width);
            AnimateWindow(Window.LeftProperty, left);
            AnimateWindow(Window.TopProperty, top);
            var animation = new DoubleAnimation(height, TimeSpan.FromMilliseconds(270)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += delegate
            {
                Window.BeginAnimation(FrameworkElement.HeightProperty, null); Window.Height = height;
                Window.BeginAnimation(FrameworkElement.WidthProperty, null); Window.Width = width;
                Window.BeginAnimation(Window.LeftProperty, null); Window.Left = left;
                Window.BeginAnimation(Window.TopProperty, null); Window.Top = top;
                animating = false;
                if (!collapsed) FadeIn(Find<Grid>("ExpandedView"));
                if (expandRequested) { expandRequested = false; RequestExpand(); }
            };
            Window.BeginAnimation(FrameworkElement.HeightProperty, animation);
        }
        private void AnimateWindow(DependencyProperty property, double value)
        {
            Window.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(270)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        private void RequestExpand()
        {
            if (animating) { expandRequested = true; return; }
            if (collapsed) ToggleCollapsed();
        }
        private void OpenFromPill()
        {
            if (collapsed && selectedChat == null && !preview && catalogSnapshot != null && pillState.Target != null)
                SelectChat(pillState.Target.Chat.Id);
            RequestExpand();
        }
        private void OpenTaskbarPanel()
        {
            if (Window.IsVisible) { HideTaskbarPanel(); return; }
            Back();
            var target = preview ? null : pillState.Target;
            if (target != null) SelectChat(target.Chat.Id);
            Window.Show();
            PlaceCorner();
            Window.Activate();
        }

        private void HideTaskbarPanel()
        {
            if (!Window.IsVisible) return;
            Window.Hide();
            if (selectedChat != null) SelectChat(null);
        }

        private void ShowWidget() { Window.Show(); RequestExpand(); Window.Activate(); Back(); }
        private void Quit() { closing = true; Window.Close(); Application.Current.Shutdown(); }
        private void LoadPreferences()
        {
            gentleMotion = SystemParameters.ClientAreaAnimation;
            try
            {
                var path = Path.Combine(options.Workspace, Product.DataDirectory, "preferences.json");
                if (!File.Exists(path))
                    path = Path.Combine(options.Workspace, Product.LegacyDataDirectory, "preferences.json");
                if (!File.Exists(path)) return;
                var prefs = Json.Parse(File.ReadAllText(path));
                object value;
                if ((value = Json.Get(prefs, "corner")) is int) corner = Math.Max(0, Math.Min(3, (int)value));
                if ((value = Json.Get(prefs, "topmost")) is bool) Window.Topmost = (bool)value;
                if ((value = Json.Get(prefs, "motion")) is bool) gentleMotion = (bool)value && SystemParameters.ClientAreaAnimation;
                if ((value = Json.Get(prefs, "attention")) is bool) popAttention = (bool)value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void SavePreferences()
        {
            StyleSelections();
            try
            {
                var folder = Path.Combine(options.Workspace, Product.DataDirectory); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "preferences.json"), new JavaScriptSerializer().Serialize(new { corner = corner, topmost = Window.Topmost, motion = gentleMotion, attention = popAttention }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static Drawing.Icon MakeIcon()
        {
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(225, 225, 225), 2.2f))
            {
                graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Drawing.Color.Transparent);
                graphics.TranslateTransform(16, 16); graphics.RotateTransform(45); graphics.DrawEllipse(pen, -12, -6, 24, 12); graphics.RotateTransform(-90); graphics.DrawEllipse(pen, -12, -6, 24, 12);
                graphics.ResetTransform(); graphics.FillEllipse(Drawing.Brushes.White, 14, 14, 4, 4);
                var handle = bitmap.GetHicon();
                try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }
}
