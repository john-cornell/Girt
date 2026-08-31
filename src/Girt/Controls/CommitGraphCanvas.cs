using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Girt.Models;

namespace Girt.Controls
{
    public class CommitGraphCanvas : FrameworkElement
    {
        public static readonly DependencyProperty CommitProperty =
            DependencyProperty.Register(
                nameof(Commit),
                typeof(GitCommit),
                typeof(CommitGraphCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public GitCommit? Commit
        {
            get => (GitCommit?)GetValue(CommitProperty);
            set => SetValue(CommitProperty, value);
        }

        public static readonly DependencyProperty LaneWidthProperty =
            DependencyProperty.Register(
                nameof(LaneWidth),
                typeof(double),
                typeof(CommitGraphCanvas),
                new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double LaneWidth
        {
            get => (double)GetValue(LaneWidthProperty);
            set => SetValue(LaneWidthProperty, value);
        }

        public static readonly DependencyProperty RowHeightProperty =
            DependencyProperty.Register(
                nameof(RowHeight),
                typeof(double),
                typeof(CommitGraphCanvas),
                new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double RowHeight
        {
            get => (double)GetValue(RowHeightProperty);
            set => SetValue(RowHeightProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var commit = Commit;
            if (commit == null) return;

            var laneWidth = LaneWidth;
            var rowHeight = RowHeight;
            var halfHeight = rowHeight / 2.0;
            var isDimmed = commit.IsDimmed;

            // 1. Draw connections
            if (commit.Connections != null)
            {
                foreach (var conn in commit.Connections)
                {
                    var brush = GetBrush(conn.Color, isDimmed);
                    var pen = new Pen(brush, isDimmed ? 1.5 : 2.0);
                    pen.Freeze();

                    var fromX = conn.FromLane * laneWidth + laneWidth / 2.0;
                    var toX = conn.ToLane * laneWidth + laneWidth / 2.0;

                    if (conn.IsPassThrough)
                    {
                        // Straight vertical line through entire row
                        dc.DrawLine(pen, new Point(fromX, 0), new Point(fromX, rowHeight));
                    }
                    else
                    {
                        // Line from this commit node center down to the bottom
                        if (conn.FromLane == conn.ToLane)
                        {
                            // Straight down to parent in same lane
                            dc.DrawLine(pen, new Point(fromX, halfHeight), new Point(toX, rowHeight));
                        }
                        else
                        {
                            // Curved bezier branch / merge line
                            var geometry = new PathGeometry();
                            var figure = new PathFigure { StartPoint = new Point(fromX, halfHeight) };
                            
                            // Cubic Bezier curve down to (toX, rowHeight)
                            var p1 = new Point(fromX, halfHeight + halfHeight * 0.5);
                            var p2 = new Point(toX, halfHeight + halfHeight * 0.5);
                            var p3 = new Point(toX, rowHeight);

                            figure.Segments.Add(new BezierSegment(p1, p2, p3, true));
                            geometry.Figures.Add(figure);
                            geometry.Freeze();

                            dc.DrawGeometry(null, pen, geometry);
                        }
                    }
                }
            }

            // 2. Draw incoming connection from top (if this commit connects with earlier child commit)
            var nodeX = commit.LaneIndex * laneWidth + laneWidth / 2.0;
            var nodeBrush = GetBrush(commit.LaneColor, isDimmed);
            var nodePen = new Pen(nodeBrush, isDimmed ? 1.5 : 2.0);
            nodePen.Freeze();

            // Line coming in from top to node center
            dc.DrawLine(nodePen, new Point(nodeX, 0), new Point(nodeX, halfHeight));

            // 3. Draw commit node circle
            var radius = isDimmed ? 3.5 : 4.5;
            dc.DrawEllipse(nodeBrush, null, new Point(nodeX, halfHeight), radius, radius);

            // Inner circle dot
            var innerBrush = isDimmed ? GetDimmedWhiteBrush() : Brushes.White;
            dc.DrawEllipse(innerBrush, null, new Point(nodeX, halfHeight), isDimmed ? 1.2 : 1.8, isDimmed ? 1.2 : 1.8);
        }

        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
        private static SolidColorBrush? _dimmedWhiteBrush;

        private static SolidColorBrush GetDimmedWhiteBrush()
        {
            if (_dimmedWhiteBrush == null)
            {
                _dimmedWhiteBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 200));
                _dimmedWhiteBrush.Freeze();
            }
            return _dimmedWhiteBrush;
        }

        private static SolidColorBrush GetBrush(string hex, bool isDimmed)
        {
            var key = isDimmed ? $"{hex}_dim" : hex;
            if (BrushCache.TryGetValue(key, out var brush))
            {
                return brush;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                if (isDimmed)
                {
                    color = Color.FromArgb(65, color.R, color.G, color.B);
                }
                var newBrush = new SolidColorBrush(color);
                newBrush.Freeze();
                BrushCache[key] = newBrush;
                return newBrush;
            }
            catch
            {
                var fallback = isDimmed ? new SolidColorBrush(Color.FromArgb(65, 100, 149, 237)) : Brushes.CornflowerBlue;
                fallback.Freeze();
                BrushCache[key] = fallback;
                return fallback;
            }
        }
    }
}
