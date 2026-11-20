using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace OrbitEvolved
{
    internal static class ActivityMarkdown
    {
        internal static readonly FontFamily CodeFont = new FontFamily("Consolas");
        private static readonly Brush Muted = Palette.Color("#AEB8C8");
        private static readonly Brush Inset = Palette.Color("#1C9AADCF");
        private static readonly Brush Rule = Palette.Color("#309AADCF");
        private static readonly Regex Fence = new Regex(@"^ {0,3}(?<marker>`{3,}|~{3,})(?<info>.*)$", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(@"^ {0,3}(?<marker>#{1,6})[ \t]+(?<text>.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex ListItem = new Regex(@"^(?<indent>[ \t]*)(?<mark>[-+*]|\d{1,9}[.)])[ \t]+(?<text>.*)$", RegexOptions.Compiled);
        private static readonly Regex Quote = new Regex(@"^ {0,3}> ?(?<text>.*)$", RegexOptions.Compiled);
        private static readonly Regex Separator = new Regex(@"^ {0,3}(?:\*\s*\*\s*\*[*\s]*|-\s*-\s*-[-\s]*|_\s*_\s*_[ _]*)$", RegexOptions.Compiled);
        private static readonly Regex TableSeparator = new Regex(@"^:?-+:?$", RegexOptions.Compiled);
        private static readonly Regex TableTokens = new Regex(@"\\[\\|]|`+[^`]*`+|\|", RegexOptions.Compiled);

        public static void Render(TextBlock block, string body, string workspace)
        {
            var lines = Lines(body);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) block.Inlines.Add(new LineBreak());
                var line = lines[i];
                var fence = Fence.Match(line);
                if (fence.Success)
                {
                    var code = ReadFence(lines, fence, ref i);
                    block.Inlines.Add(new InlineUIContainer(CodeBlock(code)));
                    continue;
                }
                string[] header;
                string[] separator;
                if (TryTable(lines, i, out header, out separator))
                {
                    var rows = ReadTableRows(lines, header.Length, ref i);
                    block.Inlines.Add(new InlineUIContainer(new ActivityMarkdownTable(header, separator, rows, workspace)));
                    continue;
                }
                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    var span = new Span
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = block.FontSize + (heading.Groups["marker"].Length <= 2 ? 2 : 1)
                    };
                    ActivityLinks.Render(span.Inlines, heading.Groups["text"].Value, workspace);
                    block.Inlines.Add(span);
                    continue;
                }
                if (Separator.IsMatch(line))
                {
                    block.Inlines.Add(new InlineUIContainer(new Border { Background = Rule, Height = 1, Width = 28, Margin = new Thickness(0, 5, 0, 5) }));
                    continue;
                }
                var item = ListItem.Match(line);
                if (item.Success)
                {
                    block.Inlines.Add(new InlineUIContainer(ListLine(item, workspace)));
                    continue;
                }
                if (Quote.IsMatch(line))
                {
                    var quote = ReadQuote(lines, ref i);
                    block.Inlines.Add(new InlineUIContainer(new Border
                    {
                        BorderBrush = Rule, BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(8, 1, 2, 1), Child = Text(quote, workspace)
                    }));
                    continue;
                }
                ActivityLinks.Render(block.Inlines, line, workspace);
            }
        }

        public static string SummaryText(string body)
        {
            var lines = Lines(body);
            var summary = new StringBuilder();
            for (var i = 0; i < lines.Length && summary.Length < 180; i++)
            {
                var line = lines[i];
                var fence = Fence.Match(line);
                if (fence.Success)
                {
                    if (summary.Length == 0) summary.Append(ReadFence(lines, fence, ref i).Trim());
                    else ReadFence(lines, fence, ref i);
                    continue;
                }
                string[] header;
                string[] separator;
                if (TryTable(lines, i, out header, out separator))
                {
                    var rows = ReadTableRows(lines, header.Length, ref i);
                    summary.Append(' ').Append(ActivityLinks.PlainText(String.Join(" · ", header)));
                    if (rows.Count > 0)
                    {
                        var values = rows[0].Where(cell => !String.IsNullOrWhiteSpace(cell));
                        foreach (var value in values) summary.Append(" · ").Append(ActivityLinks.PlainText(value));
                    }
                    continue;
                }
                if (Separator.IsMatch(line) || String.IsNullOrWhiteSpace(line)) continue;
                foreach (var pattern in new[] { Heading, ListItem, Quote })
                {
                    var match = pattern.Match(line);
                    if (!match.Success) continue;
                    line = match.Groups["text"].Value;
                    break;
                }
                summary.Append(' ').Append(ActivityLinks.PlainText(line.Trim()));
            }
            return summary.ToString();
        }

        private static string[] Lines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string ReadFence(string[] lines, Match opener, ref int index)
        {
            var marker = opener.Groups["marker"].Value;
            var first = ++index;
            while (index < lines.Length)
            {
                var closing = Fence.Match(lines[index]);
                if (closing.Success && closing.Groups["marker"].Value[0] == marker[0] &&
                    closing.Groups["marker"].Length >= marker.Length && String.IsNullOrWhiteSpace(closing.Groups["info"].Value))
                    return String.Join("\n", lines, first, index - first);
                index++;
            }
            index--;
            return String.Join("\n", lines, first, lines.Length - first);
        }

        private static string ReadQuote(string[] lines, ref int index)
        {
            var text = new List<string>();
            while (index < lines.Length)
            {
                var line = Quote.Match(lines[index]);
                if (!line.Success) break;
                text.Add(line.Groups["text"].Value);
                index++;
            }
            index--;
            return String.Join("\n", text);
        }

        private static bool TryTable(string[] lines, int index, out string[] header, out string[] separator)
        {
            header = null;
            separator = null;
            if (index + 1 >= lines.Length || lines[index + 1].IndexOf('|') < 0) return false;
            header = TableCells(lines[index]);
            if (header == null || header.Length == 0 || StartsBlock(lines[index])) return false;
            separator = TableCells(lines[index + 1]);
            return separator != null && header.Length == separator.Length && separator.All(cell => TableSeparator.IsMatch(cell));
        }

        private static bool StartsBlock(string line)
        {
            return line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)
                || Heading.IsMatch(line) || Fence.IsMatch(line) || Quote.IsMatch(line) || ListItem.IsMatch(line) || Separator.IsMatch(line);
        }

        private static List<string[]> ReadTableRows(string[] lines, int columns, ref int index)
        {
            var rows = new List<string[]>();
            index += 2;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (String.IsNullOrWhiteSpace(line) || StartsBlock(line)) break;
                var cells = TableCells(line) ?? new[] { line.Trim() };
                var explicitCells = cells.Length;
                if (explicitCells != columns) Array.Resize(ref cells, columns);
                for (var column = explicitCells; column < columns; column++) cells[column] = "";
                rows.Add(cells);
                index++;
            }
            index--;
            return rows;
        }

        private static string[] TableCells(string line)
        {
            if (line.IndexOf('|') < 0) return null;
            line = line.Trim();
            var parts = new List<string>();
            var start = 0;
            foreach (Match token in TableTokens.Matches(line))
            {
                if (token.Value != "|") continue;
                if (token.Index > 0) parts.Add(line.Substring(start, token.Index - start).Trim());
                start = token.Index + 1;
            }
            if (start == 0) return null;
            if (start < line.Length) parts.Add(line.Substring(start).Trim());
            return parts.ToArray();
        }

        private static Border CodeBlock(string code)
        {
            return new Border
            {
                Background = Inset, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(3),
                Child = new TextBlock { Text = code, FontFamily = CodeFont, TextWrapping = TextWrapping.Wrap }
            };
        }

        private static Grid ListLine(Match item, string workspace)
        {
            var grid = new Grid();
            var indent = item.Groups["indent"].Value.Replace("\t", "    ").Length;
            grid.Margin = new Thickness(Math.Min(indent, 8) * 4, 0, 0, 0);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var mark = item.Groups["mark"].Value;
            var marker = new TextBlock
            {
                Text = mark.Length == 1 ? "•" : mark, Foreground = Muted,
                Margin = new Thickness(0, 0, 7, 0)
            };
            var detail = Text(item.Groups["text"].Value, workspace);
            Grid.SetColumn(detail, 1);
            grid.Children.Add(marker);
            grid.Children.Add(detail);
            return grid;
        }

        internal static TextBlock Text(string content, string workspace)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ActivityLinks.Render(block.Inlines, content, workspace);
            return block;
        }
    }

    internal sealed class ActivityMarkdownTable : Grid
    {
        private static readonly Brush HeaderFill = Palette.Color("#249AADCF");
        private static readonly Brush AlternateFill = Palette.Color("#0F9AADCF");
        private static readonly Brush BorderColor = Palette.Color("#259AADCF");
        internal IList<string> ColumnHeaders { get; private set; }
        internal int DataRowCount { get; private set; }

        public ActivityMarkdownTable(string[] header, string[] separator, List<string[]> rows, string workspace)
        {
            ColumnHeaders = Array.AsReadOnly(header.Select(ActivityLinks.PlainText).ToArray());
            DataRowCount = rows.Count;
            if (header.Length <= 4 || rows.Count == 0)
            {
                AddColumns(header, separator, rows, workspace);
            }
            else
            {
                // Wide tables stay readable within the fixed-width activity panel.
                AddRecords(header, rows, workspace);
            }
        }

        private void AddColumns(string[] header, string[] separator, List<string[]> rows, string workspace)
        {
            for (var column = 0; column < header.Length; column++)
            {
                var typicalWidth = Math.Max(ColumnHeaders[column].Length,
                    rows.Count == 0 ? 0 : rows.Max(row => ActivityLinks.PlainText(row[column]).Length));
                var weight = Math.Sqrt(Math.Min(100, Math.Max(7, typicalWidth)));
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            }
            AddRow(header, separator, workspace, 0);
            for (var row = 0; row < rows.Count; row++) AddRow(rows[row], separator, workspace, row + 1);
        }

        private void AddRow(string[] cells, string[] separator, string workspace, int row)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < cells.Length; column++)
            {
                var alignment = TextAlignment.Left;
                if (separator[column].EndsWith(":"))
                    alignment = separator[column].StartsWith(":") ? TextAlignment.Center : TextAlignment.Right;
                var text = ActivityMarkdown.Text(cells[column], workspace);
                text.TextAlignment = alignment;
                if (row == 0) text.FontWeight = FontWeights.SemiBold;
                var fill = row == 0 ? HeaderFill : row % 2 == 0 ? AlternateFill : Brushes.Transparent;
                var cell = Cell(text, fill, new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1));
                SetColumn(cell, column);
                SetRow(cell, row);
                Children.Add(cell);
            }
        }

        private void AddRecords(string[] header, List<string[]> rows, string workspace)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var row = 0;
            foreach (var record in rows)
            {
                for (var column = 0; column < header.Length; column++, row++)
                {
                    RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var top = column == 0 ? 1 : 0;
                    var label = ActivityMarkdown.Text(header[column], workspace);
                    label.FontWeight = FontWeights.SemiBold;
                    var name = Cell(label, HeaderFill, new Thickness(1, top, 1, 1));
                    var value = Cell(ActivityMarkdown.Text(record[column], workspace), AlternateFill, new Thickness(0, top, 1, 1));
                    if (top == 1 && row > 0) name.Margin = value.Margin = new Thickness(0, 5, 0, 0);
                    SetRow(name, row);
                    SetRow(value, row);
                    SetColumn(value, 1);
                    Children.Add(name);
                    Children.Add(value);
                }
            }
        }

        private static Border Cell(TextBlock text, Brush fill, Thickness border)
        {
            return new Border
            {
                Child = text, Background = fill, BorderBrush = BorderColor, BorderThickness = border,
                Padding = new Thickness(6, 3, 6, 3)
            };
        }
    }
}
