using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Girt.Controls;
using Girt.Models;
using Girt.ViewModels;
using Xunit;

namespace Girt.Tests
{
    public class DiffViewerVirtualizationTests
    {
        private sealed class FakeDiffHost : IDiffLineHost
        {
            public ObservableCollection<DiffLine> DiffLines { get; } = new();
            public void ToggleDiffSection(DiffLine? line) { }
            public void ExpandAllDiffSections() { }
        }

        // Regression test for the diff view freezing Girt on a large file: rendering a
        // 50,000-line diff in a normal-sized window must only build rows for roughly what's on
        // screen. It used to build one per line (the list was wrapped in an outer ScrollViewer,
        // which switches WPF virtualization off).
        [Fact]
        public void LargeDiff_OnlyBuildsRowsForTheVisibleArea()
        {
            Exception? failure = null;
            int realizedRows = -1;

            var thread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                    var host = new FakeDiffHost();
                    for (var i = 0; i < 50_000; i++)
                    {
                        host.DiffLines.Add(new DiffLine { Type = DiffLineType.Context, OldLineNumber = i, NewLineNumber = i, Text = " line " + i });
                    }

                    var viewer = new DiffViewerControl { DataContext = host };
                    var window = new Window
                    {
                        Content = viewer,
                        Width = 800,
                        Height = 600,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000
                    };
                    window.Show();
                    window.UpdateLayout();

                    var itemsControl = (ItemsControl)viewer.FindName("DiffLinesItemsControl");
                    var panel = FindDescendant<VirtualizingStackPanel>(itemsControl);
                    realizedRows = panel?.Children.Count ?? -1;

                    window.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null) throw new Exception("UI thread failed", failure);
            Assert.InRange(realizedRows, 1, 500);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
