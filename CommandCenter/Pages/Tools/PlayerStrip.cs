using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandCenter.Pages.Tools
{
    // One player of a match as the replay list shows it: badge in the player's colour and the name
    public sealed record PlayerChip(string Name, Brush Color, BitmapSource? Emblem, int Side);

    // The players of a match on one line. Shows as many as fit in the column and "+N" for the rest, so long names
    // and big games never run into the next column; the names of one side are kept together, with "vs" between sides.
    public sealed class PlayerStrip : Panel
    {
        private const double Gap = 10;
        private const double NameWidth = 128;

        public static readonly DependencyProperty PlayersProperty = DependencyProperty.Register(
            nameof(Players), typeof(IReadOnlyList<PlayerChip>), typeof(PlayerStrip),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((PlayerStrip)d).Rebuild()));

        public IReadOnlyList<PlayerChip>? Players
        {
            get => (IReadOnlyList<PlayerChip>?)GetValue(PlayersProperty);
            set => SetValue(PlayersProperty, value);
        }

        private readonly TextBlock _moreText = new() { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Views.Hint };
        private readonly Border _more;
        private readonly List<UIElement> _items = new();   // chips and "vs" separators, in order
        private readonly HashSet<UIElement> _chips = new();
        private int _shown;
        private bool _showMore;
        private double _firstWidth = double.NaN;

        public PlayerStrip()
        {
            _more = new Border
            {
                Background = Views.Brush("#07091C"),
                BorderBrush = Views.Brush("#2A2A55"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _moreText,
            };
            ClipToBounds = true;
        }

        private void Rebuild()
        {
            Children.Clear();
            _items.Clear();
            _chips.Clear();
            var players = Players ?? Array.Empty<PlayerChip>();
            var sides = players.GroupBy(p => p.Side).ToList();
            // Everyone on their own in a bigger game: no "vs" between every name
            bool versus = sides.Count == 2 || sides.Any(s => s.Count() > 1);
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0 && versus && players[i].Side != players[i - 1].Side)
                    Add(new TextBlock { Text = Services.Loc.T("vs"), FontSize = 11, Foreground = Views.Hint, VerticalAlignment = VerticalAlignment.Center });
                var chip = Chip(players[i]);
                Add(chip);
                _chips.Add(chip);
            }
            Children.Add(_more);
        }

        private void Add(UIElement element)
        {
            _items.Add(element);
            Children.Add(element);
        }

        private static FrameworkElement Chip(PlayerChip player)
        {
            // A dock panel, so a name squeezed below its width is shortened with "…"
            var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement badge;
            if (player.Emblem != null)
            {
                badge = new Image { Source = player.Emblem, Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0), FlowDirection = FlowDirection.LeftToRight };
                RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Fant);
            }
            else
            {
                badge = new Border { Width = 9, Height = 9, Background = player.Color, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            }
            DockPanel.SetDock(badge, Dock.Left);
            panel.Children.Add(badge);
            panel.Children.Add(new TextBlock
            {
                Text = player.Name,
                FontSize = 12,
                Foreground = Views.Brush("#D0D0E8"),
                MaxWidth = NameWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FlowDirection = FlowDirection.LeftToRight,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return panel;
        }

        protected override Size MeasureOverride(Size available)
        {
            var any = new Size(double.PositiveInfinity, available.Height);
            foreach (var item in _items)
                item.Measure(any);

            double height = _items.Count == 0 ? 0 : _items.Max(i => i.DesiredSize.Height);
            double all = RunWidth(_items.Count);
            _firstWidth = double.NaN;
            _shown = _items.Count;
            _showMore = false;

            if (all > available.Width && _chips.Count > 0)
            {
                // Longest run that still leaves room for "+N"; a run always ends on a player
                _shown = 0;
                for (int n = _items.Count - 1; n >= 1; n--)
                {
                    if (!_chips.Contains(_items[n - 1]))
                        continue;
                    SetMore(_items.Skip(n).Count(_chips.Contains));
                    if (RunWidth(n) + Gap + _more.DesiredSize.Width <= available.Width)
                    {
                        _shown = n;
                        break;
                    }
                }
                if (_shown == 0)
                {
                    // Not even one full name fits: the first one is shortened
                    _shown = 1;
                    SetMore(_chips.Count - 1);
                    _firstWidth = Math.Max(0, available.Width - Gap - _more.DesiredSize.Width);
                    _items[0].Measure(new Size(_firstWidth, available.Height));
                }
                _showMore = true;
            }
            _more.Measure(any);
            height = Math.Max(height, _more.DesiredSize.Height);

            double width = RunWidth(_shown) + (_showMore ? Gap + _more.DesiredSize.Width : 0);
            if (!double.IsNaN(_firstWidth))
                width = _firstWidth + Gap + _more.DesiredSize.Width;
            return new Size(Math.Min(width, double.IsInfinity(available.Width) ? width : available.Width), height);
        }

        private void SetMore(int hidden)
        {
            string text = "+" + hidden;
            if (_moreText.Text != text)
                _moreText.Text = text;
            _more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        // Width of the first n items with the gaps between them
        private double RunWidth(int n)
        {
            double width = 0;
            for (int i = 0; i < n; i++)
                width += _items[i].DesiredSize.Width + (i > 0 ? Gap : 0);
            return width;
        }

        protected override Size ArrangeOverride(Size final)
        {
            double x = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (i >= _shown)
                {
                    item.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }
                double w = i == 0 && !double.IsNaN(_firstWidth) ? _firstWidth : item.DesiredSize.Width;
                item.Arrange(new Rect(x, (final.Height - item.DesiredSize.Height) / 2, w, item.DesiredSize.Height));
                x += w + Gap;
            }
            if (_showMore)
                _more.Arrange(new Rect(x, (final.Height - _more.DesiredSize.Height) / 2, _more.DesiredSize.Width, _more.DesiredSize.Height));
            else
                _more.Arrange(new Rect(0, 0, 0, 0));
            return final;
        }
    }
}
