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

            // 1. Draw connections
            if (commit.Connections != null)
            {
                foreach (var conn in commit.Connections)
                {
                    var brush = GetBrush(conn.Color);
                    var pen = new Pen(brush, 2.0);
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
            // If it's on a lane, draw vertical line from top (0) to center (halfHeight)
            var nodeX = commit.LaneIndex * laneWidth + laneWidth / 2.0;
            var nodeBrush = GetBrush(commit.LaneColor);
            var nodePen = new Pen(nodeBrush, 2.0);
            nodePen.Freeze();

            // Line coming in from top to node center (unless it's the root/new branch tip with no incoming)
            dc.DrawLine(nodePen, new Point(nodeX, 0), new Point(nodeX, halfHeight));

            // 3. Draw commit node circle
            var radius = 4.5;
            dc.DrawEllipse(nodeBrush, null, new Point(nodeX, halfHeight), radius, radius);

            // Inner circle dot for sharp crisp high contrast look
            var innerBrush = Brushes.White;
            dc.DrawEllipse(innerBrush, null, new Point(nodeX, halfHeight), 1.8, 1.8);
        }

        private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

        private static SolidColorBrush GetBrush(string hex)
        {
            if (BrushCache.TryGetValue(hex, out var brush))
            {
                return brush;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var newBrush = new SolidColorBrush(color);
                newBrush.Freeze();
                BrushCache[hex] = newBrush;
                return newBrush;
            }
            catch
            {
                var fallback = Brushes.CornflowerBlue;
                BrushCache[hex] = fallback;
                return fallback;
            }
        }
    }
}
