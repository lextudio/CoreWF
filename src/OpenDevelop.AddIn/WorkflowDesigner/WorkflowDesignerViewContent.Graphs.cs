using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;

using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>The designer's graph surfaces: a Flowchart's nodes and edges and a StateMachine's
/// states and transitions, including the node-position drag and the connector drag that draws a
/// new edge. Split out of WorkflowDesignerViewContent.cs, whose remaining canvas code renders the
/// ordinary nested-box tree.</summary>
public sealed partial class WorkflowDesignerViewContent
{
	Canvas FlowchartGraph(DesignerElementNode flowchart)
	{
		var positions = flowchart.Children.Select((child, index) => FlowPosition(child.Id, index)).ToArray();
		var maximumX = positions.Length == 0 ? 0 : positions.Max(position => position.X);
		var maximumY = positions.Length == 0 ? 0 : positions.Max(position => position.Y);
		var graph = new Canvas {
			Width = Math.Max(650, maximumX + 220),
			MinHeight = Math.Max(120, maximumY + 110),
			Background = Brushes.WhiteSmoke, Margin = new Thickness(0, 6, 0, 0)
		};
		foreach (var child in flowchart.Children) {
			DrawFlowEdge(graph, flowchart.Children, child, "$flowNext", Brushes.Teal, "Next");
			DrawFlowEdge(graph, flowchart.Children, child, "$flowTrue", Brushes.ForestGreen, "True");
			DrawFlowEdge(graph, flowchart.Children, child, "$flowFalse", Brushes.IndianRed, "False");
		}
		for (var index = 0; index < flowchart.Children.Count; index++) {
			var child = flowchart.Children[index];
			var box = Box(child);
			box.Width = 190;
			box.Tag = child.Id;
			var position = FlowPosition(child.Id, index);
			Canvas.SetLeft(box, position.X);
			Canvas.SetTop(box, position.Y);
			EnableFlowNodeDrag(graph, box, child);
			graph.Children.Add(box);
			AddFlowConnector(graph, box, child);
		}
		// After the nodes, not before: the marker sits beside its entry step, and when that step is
		// not the leftmost one the marker's position can fall behind another node's box. Canvas
		// paints in child order, so drawing it last keeps it visible instead of hidden underneath.
		DrawFlowStart(graph, flowchart);
		return graph;
	}

	/// <summary>
	/// Draws WF's green Start node and its arrow into the entry step. Without it a flowchart shows
	/// several boxes with no indication of which one runs first - the single most recognisable
	/// element of the WF flowchart canvas, and the visual counterpart to the "Set Start" command.
	/// Draws nothing when the host reports no start node (an empty or not-yet-wired flowchart).
	/// </summary>
	void DrawFlowStart(Canvas graph, DesignerElementNode flowchart)
	{
		var startId = flowchart.Properties?.FirstOrDefault(property => property.Name == "$flowStartNode")?.Value;
		if (string.IsNullOrEmpty(startId)) return;
		var index = flowchart.Children.FindIndex(child => child.Id == startId);
		if (index < 0) return;
		var target = FlowPosition(startId!, index);

		// Prefer WF's placement - marker above the entry step with the arrow pointing down into it -
		// but a step sitting near the top of the canvas has no room for that, and drawing it there
		// anyway put the marker on top of the node with its arrow collapsed to zero length. Fall
		// back to the left, where there is always room, instead of overlapping the diagram.
		const double radius = 15, captionHeight = 13, gap = 10;
		var above = target.Y >= radius * 2 + captionHeight + gap * 2;
		var centreX = above ? target.X + 95 : Math.Max(radius + 2, target.X - radius * 2 - gap * 2);
		var centreY = above ? target.Y - radius - captionHeight - gap : target.Y + 22;

		var circle = new Ellipse {
			Width = radius * 2, Height = radius * 2, Fill = Brushes.White,
			Stroke = Brushes.ForestGreen, StrokeThickness = 2,
			ToolTip = "Start - the flowchart's entry node", IsHitTestVisible = false
		};
		Canvas.SetLeft(circle, centreX - radius);
		Canvas.SetTop(circle, centreY - radius);
		graph.Children.Add(circle);

		var glyph = new Polygon {
			Points = new PointCollection { new Point(0, 0), new Point(9, 5), new Point(0, 10) },
			Fill = Brushes.ForestGreen, IsHitTestVisible = false
		};
		Canvas.SetLeft(glyph, centreX - 3);
		Canvas.SetTop(glyph, centreY - 5);
		graph.Children.Add(glyph);

		var caption = new TextBlock { Text = "Start", Foreground = Brushes.ForestGreen, FontSize = 10, IsHitTestVisible = false };
		Canvas.SetLeft(caption, centreX - 13);
		Canvas.SetTop(caption, centreY + radius);
		graph.Children.Add(caption);

		var (x1, y1, x2, y2) = above
			? (centreX, centreY + radius + captionHeight, centreX, target.Y)
			: (centreX + radius, centreY, target.X, centreY);
		var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brushes.ForestGreen, StrokeThickness = 2, IsHitTestVisible = false };
		graph.Children.Add(line);
		DrawArrowHead(graph, x1, y1, x2, y2, Brushes.ForestGreen);
	}

	/// <summary>StateMachine is graph-shaped just like Flowchart, but its transition metadata
	/// lives below each virtual State node. Render it as a graph rather than a stack of nested
	/// activity boxes so the initial/final state and transition targets are immediately visible.</summary>
	Canvas StateMachineGraph(DesignerElementNode stateMachine)
	{
		var states = stateMachine.Children.Where(node => node.Type == "State").ToArray();
		var positions = states.Select((child, index) => FlowPosition(child.Id, index)).ToArray();
		var maximumX = positions.Length == 0 ? 0 : positions.Max(position => position.X);
		var maximumY = positions.Length == 0 ? 0 : positions.Max(position => position.Y);
		var graph = new Canvas { Width = Math.Max(650, maximumX + 220), MinHeight = Math.Max(120, maximumY + 120), Background = Brushes.AliceBlue, Margin = new Thickness(0, 6, 0, 0) };
		foreach (var source in states) {
			foreach (var transition in source.Children.Where(node => node.Type == "Transition")) {
				var targetId = transition.Properties.FirstOrDefault(property => property.Name == "$target")?.Value;
				if (string.IsNullOrEmpty(targetId) || states.FirstOrDefault(state => state.Id == targetId) is not { } target) continue;
				var from = FlowPosition(source.Id, 0);
				var to = FlowPosition(target.Id, 0);
				var line = new Line { X1 = from.X + 190, Y1 = from.Y + 40, X2 = to.X, Y2 = to.Y + 40, Stroke = Brushes.MediumPurple, StrokeThickness = 2, IsHitTestVisible = false };
				graph.Children.Add(line);
				DrawArrowHead(graph, line.X1, line.Y1, line.X2, line.Y2, Brushes.MediumPurple);
				var label = new TextBlock { Text = string.IsNullOrWhiteSpace(transition.Name) || transition.Name == "Transition" ? "Transition" : transition.Name, Foreground = Brushes.MediumPurple, FontSize = 10, Background = Brushes.AliceBlue, IsHitTestVisible = false };
				Canvas.SetLeft(label, (line.X1 + line.X2) / 2 - 28);
				Canvas.SetTop(label, (line.Y1 + line.Y2) / 2 - 15);
				graph.Children.Add(label);
			}
		}
		for (var index = 0; index < states.Length; index++) {
			var state = states[index];
			var initial = state.Properties.FirstOrDefault(property => property.Name == "$initial")?.Value == "True";
			var final = state.Properties.FirstOrDefault(property => property.Name == "IsFinal")?.Value == "True";
			var title = new TextBlock { Text = (initial ? "▶ " : "") + state.Name + (final ? "  ■" : ""), FontWeight = FontWeights.Bold };
			var detail = new TextBlock { Text = state.Children.Count(child => child.Type == "Transition") + " transition(s)", Foreground = Brushes.DimGray, FontSize = 10, Margin = new Thickness(0, 4, 0, 0) };
			var card = new Border { Width = 190, BorderBrush = ValidationBorder(state, initial ? Brushes.ForestGreen : Brushes.MediumPurple), BorderThickness = new Thickness(initial ? 2.5 : 1.5), CornerRadius = new CornerRadius(8), Background = Brushes.White, Padding = new Thickness(8), Child = new StackPanel { Children = { title, detail } }, Tag = state.Id };
			var position = FlowPosition(state.Id, index);
			Canvas.SetLeft(card, position.X);
			Canvas.SetTop(card, position.Y);
			card.MouseLeftButtonDown += (_, e) => {
				if (pendingStateTransitionId != null) ConnectArmedStateTarget(state.Id);
				else selection.Select(new[] { state }, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
				e.Handled = true;
			};
			EnableFlowNodeDrag(graph, card, state);
			boxesById[state.Id] = card;
			graph.Children.Add(card);
		}
		return graph;
	}

	void AddFlowConnector(Canvas graph, Border box, DesignerElementNode node)
	{
		void AddHandle(double top, Brush color, bool? decisionBranch, string toolTip)
		{
			var handle = new Ellipse { Width = 12, Height = 12, Fill = color, Stroke = Brushes.White, StrokeThickness = 1, ToolTip = toolTip, Cursor = Cursors.Cross, Tag = node.Id };
			Canvas.SetLeft(handle, (Canvas.GetLeft(box) + box.Width - 6));
			Canvas.SetTop(handle, Canvas.GetTop(box) + top);
			handle.PreviewMouseLeftButtonDown += (_, e) => StartFlowConnectorDrag(graph, handle, node.Id, decisionBranch, e);
			graph.Children.Add(handle);
		}
		if (node.Type == "FlowDecision") {
			AddHandle(18, Brushes.ForestGreen, true, "Drag True branch to a target");
			AddHandle(42, Brushes.IndianRed, false, "Drag False branch to a target");
		} else AddHandle(30, Brushes.Teal, null, "Drag Next connection to a target");
	}

	void StartFlowConnectorDrag(Canvas graph, Ellipse handle, string sourceId, bool? branch, MouseButtonEventArgs start)
	{
		if (host == null) return;
		BeginFlowConnection(sourceId, branch);
		var origin = start.GetPosition(graph);
		var preview = new Line { X1 = origin.X, Y1 = origin.Y, X2 = origin.X, Y2 = origin.Y, Stroke = Brushes.DodgerBlue, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
		graph.Children.Add(preview);
		MouseEventHandler? move = null;
		MouseButtonEventHandler? release = null;
		move = (_, e) => { var point = e.GetPosition(graph); preview.X2 = point.X; preview.Y2 = point.Y; };
		release = (_, e) => {
			graph.MouseMove -= move;
			graph.MouseLeftButtonUp -= release;
			graph.ReleaseMouseCapture();
			graph.Children.Remove(preview);
			var targetId = NodeIdAt(graph, e.GetPosition(graph));
			if (!string.IsNullOrEmpty(targetId) && targetId != sourceId) ConnectArmedFlowTarget(targetId);
			else CancelFlowConnection();
			e.Handled = true;
		};
		graph.MouseMove += move;
		graph.MouseLeftButtonUp += release;
		graph.CaptureMouse();
		start.Handled = true;
	}

	static string? NodeIdAt(Canvas graph, Point point)
	{
		for (DependencyObject? current = graph.InputHitTest(point) as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current))
			if (current is FrameworkElement { Tag: string id }) return id;
		return null;
	}

	void DrawFlowEdge(Canvas graph, IReadOnlyList<DesignerElementNode> nodes, DesignerElementNode source, string propertyName, Brush color, string label)
	{
		var targetId = source.Properties.FirstOrDefault(property => property.Name == propertyName)?.Value;
		if (string.IsNullOrEmpty(targetId) || nodes.FirstOrDefault(node => node.Id == targetId) is not { } target) return;
		var from = FlowPosition(source.Id, 0);
		var to = FlowPosition(target.Id, 0);
		var line = new Line { X1 = from.X + 190, Y1 = from.Y + 32, X2 = to.X, Y2 = to.Y + 32, Stroke = color, StrokeThickness = 2, IsHitTestVisible = false };
		graph.Children.Add(line);
		DrawArrowHead(graph, line.X1, line.Y1, line.X2, line.Y2, color);
		var text = new TextBlock { Text = label, Foreground = color, FontSize = 10, IsHitTestVisible = false };
		Canvas.SetLeft(text, (line.X1 + line.X2) / 2 - 12);
		Canvas.SetTop(text, (line.Y1 + line.Y2) / 2 - 14);
		graph.Children.Add(text);
	}

	static void DrawArrowHead(Canvas graph, double fromX, double fromY, double toX, double toY, Brush color)
	{
		var dx = toX - fromX;
		var dy = toY - fromY;
		var length = Math.Sqrt(dx * dx + dy * dy);
		if (length < 1) return;
		var ux = dx / length;
		var uy = dy / length;
		var baseX = toX - ux * 10;
		var baseY = toY - uy * 10;
		var perpendicularX = -uy * 4;
		var perpendicularY = ux * 4;
		graph.Children.Add(new Polygon {
			Points = new PointCollection { new Point(toX, toY), new Point(baseX + perpendicularX, baseY + perpendicularY), new Point(baseX - perpendicularX, baseY - perpendicularY) },
			Fill = color,
			IsHitTestVisible = false
		});
	}

	WorkflowNodePosition FlowPosition(string id, int index)
	{
		if (flowNodePositions.TryGetValue(id, out var existing)) return existing;
		var position = new WorkflowNodePosition { X = 28 + (index % 3) * 205, Y = 28 + (index / 3) * 120 };
		flowNodePositions[id] = position;
		return position;
	}

	void EnableFlowNodeDrag(Canvas graph, Border box, DesignerElementNode node)
	{
		Point? origin = null;
		WorkflowNodePosition? start = null;
		box.PreviewMouseLeftButtonDown += (_, e) => {
			if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
			origin = e.GetPosition(graph);
			start = FlowPosition(node.Id, 0);
			box.CaptureMouse();
			e.Handled = true;
		};
		box.PreviewMouseMove += (_, e) => {
			if (origin == null || start == null || e.LeftButton != MouseButtonState.Pressed) return;
			var point = e.GetPosition(graph);
			var position = new WorkflowNodePosition {
				X = Math.Max(0, Math.Round((start.X + point.X - origin.Value.X) / 12) * 12),
				Y = Math.Max(0, Math.Round((start.Y + point.Y - origin.Value.Y) / 12) * 12)
			};
			flowNodePositions[node.Id] = position;
			Canvas.SetLeft(box, position.X);
			Canvas.SetTop(box, position.Y);
		};
		box.PreviewMouseLeftButtonUp += (_, e) => {
			if (origin == null) return;
			origin = null;
			start = null;
			box.ReleaseMouseCapture();
			SaveLayout();
			Rebuild();
			e.Handled = true;
		};
	}
}
