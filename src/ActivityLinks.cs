using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OrbitEvolved
{
    internal sealed class ActivityFileLink
    {
        public string Path;
        public int Line;
        public string Reference { get { return Line == 0 ? Path : Path + ":" + Line; } }
    }

    public sealed class ActivityTextBlock : TextBlock
    {
        public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
            "Body", typeof(string), typeof(ActivityTextBlock), new PropertyMetadata("", RenderChanged));
        public static readonly DependencyProperty WorkspaceProperty = DependencyProperty.Register(
            "Workspace", typeof(string), typeof(ActivityTextBlock), new PropertyMetadata("", RenderChanged));

        public string Body { get { return (string)GetValue(BodyProperty); } set { SetValue(BodyProperty, value); } }
        public string Workspace { get { return (string)GetValue(WorkspaceProperty); } set { SetValue(WorkspaceProperty, value); } }

        private static void RenderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var block = (ActivityTextBlock)sender;
            block.Inlines.Clear();
            ActivityMarkdown.Render(block, block.Body ?? "", block.Workspace ?? "");
        }
    }

    internal static class ActivityLinks
    {
        private static readonly Regex Inline = new Regex(
            @"\\(?<escape>[\\\x60*_{}\[\]()#+\-.!|>~])|\[(?<label>[^\]\r\n]+)\]\((?<target><[^>\r\n]+>|(?:[^()\r\n]|\([^()\r\n]*\))+)\)|(?<ticks>\x60+)(?<code>[^\r\n]+?)\k<ticks>(?!\x60)|(?<boldMarker>\*\*|(?<!\w)__)(?<bold>.+?)\k<boldMarker>|(?<!\*)\*(?![\s*])(?<italic>[^*\r\n]+?)\*(?!\*)|(?<!\w)_(?![\s_])(?<underscore>[^_\r\n]+?)_(?!\w)|~~(?<strike>[^~\r\n]+)~~",
            RegexOptions.Compiled);
        private static readonly Regex DrivePath = new Regex(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
        private static readonly Regex LineSuffix = new Regex(@"(?::(?<line>[1-9]\d*)(?::[1-9]\d*)?|#L(?<line>[1-9]\d*)(?:-L[1-9]\d*)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string PlainSummary(string body)
        {
            return Json.Clean(ActivityMarkdown.SummaryText(body ?? ""), 180);
        }

        public static string PlainText(string body)
        {
            return Inline.Replace(body, match =>
            {
                var code = match.Groups["code"];
                return code.Success ? code.Value : PlainText(InlineText(match));
            });
        }

        private static string InlineText(Match match)
        {
            foreach (var name in new[] { "escape", "label", "bold", "italic", "underscore", "strike" })
                if (match.Groups[name].Success) return match.Groups[name].Value;
            return match.Value;
        }

        public static ActivityFileLink Resolve(string destination, string workspace)
        {
            if (String.IsNullOrWhiteSpace(destination)) return null;
            var path = destination.Trim();
            if (path.StartsWith("<") && path.EndsWith(">")) path = path.Substring(1, path.Length - 2);
            if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) path = path.Substring(8);
            path = Uri.UnescapeDataString(path);
            if (path.StartsWith("/") && DrivePath.IsMatch(path.Substring(1))) path = path.Substring(1);
            var location = LineSuffix.Match(path);
            var line = 0;
            if (location.Success)
            {
                if (!Int32.TryParse(location.Groups["line"].Value, out line)) return null;
                path = path.Substring(0, location.Index);
            }
            if (path.Length == 0 || path.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|', '\r', '\n', '\0' }) >= 0) return null;
            var absolute = DrivePath.IsMatch(path);
            if (absolute ? path.IndexOf(':', 2) >= 0 : path.StartsWith("/") || path.StartsWith("\\") || path.Contains(":")) return null;
            try
            {
                if (!absolute)
                {
                    if (!DrivePath.IsMatch(workspace) || !Path.HasExtension(path)) return null;
                    path = Path.Combine(workspace, path);
                }
                path = Path.GetFullPath(path);
                return DrivePath.IsMatch(path) ? new ActivityFileLink { Path = path, Line = line } : null;
            }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        public static void Render(InlineCollection inlines, string body, string workspace)
        {
            var position = 0;
            foreach (Match match in Inline.Matches(body))
            {
                if (match.Index > position) inlines.Add(new Run(body.Substring(position, match.Index - position)));
                var link = match.Groups["label"];
                var code = match.Groups["code"];
                var label = InlineText(match);
                if (code.Success) label = code.Value;
                if (link.Success || code.Success)
                {
                    var target = link.Success ? match.Groups["target"].Value : code.Value;
                    var file = !code.Success || target.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 ? Resolve(target, workspace) : null;
                    if (file != null)
                    {
                        var hyperlink = new Hyperlink(new Run(code.Success ? label : PlainText(label))) {
                            Foreground = Palette.Color("#ACCBF1"), ToolTip = file.Reference
                        };
                        hyperlink.Click += delegate
                        {
                            if (!Open(file)) MessageBox.Show("This file could not be opened.\n\n" + file.Reference, Product.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                        };
                        inlines.Add(hyperlink);
                    }
                    else if (code.Success) inlines.Add(new Run(label) { FontFamily = ActivityMarkdown.CodeFont });
                    else Render(inlines, label, workspace);
                }
                else if (match.Groups["escape"].Success) inlines.Add(new Run(label));
                else
                {
                    var style = new Span();
                    if (match.Groups["bold"].Success) style.FontWeight = FontWeights.SemiBold;
                    else if (match.Groups["strike"].Success) style.TextDecorations = TextDecorations.Strikethrough;
                    else style.FontStyle = FontStyles.Italic;
                    Render(style.Inlines, label, workspace);
                    inlines.Add(style);
                }
                position = match.Index + match.Length;
            }
            if (position < body.Length) inlines.Add(new Run(body.Substring(position)));
        }

        private static bool Open(ActivityFileLink file)
        {
            if (!File.Exists(file.Path)) return false;
            try
            {
                var editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
                if (!File.Exists(editor)) editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe");
                var extension = Path.GetExtension(file.Path);
                // Open source and text as data, including scripts. Reveal other files in Explorer.
                var text = Regex.IsMatch(extension, @"^\.(txt|md|mdx|log|csv|tsv|json|jsonl|toml|ya?ml|xml|xaml|html?|css|svg|js|jsx|ts|tsx|mjs|cjs|cs|py|rs|go|java|kt|rb|php|sql|c|h|cpp|hpp|ps1|sh|bat|cmd|ini|cfg|conf)$",
                    RegexOptions.IgnoreCase);
                if (text && File.Exists(editor))
                    Process.Start(new ProcessStartInfo(editor, "--goto \"" + file.Reference + "\"") { UseShellExecute = false });
                else if (text)
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + file.Path + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + file.Path + "\"") { UseShellExecute = false });
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
