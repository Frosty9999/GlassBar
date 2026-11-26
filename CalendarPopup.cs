using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Common.Helpers;

namespace GlassBar
{
    /// <summary>A month calendar under the clock. The arrows (or the mouse wheel) change month; Esc closes it.</summary>
    internal sealed class CalendarPopup : GlassPopup
    {
        private const double CellSize = 32;

        private readonly TextBlock _title;
        private readonly TextBlock _today;
        private readonly UniformGrid _days;
        private DateTime _month;

        public CalendarPopup()
            : base(closeWhenDeactivated: true)
        {
            Panel.Padding = new Thickness(12, 10, 12, 10);

            _today = new TextBlock { FontSize = 13, Opacity = 0.75, Margin = new Thickness(4, 0, 0, 8) };

            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            Button previous = ArrowButton("", "Previous month", -1);
            Button next = ArrowButton("", "Next month", 1);
            Button home = new Button
            {
                Style = BarButtonStyle,
                Content = "Today",
                Padding = new Thickness(8, 3, 8, 3),
                ToolTip = "Back to this month"
            };
            home.Click += (s, e) => ShowMonth(DateTime.Today);

            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            StackPanel arrows = new StackPanel { Orientation = Orientation.Horizontal };
            arrows.Children.Add(home);
            arrows.Children.Add(previous);
            arrows.Children.Add(next);
            DockPanel.SetDock(arrows, Dock.Right);
            header.Children.Add(arrows);
            header.Children.Add(_title);

            // Weekday names, starting on the user's first day of the week
            UniformGrid weekdays = new UniformGrid { Columns = 7, Rows = 1 };
            DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
            for (int i = 0; i < 7; i++)
            {
                DayOfWeek day = (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7);
                weekdays.Children.Add(new TextBlock
                {
                    Text = format.GetShortestDayName(day),
                    Width = CellSize,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.6,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            _days = new UniformGrid { Columns = 7, Rows = 6 };

            Button notifications = new Button
            {
                Style = BarButtonStyle,
                Content = "Notifications",
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            notifications.Click += (s, e) =>
            {
                HidePopup();
                ShellHelper.ShowNotificationCenter();
            };

            StackPanel body = new StackPanel();
            body.Children.Add(_today);
            body.Children.Add(header);
            body.Children.Add(weekdays);
            body.Children.Add(_days);
            body.Children.Add(notifications);
            Panel.Child = body;

            PreviewMouseWheel += (s, e) => ShowMonth(_month.AddMonths(e.Delta > 0 ? -1 : 1));
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    HidePopup();
                }
                else if (e.Key == Key.Left || e.Key == Key.PageUp)
                {
                    ShowMonth(_month.AddMonths(-1));
                }
                else if (e.Key == Key.Right || e.Key == Key.PageDown)
                {
                    ShowMonth(_month.AddMonths(1));
                }
            };
        }

        private static Style BarButtonStyle => (Style)Application.Current.FindResource("BarButton");

        private Button ArrowButton(string glyph, string tip, int months)
        {
            Button button = new Button
            {
                Style = BarButtonStyle,
                Content = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                ToolTip = tip
            };
            button.Click += (s, e) => ShowMonth(_month.AddMonths(months));
            return button;
        }

        public void ShowBelow(Rect anchor)
        {
            _today.Text = DateTime.Today.ToString("D", CultureInfo.CurrentCulture);
            ShowMonth(DateTime.Today);
            ShowAt(anchor, above: false);
        }

        private void ShowMonth(DateTime anyDay)
        {
            _month = new DateTime(anyDay.Year, anyDay.Month, 1);
            _title.Text = _month.ToString("Y", CultureInfo.CurrentCulture); // e.g. "November 2026"
            _days.Children.Clear();

            int firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            int offset = ((int)_month.DayOfWeek - firstDay + 7) % 7;
            DateTime start = _month.AddDays(-offset);

            for (int i = 0; i < 42; i++)
            {
                DateTime day = start.AddDays(i);
                bool isToday = day == DateTime.Today;
                bool inMonth = day.Month == _month.Month;

                TextBlock number = new TextBlock
                {
                    Text = day.Day.ToString(CultureInfo.CurrentCulture),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Opacity = inMonth ? 1 : 0.35
                };
                if (isToday)
                {
                    number.Foreground = Brushes.White;
                }

                _days.Children.Add(new Border
                {
                    Width = CellSize,
                    Height = CellSize - 4,
                    CornerRadius = new CornerRadius((CellSize - 4) / 2),
                    Background = isToday ? S.AccentBrush : Brushes.Transparent,
                    Child = number
                });
            }
        }
    }
}
