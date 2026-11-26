using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Common.Helpers;

namespace GlassBar
{
    /// <summary>
    /// A folder "stack": clicking a pinned folder in the dock shows its newest files as a grid of icons.
    /// Click one to open it. Closes when you click anywhere else.
    /// </summary>
    internal sealed class StackWindow : GlassPopup
    {
        private const int MaxItems = 16;
        private const int Columns = 4;
        private const double TileWidth = 88;
        private const double IconSize = 48;

        private readonly TextBlock _header;
        private readonly WrapPanel _grid;
        private readonly TextBlock _empty;
        private string _folder;

        public StackWindow()
            : base(closeWhenDeactivated: true)
        {
            Panel.Padding = new Thickness(10, 8, 10, 8);

            _header = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(6, 2, 6, 8) };
            _grid = new WrapPanel { Width = Columns * TileWidth };
            _empty = new TextBlock { Text = "This folder is empty.", Opacity = 0.7, Margin = new Thickness(6, 4, 6, 8), Visibility = Visibility.Collapsed };

            Button openFolder = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Content = "Open in File Explorer",
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };
            openFolder.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_folder))
                {
                    ShellHelper.StartProcess(_folder);
                }

                HidePopup();
            };

            StackPanel body = new StackPanel();
            body.Children.Add(_header);
            body.Children.Add(_empty);
            body.Children.Add(_grid);
            body.Children.Add(openFolder);
            Panel.Child = body;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    HidePopup();
                }
            };
        }

        public void ShowFor(DockItem item, Rect anchor)
        {
            _folder = item.Path;
            _header.Text = item.Name;
            _grid.Children.Clear();

            List<FileSystemInfo> entries = NewestEntries(_folder);
            _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (FileSystemInfo entry in entries)
            {
                _grid.Children.Add(MakeTile(entry));
            }

            ShowAt(anchor, above: true);
        }

        private static List<FileSystemInfo> NewestEntries(string folder)
        {
            try
            {
                return new DirectoryInfo(folder)
                    .EnumerateFileSystemInfos()
                    .Where(e => (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    // Downloads often keep the original "modified" date, so the newer of the two dates counts.
                    .OrderByDescending(e => e.LastWriteTimeUtc > e.CreationTimeUtc ? e.LastWriteTimeUtc : e.CreationTimeUtc)
                    .Take(MaxItems)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<FileSystemInfo>();
            }
        }

        private FrameworkElement MakeTile(FileSystemInfo entry)
        {
            Image icon = new Image
            {
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            LoadIconAsync(entry.FullName, icon);

            TextBlock name = new TextBlock
            {
                Text = entry.Name,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                FontSize = 11
            };

            StackPanel content = new StackPanel();
            content.Children.Add(icon);
            content.Children.Add(name);

            Border tile = MakeHoverTile(content, new Thickness(4, 6, 4, 6));
            tile.Width = TileWidth;
            tile.ToolTip = entry.Name + "\n" + (entry.LastWriteTime > entry.CreationTime ? entry.LastWriteTime : entry.CreationTime).ToString("g");
            tile.MouseLeftButtonUp += (s, e) =>
            {
                ShellHelper.StartProcess(entry.FullName);
                HidePopup();
            };
            return tile;
        }

        /// <summary>Loads the file's icon on ManagedShell's icon thread, the same way dock icons load.</summary>
        private static void LoadIconAsync(string path, Image target)
        {
            Task.Factory.StartNew(() => ShellIcons.Load(path, 96), CancellationToken.None, TaskCreationOptions.None, IconHelper.IconScheduler)
                .ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    {
                        target.Source = t.Result;
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
