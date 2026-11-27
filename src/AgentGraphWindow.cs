using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Forms = System.Windows.Forms;

namespace OrbitEvolved
{
    internal sealed class AgentGraphWindow : Window
    {
        private static readonly Geometry ExpandIcon = Geometry.Parse("M 1,5 V 1 H 5 M 7,1 H 11 V 5 M 11,7 V 11 H 7 M 5,11 H 1 V 7");
        private static readonly Geometry RestoreIcon = Geometry.Parse("M 5,1 V 5 H 1 M 7,1 V 5 H 11 M 11,7 H 7 V 11 M 1,7 H 5 V 11");
        private static readonly Geometry CloseIcon = Geometry.Parse("M 2,2 L 10,10 M 10,2 L 2,10");
        private const double CardWidth = 190;
        private const double CardHeight = 91;
        private const double ColumnPitch = 243;
        private const double RowPitch = 110;
        private const double GraphInset = 24;
        private static readonly Brush PanelSurface = Palette.Color("#10FFFFFF");
        private static readonly Brush Board = Palette.Color("#40101418");
        private static readonly Brush CardSurface = Palette.Color("#F02B2D31");
        private static readonly Brush SelectedSurface = Palette.Color("#F037424F");
        private static readonly Brush Primary = Palette.Color("#F4F6FB");
        private static readonly Brush Secondary = Palette.Color("#C6CDD7");
        private static readonly Brush Muted = Palette.Color("#B2BBC8");
        private static readonly Brush Outline = Palette.Color("#23FFFFFF");
        private static readonly Brush SelectedOutline = Palette.Color("#B5A8C8EC");
        private static readonly Brush Attention = Palette.Color("#E5BE89");
        private static readonly Style CardStyle = CreateCardStyle();

        private readonly Dictionary<string, GraphCard> cards = new Dictionary<string, GraphCard>(StringComparer.Ordinal);
        private readonly ScrollViewer graphScroll;
        private readonly ScrollViewer detailScroll;
        private readonly Canvas canvas;
        private readonly ScaleTransform graphScale = new ScaleTransform();
        private readonly GraphEdges edges;
        private readonly Button zoomReset;
        private readonly StackPanel graphTools;
        private readonly TextBlock chatHeading;
        private readonly TextBlock graphCount;
        private readonly TextBlock graphWarning;
        private readonly TextBlock graphEmpty;
        private readonly TextBlock agentName;
        private readonly TextBlock agentStatus;
        private readonly TextBlock agentParent;
        private readonly TextBlock agentChildren;
        private readonly TextBlock agentIssue;
        private readonly TextBlock detailHeading;
        private readonly TextBlock historyHeading;
        private readonly ActivityTextBlock activity;
        private readonly StackPanel history;
        private readonly Button readsToggle;
        private readonly Button openCodex, branchToggle, detailsTab, filesTab;
        private readonly StackPanel detailBody, filesBody;
        private readonly HashSet<string> foldedBranches = new HashSet<string>(StringComparer.Ordinal);
        private List<Agent> allAgents = new List<Agent>();
        private Action openCodexAction;
        private bool showingFiles;
        private string filesFingerprint;
        private readonly Button maximize;
        private readonly Button close;
        private readonly Grid frame;
        private readonly Border glass;
        private readonly TranslateTransform entrance = new TranslateTransform();
        private AgentGraphLayout layout;
        private string fallbackWorkspace;
        private string selectedId;
        private string historyWorkspace;
        private bool showFileReads;
        private bool closingWithMotion;
        private string agentLinksKey;
        private MouseButton? panButton;
        private Point panOrigin, panOffset;
        private List<ActivityEvent> shownEvents = new List<ActivityEvent>();

        internal int NodeCount { get { return cards.Count; } }
        internal int EdgeCount { get { return layout == null ? 0 : layout.EdgeCount; } }
        internal string SelectedAgentId { get { return selectedId; } }
        internal string SelectedActivity { get { return activity.Body; } }
        internal double Zoom { get { return graphScale.ScaleX; } }
        internal int HighlightedEdges { get { return edges.HighlightedEdges; } }
        internal bool GentleMotion { get; set; }
        private bool MotionEnabled { get { return GentleMotion && SystemParameters.ClientAreaAnimation; } }

        public AgentGraphWindow()
        {
            Title = Product.Name + " · Agent workflow";
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 690;
            MinHeight = 440;
            Width = 1160;
            Height = 660;
            Background = Brushes.Transparent;
            Foreground = Primary;
            FontFamily = new FontFamily("Segoe UI");
            GentleMotion = SystemParameters.ClientAreaAnimation;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            Resources[typeof(ScrollBar)] = CreateScrollbarStyle();

            // A barely visible hit surface leaves transparent resize gutters around the glass.
            var root = new Grid { Background = Palette.Color("#01000000") };
            Content = root;
            frame = new Grid { Margin = new Thickness(12), RenderTransform = entrance };
            root.Children.Add(frame);
            glass = new Border {
                Background = new LinearGradientBrush(Color.FromArgb(216, 42, 42, 42), Color.FromArgb(228, 26, 26, 26), 65),
                BorderBrush = Palette.Color("#54FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.3 },
                IsHitTestVisible = false
            };
            frame.Children.Add(glass);
            var view = new DockPanel { LastChildFill = true, Margin = new Thickness(20, 12, 20, 17) };
            frame.Children.Add(view);
            var header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            DockPanel.SetDock(header, Dock.Top);
            view.Children.Add(header);
            header.MouseLeftButtonDown += DragHeader;

            var headings = new StackPanel { Margin = new Thickness(1, 4, 12, 0) };
            headings.Children.Add(Label("Agent workflow", 22, Primary, FontWeights.SemiBold));
            chatHeading = Label("", 12, Secondary, FontWeights.Normal);
            chatHeading.TextTrimming = TextTrimming.CharacterEllipsis;
            chatHeading.Margin = new Thickness(0, 3, 0, 0);
            headings.Children.Add(chatHeading);
            header.Children.Add(headings);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(controls, 1);
            header.Children.Add(controls);
            openCodex = ViewButton("Open in Codex", "Open this chat in Codex Desktop", delegate { if (openCodexAction != null) openCodexAction(); });
            openCodex.Visibility = Visibility.Collapsed;
            controls.Children.Add(openCodex);
            maximize = HeaderButton(ExpandIcon, "Maximize", ToggleMaximized);
            controls.Children.Add(maximize);
            close = HeaderButton(CloseIcon, "Close graph (Esc)", CloseFromControl);
            close.Margin = new Thickness(3, 0, 0, 0);
            controls.Children.Add(close);
            StateChanged += delegate
            {
                var maximized = WindowState == WindowState.Maximized;
                ((System.Windows.Shapes.Path)maximize.Content).Data = maximized ? RestoreIcon : ExpandIcon;
                maximize.ToolTip = maximized ? "Restore window" : "Maximize";
                AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
                frame.Margin = new Thickness(maximized ? 0 : 12);
                glass.CornerRadius = new CornerRadius(maximized ? 0 : 15);
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Escape) return;
                args.Handled = true;
                CloseFromControl();
            };

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 255, MaxWidth = 430 });
            view.Children.Add(columns);

            var graph = Section();
            columns.Children.Add(graph);
            var graphPanel = new DockPanel();
            graph.Child = graphPanel;
            var graphHeader = new DockPanel { Margin = new Thickness(13, 10, 13, 10) };
            DockPanel.SetDock(graphHeader, Dock.Top);
            graphPanel.Children.Add(graphHeader);
            graphHeader.Children.Add(Label("Delegation graph", 12, Primary, FontWeights.SemiBold));
            graphCount = Label("", 11, Secondary, FontWeights.Normal);
            graphCount.HorizontalAlignment = HorizontalAlignment.Right;
            graphCount.VerticalAlignment = VerticalAlignment.Center;
            graphCount.TextTrimming = TextTrimming.CharacterEllipsis;
            graphHeader.Children.Add(graphCount);

            var graphFooter = new DockPanel { Margin = new Thickness(13, 9, 13, 10) };
            DockPanel.SetDock(graphFooter, Dock.Bottom);
            graphPanel.Children.Add(graphFooter);
            graphTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(graphTools, Dock.Right);
            graphFooter.Children.Add(graphTools);
            graphTools.Children.Add(ViewButton("−", "Zoom out", delegate { ZoomBy(1 / 1.2, ViewCenter()); }));
            zoomReset = ViewButton("100%", "Reset zoom to 100%", delegate { SetZoom(1, ViewCenter()); });
            zoomReset.MinWidth = 46;
            graphTools.Children.Add(zoomReset);
            graphTools.Children.Add(ViewButton("+", "Zoom in", delegate { ZoomBy(1.2, ViewCenter()); }));
            graphTools.Children.Add(ViewButton("Fit", "Fit the whole workflow in view", FitGraph));
            var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            graphFooter.Children.Add(legend);
            legend.Children.Add(Label("Parent \u2192 delegated agent", 11, Secondary, FontWeights.Normal));
            graphWarning = Label("", 11, Attention, FontWeights.Normal);
            graphWarning.Margin = new Thickness(0, 3, 0, 0);
            graphWarning.TextTrimming = TextTrimming.CharacterEllipsis;
            legend.Children.Add(graphWarning);

            var viewport = new Grid { Background = Dots() };
            graphPanel.Children.Add(new Border { BorderBrush = Outline, BorderThickness = new Thickness(0, 1, 0, 1), Child = viewport });
            graphScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                PanningMode = PanningMode.Both, Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Focusable = false
            };
            AutomationProperties.SetName(graphScroll, "Agent delegation graph");
            viewport.Children.Add(graphScroll);
            canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LayoutTransform = graphScale };
            KeyboardNavigation.SetDirectionalNavigation(canvas, KeyboardNavigationMode.None);
            graphScroll.Content = canvas;
            edges = new GraphEdges { IsHitTestVisible = false };
            canvas.Children.Add(edges);
            graphEmpty = Label("No agents have been recorded for this chat yet.", 13, Secondary, FontWeights.Normal);
            graphEmpty.HorizontalAlignment = HorizontalAlignment.Center;
            graphEmpty.VerticalAlignment = VerticalAlignment.Center;
            graphEmpty.Margin = new Thickness(24);
            graphEmpty.TextAlignment = TextAlignment.Center;
            graphEmpty.TextWrapping = TextWrapping.Wrap;
            graphEmpty.IsHitTestVisible = false;
            viewport.Children.Add(graphEmpty);
            graphScroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs args)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    ZoomBy(Math.Pow(1.2, args.Delta / 120.0), args.GetPosition(graphScroll));
                    args.Handled = true;
                    return;
                }
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 && graphScroll.ScrollableHeight > 0) return;
                if (graphScroll.ScrollableWidth <= 0) return;
                graphScroll.ScrollToHorizontalOffset(graphScroll.HorizontalOffset - args.Delta / 2.0);
                args.Handled = true;
            };
            graphScroll.PreviewMouseDown += BeginPan;
            graphScroll.PreviewMouseMove += MovePan;
            graphScroll.PreviewMouseUp += EndPan;
            graphScroll.LostMouseCapture += delegate { panButton = null; graphScroll.Cursor = null; };

            var inspector = Section();
            Grid.SetColumn(inspector, 2);
            columns.Children.Add(inspector);
            var details = new DockPanel();
            inspector.Child = details;
            var inspectorHeader = new Border { BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 10, 14, 10) };
            var inspectorTabs = new StackPanel { Orientation = Orientation.Horizontal };
            detailsTab = ViewButton("Agent details", "Show selected agent details", delegate { SetInspector(false); });
            filesTab = ViewButton("Files changed", "Show files changed in this chat", ShowFiles);
            inspectorTabs.Children.Add(detailsTab);
            inspectorTabs.Children.Add(filesTab);
            inspectorHeader.Child = inspectorTabs;
            DockPanel.SetDock(inspectorHeader, Dock.Top);
            details.Children.Add(inspectorHeader);
            detailScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly
            };
            AutomationProperties.SetName(detailScroll, "Selected agent activity");
            details.Children.Add(detailScroll);
            var inspectorBody = new Grid { Margin = new Thickness(14, 11, 14, 15) };
            detailBody = new StackPanel();
            filesBody = new StackPanel { Visibility = Visibility.Collapsed };
            inspectorBody.Children.Add(detailBody);
            inspectorBody.Children.Add(filesBody);
            detailScroll.Content = inspectorBody;

            agentName = Label("", 19, Primary, FontWeights.SemiBold);
            agentName.TextWrapping = TextWrapping.Wrap;
            detailBody.Children.Add(agentName);
            agentStatus = Label("", 12, Secondary, FontWeights.SemiBold);
            agentStatus.Margin = new Thickness(0, 4, 0, 0);
            detailBody.Children.Add(agentStatus);
            agentParent = Label("", 12, Muted, FontWeights.Normal);
            agentParent.Margin = new Thickness(0, 7, 0, 0);
            agentParent.TextWrapping = TextWrapping.Wrap;
            detailBody.Children.Add(agentParent);
            agentChildren = Label("", 12, Muted, FontWeights.Normal);
            agentChildren.Margin = new Thickness(0, 5, 0, 0);
            agentChildren.TextWrapping = TextWrapping.Wrap;
            detailBody.Children.Add(agentChildren);
            agentIssue = Label("", 12, Attention, FontWeights.Normal);
            agentIssue.Margin = new Thickness(0, 7, 0, 0);
            agentIssue.TextWrapping = TextWrapping.Wrap;
            detailBody.Children.Add(agentIssue);
            branchToggle = ViewButton("Collapse branch", "Collapse this completed branch", ToggleSelectedBranch);
            branchToggle.Margin = new Thickness(0, 6, 0, 0);
            branchToggle.HorizontalAlignment = HorizontalAlignment.Left;
            detailBody.Children.Add(branchToggle);

            detailHeading = Label("", 12, Primary, FontWeights.SemiBold);
            detailHeading.Margin = new Thickness(0, 17, 0, 6);
            detailBody.Children.Add(detailHeading);
            activity = ActivityBlock();
            detailBody.Children.Add(activity);
            var historyHeader = new DockPanel { Margin = new Thickness(0, 16, 0, 1) };
            readsToggle = new Button
            {
                Style = CardStyle, Background = PanelSurface, BorderBrush = Outline, Foreground = Secondary,
                Cursor = Cursors.Hand, FontSize = 11, Padding = new Thickness(6, 3, 6, 3)
            };
            readsToggle.Click += delegate
            {
                showFileReads = !showFileReads;
                RefreshInspector();
            };
            DockPanel.SetDock(readsToggle, Dock.Right);
            historyHeader.Children.Add(readsToggle);
            historyHeading = Label("Earlier activity", 12, Primary, FontWeights.SemiBold);
            historyHeading.VerticalAlignment = VerticalAlignment.Center;
            historyHeader.Children.Add(historyHeading);
            detailBody.Children.Add(historyHeader);
            history = new StackPanel();
            detailBody.Children.Add(history);

            SetInspector(false);
            Update("", Enumerable.Empty<Agent>(), "");
            SourceInitialized += delegate { CenterOnMonitor(); };
            Loaded += delegate
            {
                ApplyWidgetStyle();
                if (MotionEnabled)
                {
                    BeginAnimation(OpacityProperty, Motion(0, 1, 280));
                    entrance.BeginAnimation(TranslateTransform.YProperty, Motion(8, 0, 280));
                }
                GraphCard first;
                if (selectedId != null && cards.TryGetValue(selectedId, out first)) first.Focus();
            };
        }

        private void ApplyWidgetStyle()
        {
            if (Owner == null) return;
            var surface = Owner.FindName("Surface") as Border;
            if (surface != null)
            {
                glass.Background = surface.Background;
                glass.BorderBrush = surface.BorderBrush;
                glass.Effect = surface.Effect;
            }
            var icons = Owner.TryFindResource("IconButton") as Style;
            if (icons != null) { maximize.Style = icons; close.Style = icons; }
            var buttons = Owner.TryFindResource("TextButton") as Style;
            if (buttons != null)
            {
                readsToggle.Style = buttons;
                foreach (var button in graphTools.Children.OfType<Button>()) button.Style = buttons;
            }
            var tooltip = Owner.TryFindResource(typeof(ToolTip)) as Style;
            if (tooltip != null) Resources[typeof(ToolTip)] = tooltip;
        }

        private void CenterOnMonitor()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var ownerHandle = Owner == null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
            var monitor = ownerHandle == IntPtr.Zero ? Forms.Screen.FromPoint(Forms.Cursor.Position) : Forms.Screen.FromHandle(ownerHandle);
            var area = monitor.WorkingArea;
            var source = PresentationSource.FromVisual(ownerHandle == IntPtr.Zero ? this : Owner);
            var scale = source == null || source.CompositionTarget == null ? Matrix.Identity : source.CompositionTarget.TransformToDevice;
            var availableWidth = Math.Max(1, area.Width / scale.M11 - 24);
            var availableHeight = Math.Max(1, area.Height / scale.M22 - 24);
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
            var width = (int)Math.Round(Width * scale.M11);
            var height = (int)Math.Round(Height * scale.M22);
            // Native coordinates keep the center correct on secondary monitors with a different DPI or origin.
            SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2,
                width, height, 0x0014); // SWP_NOACTIVATE | SWP_NOZORDER
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private static DoubleAnimation Motion(double from, double to, int milliseconds)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }

        private void CloseFromControl()
        {
            if (closingWithMotion) return;
            if (!MotionEnabled) { Close(); return; }
            closingWithMotion = true;
            var fade = Motion(Opacity, 0, 160);
            fade.Completed += delegate { if (IsVisible) Close(); };
            BeginAnimation(OpacityProperty, fade);
            if (WindowState == WindowState.Normal) entrance.BeginAnimation(TranslateTransform.YProperty, Motion(0, 5, 160));
        }

        private Point ViewCenter() { return new Point(graphScroll.ViewportWidth / 2, graphScroll.ViewportHeight / 2); }

        private void ZoomBy(double factor, Point anchor) { SetZoom(Zoom * factor, anchor); }

        internal void FitGraph()
        {
            if (canvas.Width <= 0 || canvas.Height <= 0 || graphScroll.ViewportWidth <= 0 || graphScroll.ViewportHeight <= 0) return;
            var fit = Math.Min((graphScroll.ViewportWidth - 8) / canvas.Width, (graphScroll.ViewportHeight - 8) / canvas.Height);
            SetZoom(Math.Min(1, fit), ViewCenter());
            graphScroll.ScrollToHorizontalOffset(0);
            graphScroll.ScrollToVerticalOffset(0);
        }

        internal void SetZoom(double scale, Point anchor)
        {
            if (canvas.Width <= 0 || canvas.Height <= 0) return;
            scale = Math.Max(0.005, Math.Min(2.25, scale));
            if (Math.Abs(Zoom - scale) < 0.00001) return;
            var oldWidth = graphScroll.ViewportWidth;
            var oldHeight = graphScroll.ViewportHeight;
            var x = (graphScroll.HorizontalOffset + anchor.X - CenterInset(oldWidth, canvas.Width * Zoom)) / Zoom;
            var y = (graphScroll.VerticalOffset + anchor.Y - CenterInset(oldHeight, canvas.Height * Zoom)) / Zoom;
            graphScale.ScaleX = graphScale.ScaleY = scale;
            var percentage = Math.Max(1, (int)Math.Round(scale * 100)) + "%";
            zoomReset.Content = percentage;
            AutomationProperties.SetName(zoomReset, "Reset zoom to 100%. Current zoom " + percentage);
            graphScroll.UpdateLayout();
            graphScroll.ScrollToHorizontalOffset(x * scale + CenterInset(graphScroll.ViewportWidth, canvas.Width * scale) - anchor.X);
            graphScroll.ScrollToVerticalOffset(y * scale + CenterInset(graphScroll.ViewportHeight, canvas.Height * scale) - anchor.Y);
        }

        private static double CenterInset(double viewport, double content) { return Math.Max(0, (viewport - content) / 2); }

        private void BeginPan(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton != MouseButton.Left && args.ChangedButton != MouseButton.Middle) return;
            if (graphScroll.ScrollableWidth <= 0 && graphScroll.ScrollableHeight <= 0) return;
            for (var source = args.OriginalSource as DependencyObject; source != null && source != graphScroll;)
            {
                if (source is ScrollBar || (source is ButtonBase && args.ChangedButton == MouseButton.Left)) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            if (!graphScroll.CaptureMouse()) return;
            panButton = args.ChangedButton;
            panOrigin = args.GetPosition(graphScroll);
            panOffset = new Point(graphScroll.HorizontalOffset, graphScroll.VerticalOffset);
            graphScroll.Cursor = Cursors.SizeAll;
            args.Handled = true;
        }

        private void MovePan(object sender, MouseEventArgs args)
        {
            if (!panButton.HasValue) return;
            var delta = args.GetPosition(graphScroll) - panOrigin;
            graphScroll.ScrollToHorizontalOffset(panOffset.X - delta.X);
            graphScroll.ScrollToVerticalOffset(panOffset.Y - delta.Y);
            args.Handled = true;
        }

        private void EndPan(object sender, MouseButtonEventArgs args)
        {
            if (panButton != args.ChangedButton) return;
            graphScroll.ReleaseMouseCapture();
            args.Handled = true;
        }

        public void Update(string chatTitle, IEnumerable<Agent> agents, string fallbackWorkspace)
        {
            allAgents = AgentGraphLayout.OrderedAgents(agents ?? Enumerable.Empty<Agent>());
            this.fallbackWorkspace = fallbackWorkspace ?? "";
            chatHeading.Text = chatTitle ?? "";
            chatHeading.ToolTip = chatHeading.Text;
            Title = Product.Name + " · " + (chatHeading.Text == "" ? "Agent workflow" : chatHeading.Text);

            RefreshGraph();
        }

        private void RefreshGraph()
        {
            var next = AgentGraphLayout.FromOrdered(allAgents, foldedBranches, selectedId);
            foreach (var node in next.Nodes)
                if (!node.CanCollapse) foldedBranches.Remove(node.Agent.Id);
            if (layout == null || next.Nodes.Count != layout.Nodes.Count ||
                next.Nodes.Where((node, index) => !SamePlacement(node, layout.Nodes[index])).Any())
                ArrangeGraph(next);
            foreach (var node in next.Nodes) cards[node.Agent.Id].Refresh(node.Agent);
            SelectAgent(selectedId != null && cards.ContainsKey(selectedId) ? selectedId :
                layout.Nodes.Count == 0 ? null : layout.Nodes[0].Agent.Id);
            if (showingFiles) RefreshFiles();
        }

        private static bool SamePlacement(AgentGraphNode left, AgentGraphNode right)
        {
            return left.Agent.Id == right.Agent.Id && left.ParentId == right.ParentId && left.Issue == right.Issue &&
                left.Depth == right.Depth && left.Row == right.Row && left.CanCollapse == right.CanCollapse &&
                left.FoldedCount == right.FoldedCount;
        }

        private void ToggleSelectedBranch()
        {
            GraphCard card;
            if (selectedId == null || !cards.TryGetValue(selectedId, out card) || !card.Node.CanCollapse) return;
            if (!foldedBranches.Remove(selectedId)) foldedBranches.Add(selectedId);
            RefreshGraph();
        }

        internal void SetOpenCodex(Action action)
        {
            openCodexAction = action;
            openCodex.Visibility = VisibleIf(action != null);
        }

        internal void ShowFiles() { SetInspector(true); }

        private void SetInspector(bool files)
        {
            var changed = showingFiles != files;
            showingFiles = files;
            detailBody.Visibility = VisibleIf(!files);
            filesBody.Visibility = VisibleIf(files);
            detailsTab.Background = files ? Brushes.Transparent : PanelSurface;
            filesTab.Background = files ? PanelSurface : Brushes.Transparent;
            AutomationProperties.SetName(detailScroll, files ? "Files changed in this chat" : "Selected agent activity");
            if (changed) detailScroll.ScrollToTop();
            if (files) RefreshFiles();
        }

        private void RefreshFiles()
        {
            var groups = allAgents.Where(agent => agent.ChangedFiles.Count != 0).Select(agent => new {
                Agent = agent,
                Paths = agent.ChangedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            }).ToList();
            var fingerprint = String.Join("\0", groups.SelectMany(group =>
                new[] { group.Agent.Id, group.Agent.DisplayName, group.Agent.Workspace ?? "" }.Concat(group.Paths)));
            if (fingerprint == filesFingerprint) return;
            filesFingerprint = fingerprint;
            filesBody.Children.Clear();
            if (groups.Count == 0)
            {
                filesBody.Children.Add(Label("No file edits recorded for this chat.", 12, Secondary, FontWeights.Normal));
                return;
            }
            foreach (var group in groups)
            {
                var heading = Label(group.Agent.DisplayName, 13, Primary, FontWeights.SemiBold);
                heading.Margin = new Thickness(0, filesBody.Children.Count == 0 ? 0 : 12, 0, 5);
                filesBody.Children.Add(heading);
                foreach (var path in group.Paths)
                {
                    var file = new ActivityFileLink { Path = path };
                    var workspace = group.Agent.Workspace ?? "";
                    var prefix = workspace.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var label = workspace != "" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? path.Substring(prefix.Length) : path;
                    var row = Label("", 12, Secondary, FontWeights.Normal);
                    row.TextWrapping = TextWrapping.Wrap;
                    row.Margin = new Thickness(0, 0, 0, 4);
                    row.Inlines.Add(ActivityLinks.FileHyperlink(file, label));
                    filesBody.Children.Add(row);
                }
            }
        }

        internal bool SelectAgent(string id)
        {
            if (id != null && !cards.ContainsKey(id)) return false;
            if (id != selectedId)
            {
                GraphCard previous;
                if (selectedId != null && cards.TryGetValue(selectedId, out previous)) previous.SetSelected(false);
                selectedId = id;
                showFileReads = false;
                if (selectedId != null) cards[selectedId].SetSelected(true);
                HighlightBranch();
                if (!showingFiles)
                {
                    detailScroll.ScrollToTop();
                    if (IsLoaded && MotionEnabled) detailScroll.BeginAnimation(OpacityProperty, Motion(0.45, 1, 190));
                }
            }
            RefreshInspector();
            return true;
        }

        private void ArrangeGraph(AgentGraphLayout next)
        {
            var present = new HashSet<string>(next.Nodes.Select(node => node.Agent.Id), StringComparer.Ordinal);
            foreach (var removed in cards.Keys.Where(id => !present.Contains(id)).ToList())
            {
                canvas.Children.Remove(cards[removed]);
                cards.Remove(removed);
            }
            layout = next;
            for (int index = 0; index < next.Nodes.Count; index++)
            {
                var node = next.Nodes[index];
                GraphCard card;
                var id = node.Agent.Id;
                if (!cards.TryGetValue(id, out card))
                {
                    card = new GraphCard();
                    card.Click += delegate { SetInspector(false); SelectAgent(id); };
                    card.PreviewKeyDown += CardKeyDown;
                    cards.Add(id, card);
                    canvas.Children.Add(card);
                }
                card.SetNode(node, index);
                Canvas.SetLeft(card, NodeX(node));
                Canvas.SetTop(card, NodeY(node));
            }
            canvas.Width = next.ColumnCount == 0 ? 0 : 2 * GraphInset + (next.ColumnCount - 1) * ColumnPitch + CardWidth;
            canvas.Height = next.RowCount == 0 ? 0 : 2 * GraphInset + (next.RowCount - 1) * RowPitch + CardHeight;
            edges.Width = canvas.Width;
            edges.Height = canvas.Height;
            edges.SetLayout(next);
            HighlightBranch();
            graphEmpty.Visibility = next.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            graphTools.IsEnabled = next.Nodes.Count > 0;
            graphCount.Text = Count(next.Nodes.Count, "agent") + " \u00b7 " + Count(next.EdgeCount, "delegation");
            var issues = next.Nodes.Count(node => node.Issue != AgentGraphIssue.None);
            graphWarning.Text = issues == 0 ? "" : Count(issues, "unavailable parent link");
            graphWarning.Visibility = VisibleIf(issues > 0);
            graphWarning.ToolTip = issues == 0 ? null : "Select an affected agent for details about its recorded parent.";
        }

        private void HighlightBranch()
        {
            GraphCard selected;
            if (selectedId == null || !cards.TryGetValue(selectedId, out selected))
            {
                foreach (var card in cards.Values) card.Opacity = 1;
                edges.HighlightBranch(layout, null);
                return;
            }
            var focused = new HashSet<string>(StringComparer.Ordinal) { selectedId };
            var links = new HashSet<string>(StringComparer.Ordinal);
            var node = selected.Node;
            while (node.ParentId != null)
            {
                links.Add(node.Agent.Id);
                node = cards[node.ParentId].Node;
                focused.Add(node.Agent.Id);
            }
            var pending = new Stack<AgentGraphNode>(selected.Node.Children);
            while (pending.Count != 0)
            {
                node = pending.Pop();
                links.Add(node.Agent.Id);
                focused.Add(node.Agent.Id);
                foreach (var child in node.Children) pending.Push(child);
            }
            foreach (var card in cards.Values) card.Opacity = focused.Contains(card.Node.Agent.Id) ? 1 : 0.58;
            edges.HighlightBranch(layout, links);
        }

        private void RefreshInspector()
        {
            GraphCard card;
            if (selectedId == null || !cards.TryGetValue(selectedId, out card))
            {
                agentName.Text = "No agent selected";
                agentStatus.Visibility = agentParent.Visibility = agentChildren.Visibility = agentIssue.Visibility = branchToggle.Visibility = Visibility.Collapsed;
                agentLinksKey = null;
                detailHeading.Visibility = activity.Visibility = Visibility.Collapsed;
                historyHeading.Visibility = readsToggle.Visibility = Visibility.Collapsed;
                activity.Body = "";
                RenderHistory(new List<ActivityEvent>(), "");
                return;
            }
            var agent = card.Agent;
            agentName.Text = agent.DisplayName;
            agentStatus.Text = agent.StatusLabel;
            agentStatus.Foreground = card.StatusBrush;
            agentStatus.Visibility = Visibility.Visible;
            RefreshAgentLinks(card.Node);
            agentIssue.Text = IssueText(card.Node);
            agentIssue.Visibility = VisibleIf(agentIssue.Text != "");
            branchToggle.Visibility = VisibleIf(card.Node.CanCollapse);
            var branchAction = card.Node.FoldedCount == 0 ? "Collapse branch" : "Expand branch";
            branchToggle.Content = branchAction;
            branchToggle.ToolTip = branchAction;
            AutomationProperties.SetName(branchToggle, branchAction);
            var workspace = String.IsNullOrEmpty(agent.Workspace) ? fallbackWorkspace : agent.Workspace;
            var hasResult = agent.State == "completed" && !String.IsNullOrWhiteSpace(agent.Result);
            var summary = agent.ActivitySummary;
            var body = agent.ActivityDetail;
            var earlier = agent.Events.Where(entry => !entry.IsLifecycle).OrderByDescending(entry => entry.At).ToList();
            var current = summary == "" ? -1 : earlier.FindIndex(entry => entry.Body == body || entry.Text == summary || Json.Clean(entry.Text, 180) == summary);
            if (current >= 0) earlier.RemoveAt(current);
            detailHeading.Text = hasResult ? "Public result" : "Latest activity";
            detailHeading.Visibility = activity.Visibility = Visibility.Visible;
            activity.Workspace = workspace;
            activity.Body = String.IsNullOrWhiteSpace(body) ? "No public activity recorded yet." : body;
            var reads = earlier.Count(entry => entry.FileRead);
            historyHeading.Visibility = VisibleIf(earlier.Count > 0);
            readsToggle.Visibility = VisibleIf(reads > 0);
            readsToggle.Content = showFileReads ? "Hide file reads" : "Show file reads (" + reads + ")";
            if (!showFileReads) earlier.RemoveAll(entry => entry.FileRead);
            RenderHistory(earlier, workspace);
        }

        private void RenderHistory(List<ActivityEvent> next, string workspace)
        {
            if (workspace == historyWorkspace && next.Count == shownEvents.Count &&
                Enumerable.Range(0, next.Count).All(index => next[index].At == shownEvents[index].At && next[index].Body == shownEvents[index].Body)) return;

            history.Children.Clear();
            shownEvents = next;
            historyWorkspace = workspace;
            foreach (var entry in next)
            {
                var time = Label(entry.At.ToLocalTime().ToString("HH:mm:ss"), 10, Muted, FontWeights.Normal);
                time.Margin = new Thickness(0, 0, 0, 3);
                var body = ActivityBlock();
                body.Workspace = workspace;
                body.Body = entry.Body;
                var row = new StackPanel();
                row.Children.Add(time);
                row.Children.Add(body);
                history.Children.Add(new Border
                {
                    BorderBrush = Outline, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 8, 0, 8), Child = row
                });
            }
        }

        private void RefreshAgentLinks(AgentGraphNode node)
        {
            var parent = node.ParentId == null ? null : cards[node.ParentId].Agent;
            var children = node.Children.Select(child => cards[child.Agent.Id].Agent).ToList();
            var prefix = parent != null ? "Delegated by " : node.Agent.IsRoot && node.Issue == AgentGraphIssue.None ? "Lead agent" : "";
            var key = prefix + "\0" + (parent == null ? "" : parent.Id + "\0" + parent.DisplayName) + "\0" +
                String.Join("\0", children.Select(child => child.Id + "\0" + child.DisplayName));
            if (key == agentLinksKey) return;
            agentLinksKey = key;
            agentParent.Inlines.Clear();
            if (prefix != "") agentParent.Inlines.Add(new Run(prefix));
            if (parent != null) AddAgentLink(agentParent, parent);
            agentParent.Visibility = VisibleIf(prefix != "");
            agentChildren.Inlines.Clear();
            if (children.Count > 0)
            {
                agentChildren.Inlines.Add(new Run("Delegated to "));
                for (int index = 0; index < children.Count; index++)
                {
                    if (index > 0) agentChildren.Inlines.Add(new Run(", "));
                    AddAgentLink(agentChildren, children[index]);
                }
            }
            agentChildren.Visibility = VisibleIf(children.Count > 0);
        }

        private void AddAgentLink(TextBlock block, Agent agent)
        {
            var link = new Hyperlink(new Run(agent.DisplayName)) { Foreground = SelectedOutline, ToolTip = "Focus " + agent.DisplayName };
            link.Click += delegate { FocusAgent(agent.Id); };
            block.Inlines.Add(link);
        }

        private void FocusAgent(string id)
        {
            if (!SelectAgent(id)) return;
            SetInspector(false);
            cards[id].Focus();
            cards[id].BringIntoView();
        }

        private void CardKeyDown(object sender, KeyEventArgs args)
        {
            var node = ((GraphCard)sender).Node;
            AgentGraphNode target;
            switch (args.Key)
            {
                case Key.Left: target = node.ParentId == null ? null : cards[node.ParentId].Node; break;
                case Key.Right: target = node.Children.FirstOrDefault(); break;
                case Key.Up: target = layout.Nodes.Where(item => item.Depth == node.Depth && item.Row < node.Row).OrderByDescending(item => item.Row).FirstOrDefault(); break;
                case Key.Down: target = layout.Nodes.Where(item => item.Depth == node.Depth && item.Row > node.Row).OrderBy(item => item.Row).FirstOrDefault(); break;
                default: return;
            }
            args.Handled = true;
            if (target == null) return;
            FocusAgent(target.Agent.Id);
        }

        private static double NodeX(AgentGraphNode node) { return GraphInset + node.Depth * ColumnPitch; }
        private static double NodeY(AgentGraphNode node) { return GraphInset + node.Row * RowPitch; }
        private static string Count(int count, string singular) { return count + " " + singular + (count == 1 ? "" : "s"); }
        private static Visibility VisibleIf(bool show) { return show ? Visibility.Visible : Visibility.Collapsed; }
        private static string IssueText(AgentGraphNode node)
        {
            if (node.Issue == AgentGraphIssue.MissingParent)
                return node.Agent.RecordedParentId == "" ? "The activity has no recorded delegation parent." : "The recorded parent is unavailable in this chat.";
            if (node.Issue == AgentGraphIssue.Cycle) return "The recorded parent link forms a cycle.";
            return "";
        }

        private void DragHeader(object sender, MouseButtonEventArgs args)
        {
            var control = args.OriginalSource as DependencyObject;
            while (control != null && control != sender)
            {
                if (control is Button) return;
                control = VisualTreeHelper.GetParent(control);
            }
            if (args.ClickCount == 2) ToggleMaximized();
            else if (WindowState == WindowState.Normal)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }
        private void ToggleMaximized() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

        private static TextBlock Label(string text, double size, Brush foreground, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = foreground, FontWeight = weight };
        }

        private static ActivityTextBlock ActivityBlock()
        {
            return new ActivityTextBlock { Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        }

        private static Border Section()
        {
            return new Border { Background = PanelSurface, BorderBrush = Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11) };
        }

        private static Button HeaderButton(Geometry geometry, string name, Action click)
        {
            var icon = new System.Windows.Shapes.Path {
                Data = geometry, Width = 12, Height = 12, StrokeThickness = 1.25,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None
            };
            var button = new Button
            {
                Content = icon, Width = 26, Height = 26, Style = CardStyle, Cursor = Cursors.Hand, ToolTip = name,
                BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            AutomationProperties.SetName(button, name);
            button.Click += delegate { click(); };
            return button;
        }

        private static Button ViewButton(string text, string description, Action action)
        {
            var button = new Button {
                Content = text, ToolTip = description, Style = CardStyle, Foreground = Secondary,
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(6, 0, 6, 0),
                MinWidth = 25, Height = 25, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center
            };
            AutomationProperties.SetName(button, description);
            button.Click += delegate { action(); };
            return button;
        }

        private static DrawingBrush Dots()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Board, null, new RectangleGeometry(new Rect(0, 0, 18, 18))));
            group.Children.Add(new GeometryDrawing(Palette.Color("#18FFFFFF"), null, new EllipseGeometry(new Point(9, 9), 0.7, 0.7)));
            var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 18, 18), ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze();
            return brush;
        }

        private static Style CreateCardStyle()
        {
            return (Style)XamlReader.Parse(@"<Style TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
              <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
              <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='Button'>
                  <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='7' Padding='{TemplateBinding Padding}'>
                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#88FFFFFF'/></Trigger>
                    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#E1FFFFFF'/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>");
        }

        private static Style CreateScrollbarStyle()
        {
            return (Style)XamlReader.Parse(@"<Style TargetType='ScrollBar' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
              <Setter Property='Width' Value='11'/><Setter Property='Height' Value='Auto'/>
              <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
                <Border Background='Transparent' Padding='2'>
                  <Track x:Name='PART_Track' IsDirectionReversed='True'>
                    <Track.DecreaseRepeatButton><RepeatButton x:Name='Before' Opacity='0' Focusable='False' Command='ScrollBar.PageUpCommand'/></Track.DecreaseRepeatButton>
                    <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='#58FFFFFF' CornerRadius='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton x:Name='After' Opacity='0' Focusable='False' Command='ScrollBar.PageDownCommand'/></Track.IncreaseRepeatButton>
                  </Track>
                </Border>
                <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'>
                  <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
                  <Setter TargetName='Before' Property='Command' Value='ScrollBar.PageLeftCommand'/>
                  <Setter TargetName='After' Property='Command' Value='ScrollBar.PageRightCommand'/>
                </Trigger></ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
              <Style.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='11'/></Trigger></Style.Triggers>
            </Style>");
        }

        private sealed class GraphCard : Button
        {
            private readonly TextBlock name;
            private readonly TextBlock status;
            private readonly TextBlock summary;
            private readonly TextBlock issue;
            private readonly TextBlock glyph;
            private string previousState;
            private int previousFoldedCount;
            private string previousDetail;
            public AgentGraphNode Node { get; private set; }
            public Agent Agent { get; private set; }
            public Brush StatusBrush { get { return status.Foreground; } }

            public GraphCard()
            {
                Style = CardStyle;
                Width = CardWidth;
                Height = CardHeight;
                Padding = new Thickness(11, 8, 11, 8);
                HorizontalContentAlignment = HorizontalAlignment.Stretch;
                Cursor = Cursors.Hand;
                SetSelected(false);
                var body = new StackPanel();
                Content = body;
                var cardTitle = new DockPanel();
                body.Children.Add(cardTitle);
                issue = Label("", 11, Attention, FontWeights.SemiBold);
                issue.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(issue, Dock.Right);
                cardTitle.Children.Add(issue);
                glyph = Label("", 12, Secondary, FontWeights.Normal);
                glyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
                glyph.Margin = new Thickness(0, 3, 7, 0);
                cardTitle.Children.Add(glyph);
                name = Label("", 14, Primary, FontWeights.SemiBold);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                cardTitle.Children.Add(name);
                status = Label("", 11, Secondary, FontWeights.SemiBold);
                status.Margin = new Thickness(0, 3, 0, 0);
                body.Children.Add(status);
                summary = Label("", 11, Muted, FontWeights.Normal);
                summary.Margin = new Thickness(0, 6, 0, 0);
                summary.TextWrapping = TextWrapping.Wrap;
                summary.TextTrimming = TextTrimming.CharacterEllipsis;
                summary.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                summary.LineHeight = 14;
                summary.Height = 28;
                body.Children.Add(summary);
            }

            public void SetNode(AgentGraphNode node, int index)
            {
                Node = node;
                TabIndex = index;
                issue.Text = node.Issue == AgentGraphIssue.None ? "" : "!";
            }

            public void Refresh(Agent agent)
            {
                Agent = agent;
                name.Text = agent.DisplayName;
                glyph.Text = agent.Glyph;
                var state = agent.State ?? "";
                if (previousState != state || previousFoldedCount != Node.FoldedCount)
                {
                    status.Text = Node.FoldedCount > 0 ? Node.FoldedCount + " done" : agent.StatusLabel;
                    status.Foreground = glyph.Foreground = agent.AccentBrush;
                    previousState = state;
                    previousFoldedCount = Node.FoldedCount;
                }
                var detail = agent.ActivityDetail;
                if (previousDetail != detail)
                {
                    var plain = ActivityLinks.PlainSummary(detail);
                    summary.Text = plain == "" ? "No activity yet" : plain;
                    previousDetail = detail;
                }
                var warning = IssueText(Node);
                var caption = name.Text + " \u00b7 " + status.Text + " \u00b7 " + summary.Text;
                if (Node.FoldedCount > 0) caption += " \u00b7 Collapsed branch";
                if (warning != "") caption += " \u00b7 " + warning;
                ToolTip = caption;
                AutomationProperties.SetName(this, caption);
            }

            public void SetSelected(bool selected)
            {
                Background = selected ? SelectedSurface : CardSurface;
                BorderBrush = selected ? SelectedOutline : Outline;
                AutomationProperties.SetItemStatus(this, selected ? "Selected" : "");
            }
        }

        private sealed class GraphEdges : FrameworkElement
        {
            private static readonly Brush Arrow = Palette.Color("#71849C");
            private static readonly Pen Stroke = new Pen(Arrow, 1.4);
            private static readonly Pen FocusStroke = new Pen(SelectedOutline, 1.65);
            private Geometry lines;
            private Geometry heads;
            private Geometry focusedLines;
            private Geometry focusedHeads;
            internal int HighlightedEdges { get; private set; }

            public void SetLayout(AgentGraphLayout layout)
            {
                BuildPaths(layout, null, out lines, out heads);
                InvalidateVisual();
            }

            public void HighlightBranch(AgentGraphLayout layout, HashSet<string> childIds)
            {
                HighlightedEdges = childIds == null ? 0 : childIds.Count;
                focusedLines = focusedHeads = null;
                if (childIds != null) BuildPaths(layout, childIds, out focusedLines, out focusedHeads);
                InvalidateVisual();
            }

            private static void BuildPaths(AgentGraphLayout layout, HashSet<string> childIds, out Geometry lines, out Geometry heads)
            {
                var connectors = new StreamGeometry();
                var arrowheads = new StreamGeometry();
                using (var path = connectors.Open())
                using (var arrows = arrowheads.Open())
                {
                    foreach (var parent in layout.Nodes.Where(node => node.Children.Count > 0))
                    {
                        var children = childIds == null ? parent.Children : parent.Children.Where(child => childIds.Contains(child.Agent.Id)).ToList();
                        if (children.Count == 0) continue;
                        var startX = NodeX(parent) + CardWidth;
                        var startY = NodeY(parent) + CardHeight / 2;
                        var trunkX = startX + (ColumnPitch - CardWidth) / 2;
                        var firstY = Math.Min(startY, NodeY(children[0]) + CardHeight / 2);
                        var lastY = Math.Max(startY, NodeY(children[children.Count - 1]) + CardHeight / 2);
                        path.BeginFigure(new Point(startX, startY), false, false);
                        path.LineTo(new Point(trunkX, startY), true, false);
                        if (firstY != lastY)
                        {
                            path.BeginFigure(new Point(trunkX, firstY), false, false);
                            path.LineTo(new Point(trunkX, lastY), true, false);
                        }
                        foreach (var child in children)
                        {
                            var x = NodeX(child) - 1;
                            var y = NodeY(child) + CardHeight / 2;
                            path.BeginFigure(new Point(trunkX, y), false, false);
                            path.LineTo(new Point(x - 4, y), true, false);
                            arrows.BeginFigure(new Point(x, y), true, true);
                            arrows.LineTo(new Point(x - 7, y - 4), true, false);
                            arrows.LineTo(new Point(x - 7, y + 4), true, false);
                        }
                    }
                }
                connectors.Freeze();
                arrowheads.Freeze();
                lines = connectors;
                heads = arrowheads;
            }

            protected override void OnRender(DrawingContext drawing)
            {
                if (lines == null) return;
                if (focusedLines != null) drawing.PushOpacity(0.42);
                drawing.DrawGeometry(null, Stroke, lines);
                drawing.DrawGeometry(Arrow, null, heads);
                if (focusedLines == null) return;
                drawing.Pop();
                drawing.DrawGeometry(null, FocusStroke, focusedLines);
                drawing.DrawGeometry(SelectedOutline, null, focusedHeads);
            }
        }
    }
}
