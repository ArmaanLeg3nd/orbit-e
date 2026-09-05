using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
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

namespace Orbit
{
    public sealed class Options
    {
        public bool Demo;
        public bool Test;
        public string Workspace;
        public string Session = "";
        public string Events = "";
        public string Sessions;
        public Options(string[] args)
        {
            var location = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            Workspace = location.Name == "bin" ? location.Parent.FullName : location.FullName;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--demo") Demo = true;
                else if (args[i] == "--live") Demo = false;
                else if (args[i] == "--self-test") Test = true;
                else if (args[i] == "--workspace" && i + 1 < args.Length) Workspace = Path.GetFullPath(args[++i]);
                else if (args[i] == "--session" && i + 1 < args.Length) Session = args[++i];
                else if (args[i] == "--events" && i + 1 < args.Length) Events = Path.GetFullPath(args[++i]);
                else if (args[i] == "--sessions-dir" && i + 1 < args.Length) Sessions = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
            }
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
                using (var mutex = new Mutex(true, @"Local\Orbit.AgentWidget", out created))
                using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Orbit.AgentWidget.Show"))
                {
                    if (!created) { wake.Set(); return 0; }
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var widget = new Widget(options, wake);
                    app.Run(widget.Window);
                    GC.KeepAlive(widget);
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orbit-error.log"), ex.ToString()); } catch (IOException) { }
                MessageBox.Show("Orbit could not start.\n\n" + ex.Message, "Orbit", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class Widget
    {
        public readonly Window Window;
        private readonly Options options;
        private readonly EventWaitHandle wake;
        private readonly ActivityReader reader;
        private readonly DemoFeed demo = new DemoFeed();
        private readonly ObservableCollection<Agent> agents = new ObservableCollection<Agent>();
        private readonly AgentTree tree = new AgentTree();
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
        private readonly HashSet<string> attentionSeen = new HashSet<string>();
        private Point dragStart;
        private bool dragMoved;

        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        private void Visible(string name, bool visible) { Find<FrameworkElement>(name).Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

        public Widget(Options settings, EventWaitHandle signal)
        {
            options = settings; wake = signal; preview = settings.Demo;
            reader = new ActivityReader(options.Workspace, options.Session, options.Events, options.Sessions);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Orbit.Widget.xaml")) Window = (Window)XamlReader.Load(stream);
            LoadPreferences();
            Find<TreeView>("AgentTreeView").ItemsSource = tree.Roots;
            Find<TreeView>("AgentTreeView").AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e)
            {
                var button = e.OriginalSource as Button;
                if (button != null && button.Tag is Agent) OpenDetail(((Agent)button.Tag).Id);
            }));
            Click("CollapseButton", ToggleCollapsed);
            Click("ExpandButton", delegate { if (!dragMoved) RequestExpand(); });
            Click("SettingsButton", ShowSettings);
            Click("SourceButton", delegate { if (preview) SetPreview(false); else ShowSettings(); });
            Click("SettingsBackButton", Back);
            Click("BackButton", Back);
            Click("PreviewButton", delegate { SetPreview(true); Back(); });
            Click("LiveButton", delegate { SetPreview(false); Back(); });
            Click("DemoButton", delegate { SetPreview(true); Back(); });
            Click("CopyButton", CopyActivity);
            Click("QuitButton", Quit);
            Click("TopLeftButton", delegate { corner = 0; PlaceCorner(); SavePreferences(); });
            Click("TopRightButton", delegate { corner = 1; PlaceCorner(); SavePreferences(); });
            Click("BottomLeftButton", delegate { corner = 2; PlaceCorner(); SavePreferences(); });
            Click("BottomRightButton", delegate { corner = 3; PlaceCorner(); SavePreferences(); });
            Find<CheckBox>("TopmostCheck").IsChecked = Window.Topmost;
            Find<CheckBox>("MotionCheck").IsChecked = gentleMotion;
            Find<CheckBox>("AttentionCheck").IsChecked = popAttention;
            Find<CheckBox>("TopmostCheck").Click += delegate { Window.Topmost = Find<CheckBox>("TopmostCheck").IsChecked == true; SavePreferences(); };
            Find<CheckBox>("MotionCheck").Click += delegate { gentleMotion = Find<CheckBox>("MotionCheck").IsChecked == true; SavePreferences(); };
            Find<CheckBox>("AttentionCheck").Click += delegate { popAttention = Find<CheckBox>("AttentionCheck").IsChecked == true; SavePreferences(); };
            Find<Grid>("DragHeader").MouseLeftButtonDown += HeaderDrag;
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
                else if ((e.Key == Key.Enter || e.Key == Key.Space) && collapsed) { ToggleCollapsed(); e.Handled = true; }
                else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control) { Quit(); e.Handled = true; }
            };
            Window.Closing += delegate { closing = true; timer.Stop(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); };
            Window.SourceInitialized += delegate { PlaceCorner(); };
            Window.Loaded += delegate
            {
                PlaceCorner();
                if (gentleMotion) { Window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))); }
                Poll();
            };
            trayIcon = MakeIcon();
            Window.Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Orbit · Agent activity", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show Orbit", null, delegate { Window.Dispatcher.BeginInvoke(new Action(ShowWidget)); });
            menu.Items.Add("Collapse / expand", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { Window.Show(); ToggleCollapsed(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Live activity", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { SetPreview(false); ShowWidget(); })); });
            menu.Items.Add("Preview animation", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { SetPreview(true); ShowWidget(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Hide widget", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { Window.Hide(); })); });
            menu.Items.Add("Quit Orbit", null, delegate { Window.Dispatcher.BeginInvoke(new Action(Quit)); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Window.Dispatcher.BeginInvoke(new Action(ShowWidget)); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += delegate
            {
                if (wake.WaitOne(0)) ShowWidget();
                tick++;
                if (tick % 2 == 0) { foreach (var agent in agents) agent.Notify(); tree.Refresh(); if (detailId != null) RefreshDetail(); }
                if (tick % 3 == 0) Poll();
            };
            timer.Start();
            SetModeLabels();
            StyleSelections();
        }
        private void SetModeLabels()
        {
            Find<Button>("SourceButton").Content = preview ? "Go live" : String.IsNullOrEmpty(options.Events) ? "Codex" : "File";
            Find<Button>("LiveButton").Content = String.IsNullOrEmpty(options.Events) ? "Live · Codex" : "Live · File";
            Text("SettingsSource", preview ? "Simulated agents. Choose Live for your workspace." : "Read-only activity from local sessions.");
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
            preview = enabled; generation++; detailId = null; attentionSeen.Clear(); agents.Clear(); tree.Clear();
            SetModeLabels(); Poll();
        }
        private void Poll()
        {
            if (closing) return;
            if (preview) { Apply(demo.Poll()); return; }
            if (polling) return;
            polling = true;
            int version = generation;
            Task.Factory.StartNew(delegate { return reader.Poll(); }).ContinueWith(task =>
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
                    }
                    else Apply(task.Result);
                }));
            });
        }
        private void Apply(Snapshot snapshot)
        {
            current = snapshot;
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
            Text("PillSubtitle", preview ? "Preview · simulated activity" : sessionCount > 1 ? sessionCount + " sessions · " + working + " working" : waiting > 0 ? waiting + " waiting · " + done + " done" : done > 0 ? done + " completed · " + snapshot.Project : "Orbit · " + snapshot.Project);
            Visible("EmptyState", agents.Count == 0);
            Text("EmptyDescription", snapshot.Problem != "" ? snapshot.Problem : "Agents will appear as they start working.");
            tree.Update(agents, !String.IsNullOrEmpty(snapshot.FocusedSession) ? snapshot.FocusedSession : snapshot.Session, snapshot.Project);
            if (detailId != null) RefreshDetail();
            tray.Text = Json.Clean("Orbit · " + (preview ? "Preview · " : "") + title, 63);
        }
        private void OpenDetail(string id)
        {
            Find<Button>("CopyButton").Content = "Copy activity";
            detailId = id; Visible("ExpandedView", false); Visible("SettingsView", false); Visible("DetailView", true); RefreshDetail();
            Find<Button>("BackButton").Focus(); FadeIn(Find<Grid>("DetailView"));
        }
        private void RefreshDetail()
        {
            var agent = agents.FirstOrDefault(a => a.Id == detailId);
            if (agent == null) { Back(); return; }
            Text("DetailName", agent.DisplayName); Text("DetailStatus", agent.StatusLabel); Text("DetailElapsed", agent.Elapsed); Text("DetailTask", agent.Task);
            Find<TextBlock>("DetailStatus").Foreground = agent.AccentBrush;
            var parent = agents.FirstOrDefault(a => a.Id == agent.ParentId);
            Text("DetailParent", agent.IsRoot ? "Coordinating this workspace" : parent != null ? "Spawned by " + parent.Name : String.IsNullOrEmpty(agent.ParentId) ? "Team agent" : "Parent · " + Json.Clean(agent.ParentId, 34));
            Find<ItemsControl>("DetailEvents").ItemsSource = agent.Events.OrderByDescending(e => e.At).ToList();
        }
        private void Back()
        {
            detailId = null; Visible("ExpandedView", !collapsed); Visible("DetailView", false); Visible("SettingsView", false);
            Find<Button>("SettingsButton").Focus();
        }
        private void ShowSettings()
        {
            detailId = null; Visible("ExpandedView", false); Visible("DetailView", false); Visible("SettingsView", true);
            Find<Button>("SettingsBackButton").Focus(); FadeIn(Find<Grid>("SettingsView"));
        }
        private void CopyActivity()
        {
            var agent = agents.FirstOrDefault(a => a.Id == detailId);
            if (agent == null) return;
            var text = agent.Name + " · " + agent.StatusLabel + "\r\n" + agent.Task + "\r\n\r\n" + String.Join("\r\n", agent.Events.Select(e => e.At.ToLocalTime().ToString("HH:mm:ss") + "  " + e.Text));
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
            var screen = Forms.Screen.FromHandle(handle).WorkingArea;
            var source = PresentationSource.FromVisual(Window);
            var matrix = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            return new Rect(matrix.Transform(new Point(screen.Left, screen.Top)), matrix.Transform(new Point(screen.Right, screen.Bottom)));
        }
        private void PlaceCorner()
        {
            if (animating) return;
            var area = WorkArea();
            var x = corner % 2 == 0 ? area.Left + 8 : area.Right - Window.Width - 8;
            var y = corner < 2 ? area.Top + 8 : area.Bottom - Window.Height - 8;
            Window.Left = Math.Max(area.Left, x); Window.Top = Math.Max(area.Top, y);
            StyleSelections();
        }
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
            if (animating) return;
            collapsed = !collapsed; detailId = null;
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
        private void ShowWidget() { Window.Show(); RequestExpand(); Window.Activate(); Back(); }
        private void Quit() { closing = true; Window.Close(); Application.Current.Shutdown(); }
        private void LoadPreferences()
        {
            gentleMotion = SystemParameters.ClientAreaAnimation;
            try
            {
                var path = Path.Combine(options.Workspace, ".orbit", "preferences.json");
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
                var folder = Path.Combine(options.Workspace, ".orbit"); Directory.CreateDirectory(folder);
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
