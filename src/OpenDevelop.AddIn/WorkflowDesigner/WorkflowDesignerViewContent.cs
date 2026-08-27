using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>Renders a loaded CoreWF activity tree as nested boxes on a WPF canvas - the
/// from-scratch design surface doc/technotes/workflow-designer.md settled on once it became
/// clear System.Activities.Design (the classic rehosted designer) has no path to .NET 10/
/// cross-platform. Modeled on MewUIDesignerViewContent's shape (load/select/edit/save against
/// an out-of-process host, DesignerSelectionController driving the Properties/Outline pads),
/// with shared command-driven undo/redo, toolbox drag/drop and workflow-level argument editing
/// layered on top - see the technote's phased plan.</summary>
public sealed class WorkflowDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer
{
	public static readonly string[] ToolNames = { "Sequence", "If", "WriteLine", "Assign", "Delay", "Parallel", "While", "Flowchart" };

	readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	readonly StackPanel surface = new() { Margin = new Thickness(24) };
	readonly DesignerCanvas canvas = new();
	readonly DocumentOutlineControl outline = new();
	readonly ListBox toolbox = new() { DisplayMemberPath = nameof(DesignerToolboxItemInfo.DisplayName) };
	readonly TextBox toolboxFilter = new() { Margin = new Thickness(4), ToolTip = "Filter activities" };
	readonly TextBlock toolboxSummary = new() { Margin = new Thickness(4, 2, 4, 0), Foreground = Brushes.DimGray };
	readonly DockPanel toolboxPane = new();
	readonly PropertyContainer properties = new();
	readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, ToolTip = "Workflow properties accept literals; compatible arguments also accept @variableName, =C#, or =vb: expression." };
	readonly OpenedFile xamlFile;
	readonly DesignerToolboxController toolboxModel = new();
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	readonly Dictionary<string, Border> boxesById = new();
	readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
	readonly Canvas overview = new() { Width = 150, Height = 72, Background = Brushes.Gainsboro, Margin = new Thickness(8, 2, 8, 2) };
	readonly Border overviewViewport = new() { BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), IsHitTestVisible = false };
	readonly HashSet<string> collapsed = new();
	readonly HashSet<string> breakpoints = new();
	readonly Dictionary<string, WorkflowNodePosition> flowNodePositions = new(StringComparer.Ordinal);

	WorkflowDesignerHostClient? host;
	DesignerSessionState state = new();
	DesignerElementNode? selected;
	string drillRootId = "";
	string loadedXamlText = "";
	double zoom = 1;
	bool awaitingWorkflowShortcut;
	string? pendingFlowSourceId;
	bool? pendingFlowDecisionBranch;

	string LayoutFileName => xamlFile.FileName + ".workflowdesigner.layout.json";
	sealed class WorkflowNodePosition { public double X { get; set; } public double Y { get; set; } }
	sealed class WorkflowLayoutFile {
		public Dictionary<string, WorkflowNodePosition> Nodes { get; set; } = new(StringComparer.Ordinal);
		public List<string> BreakpointElementIds { get; set; } = new();
	}

	public WorkflowDesignerViewContent(OpenedFile file) : base(file)
	{
		toolboxModel.SetItems(ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = "Workflow" }));
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		toolboxModel.ItemsChanged += (_, _) => RefreshToolboxView();
		toolboxFilter.TextChanged += (_, _) => toolboxModel.Filter(toolboxFilter.Text);
		DockPanel.SetDock(toolboxSummary, Dock.Top);
		DockPanel.SetDock(toolboxFilter, Dock.Top);
		toolboxPane.Children.Add(toolboxSummary);
		toolboxPane.Children.Add(toolboxFilter);
		toolboxPane.Children.Add(toolbox);
		RefreshToolboxView();
		toolbox.SelectionChanged += (_, _) => toolboxModel.Select((toolbox.SelectedItem as DesignerToolboxItemInfo)?.TypeName);
		toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) Add(item.TypeName); };
		toolbox.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && toolboxModel.SelectedItem is { } item) DragDrop.DoDragDrop(toolbox, item, DragDropEffects.Copy); };

		// Ordered multi-selection + shared pad/command bridge (designer-common.md's 2026-08-24
		// "push for more code reuse" pass) - matches MewUI/GTK4 wiring instead of the hand-rolled
		// TreeChanged/SelectionChanged subscriptions an earlier draft of this file used.
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => state.CanUndo && host?.IsAlive == true, Undo, () => state.CanRedo && host?.IsAlive == true, Redo,
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; HighlightSelection(); });
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);

		xamlFile = file;
		TabPageText = "Design";
		ConfigureCanvas();
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(breadcrumbs);
		breadcrumbs.Children.Add(overview);
		grid.Children.Add(canvas);
		Grid.SetRow(canvas, 1);
		Grid.SetRow(diagnostic, 2);
		grid.Children.Add(diagnostic);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => commands.Execute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => commands.Execute(DesignerCommandNames.Redo)));
		grid.PreviewKeyDown += (_, e) => {
			if (awaitingWorkflowShortcut) {
				awaitingWorkflowShortcut = false;
				switch (e.Key) {
					case Key.A: ShowArguments(); break;
					case Key.V: ShowVariables(); break;
					case Key.E: ShowExpressionEditor(); break;
					case Key.B: ToggleBreakpoint(); break;
					case Key.D: ContinueWorkflow(); break;
					case Key.S: StepWorkflow(); break;
					case Key.X: ExpandCurrent(); break;
					case Key.C: CollapseCurrent(); break;
					case Key.R: RestoreView(); break;
					default: return;
				}
				e.Handled = true;
				return;
			}
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.T) { toolboxFilter.Focus(); toolboxFilter.SelectAll(); e.Handled = true; return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.E) { awaitingWorkflowShortcut = true; e.Handled = true; }
			if (e.Key == Key.Escape && pendingFlowSourceId != null) { CancelFlowConnection(); e.Handled = true; }
		};
		UserContent = grid;
	}

	/// <summary>Wires up the shared zoom/fit toolbar every other designer backend uses
	/// (designer-common.md) - the closest in-repo equivalent to VS's shell bar
	/// (workflow-designer-shell-features.md), rather than a bespoke workflow-only chrome.
	/// Gridlines/Theme/ShowNames/DesignSize don't map to an activity tree, so only Zoom+Fit
	/// are enabled.</summary>
	void ConfigureCanvas()
	{
		canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit;
		foreach (var label in new[] { "Fit", "50%", "75%", "100%", "125%", "150%", "200%" }) canvas.ZoomCombo.Items.Add(label);
		canvas.ZoomCombo.SelectedIndex = 3;
		canvas.ZoomChanged += (_, _) => { if (canvas.ZoomCombo.SelectedIndex > 0) Zoom = new[] { .5, .75, 1, 1.25, 1.5, 2 }[canvas.ZoomCombo.SelectedIndex - 1]; };
		canvas.FitRequested += (_, _) => FitView();
		scroller.Content = surface;
		scroller.ScrollChanged += (_, _) => UpdateOverviewViewport();
		overview.MouseLeftButtonDown += (_, e) => {
			if (e.OriginalSource != overview || scroller.ExtentHeight <= scroller.ViewportHeight) return;
			var point = e.GetPosition(overview);
			var ratio = Math.Clamp(point.Y / overview.Height, 0, 1);
			scroller.ScrollToVerticalOffset(ratio * Math.Max(0, scroller.ExtentHeight - scroller.ViewportHeight));
			e.Handled = true;
		};
		surface.AllowDrop = true;
		surface.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		surface.Drop += (_, e) => { if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) Add(item.TypeName); e.Handled = true; };
		canvas.ContentHost.Content = scroller;
	}

	public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .25, 3); surface.LayoutTransform = new ScaleTransform(zoom, zoom); } }

	void FitView()
	{
		var width = surface.ActualWidth;
		var height = surface.ActualHeight;
		if (width > 0 && height > 0 && scroller.ViewportWidth > 0 && scroller.ViewportHeight > 0)
			Zoom = Math.Min(scroller.ViewportWidth / width, scroller.ViewportHeight / height);
		if (canvas.ZoomCombo.SelectedIndex != 0) canvas.ZoomCombo.SelectedIndex = 0;
	}

	void RefreshToolboxView()
	{
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		var project = toolboxModel.AllItems.Count(item => item.Category == "Project");
		var referenced = toolboxModel.AllItems.Count(item => item.Category == "Referenced activity");
		toolboxSummary.Text = $"{toolboxModel.VisibleItems.Count}/{toolboxModel.AllItems.Count} activities"
			+ (project + referenced == 0 ? "" : $" · project {project}, referenced {referenced}");
	}

	public object OutlineContent => outline;
	public object ToolsContent => toolboxPane;
	public PropertyContainer PropertyContainer => properties;
	public string Status => state.Accepted ? $"Ready: {ElementCount} activities (host {host?.ProcessId})" : state.Error;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count();
	public int HostProcessId => host?.ProcessId ?? 0;
	public int ToolboxItemCount => toolboxModel.AllItems.Count;
	public int ProjectToolboxItemCount => toolboxModel.AllItems.Count(item => item.Category == "Project");
	public int ReferencedToolboxItemCount => toolboxModel.AllItems.Count(item => item.Category == "Referenced activity");

	public bool Add(string type, string? targetParentId = null)
	{
		if (host == null || state.Tree == null) return false;
		var parentId = targetParentId ?? selected?.Id ?? state.Tree.Id;
		var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet();
		try {
			Mutate(() => host.AddElementAsync(state.Version, parentId, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult());
		} catch (Exception exception) {
			diagnostic.Text = $"Cannot insert {type}: {exception.Message}";
			return false;
		}
		var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id));
		if (added != null) selection.Select(added);
		else diagnostic.Text = $"Cannot insert {type}: the selected activity does not accept another child.";
		return added != null;
	}

	WorkflowPropertyAdapter Adapter(DesignerElementNode node) => new(node, (name, value) => SetProperty(node.Id, name, value));

	public bool SetSelectedProperty(string name, string value) => selected != null && SetProperty(selected.Id, name, value);

	bool SetProperty(string id, string name, string value)
	{
		if (host == null) return false;
		Mutate(() => host.SetPropertyAsync(state.Version, id, name, value).GetAwaiter().GetResult());
		return selection.Find(id) != null;
	}

	public bool DeleteSelected() => commands.Execute(DesignerCommandNames.Delete);

	bool DeleteSelectedCore()
	{
		if (selection.SelectedIds.Count == 0 || host == null) return false;
		var ids = selection.SelectedIds.ToArray();
		Mutate(() => host.DeleteElementsAsync(state.Version, ids).GetAwaiter().GetResult());
		return true;
	}

	bool Undo()
	{
		if (host == null) return false;
		Mutate(() => host.UndoAsync(state.Version).GetAwaiter().GetResult());
		return true;
	}

	bool Redo()
	{
		if (host == null) return false;
		Mutate(() => host.RedoAsync(state.Version).GetAwaiter().GetResult());
		return true;
	}

	void Mutate(Func<DesignerSessionState> action)
	{
		state = action();
		xamlFile.MakeDirty();
		Rebuild();
		commands.Invalidate();
	}

	void Rebuild()
	{
		diagnostic.Text = Status;
		pads.UpdateTree(state.Tree);
		boxesById.Clear();
		surface.Children.Clear();
		if (state.Tree != null) {
			var root = Find(state.Tree, drillRootId) ?? state.Tree;
			if (root == state.Tree) drillRootId = "";
			BuildBreadcrumbs(state.Tree, root);
			BuildOverview(state.Tree, root.Id);
			surface.Children.Add(Box(root));
		}
	}

	void BuildOverview(DesignerElementNode root, string currentId)
	{
		overview.Children.Clear();
		var nodes = Flatten(root).ToArray();
		for (var index = 0; index < nodes.Length; index++) {
			var node = nodes[index];
			var depth = node.Id.Length == 0 ? 0 : node.Id.Count(character => character == '.') + 1;
			var rectangle = new Border { Width = Math.Max(18, 62 - depth * 8), Height = 7, Background = node.Id == currentId ? Brushes.SteelBlue : Brushes.SlateGray, ToolTip = node.Name };
			Canvas.SetLeft(rectangle, 4 + depth * 12);
			Canvas.SetTop(rectangle, 4 + index * 62.0 / Math.Max(1, nodes.Length - 1));
			rectangle.MouseLeftButtonDown += (_, e) => { drillRootId = node.Id; Rebuild(); e.Handled = true; };
			overview.Children.Add(rectangle);
		}
		overview.Children.Add(overviewViewport);
		UpdateOverviewViewport();
	}

	void UpdateOverviewViewport()
	{
		if (!overview.Children.Contains(overviewViewport)) return;
		var extent = scroller.ExtentHeight;
		var viewport = scroller.ViewportHeight;
		if (extent <= 0 || viewport <= 0 || viewport >= extent) {
			overviewViewport.Visibility = Visibility.Collapsed;
			return;
		}
		overviewViewport.Visibility = Visibility.Visible;
		var height = Math.Max(6, overview.Height * viewport / extent);
		overviewViewport.Width = overview.Width - 2;
		overviewViewport.Height = height;
		Canvas.SetLeft(overviewViewport, 1);
		Canvas.SetTop(overviewViewport, (overview.Height - height) * scroller.VerticalOffset / Math.Max(1, extent - viewport));
	}

	static DesignerElementNode? Find(DesignerElementNode root, string id) => root.Id == id ? root : root.Children.Select(child => Find(child, id)).FirstOrDefault(value => value != null);

	void BuildBreadcrumbs(DesignerElementNode root, DesignerElementNode current)
	{
		breadcrumbs.Children.Clear();
		var path = current.Id.Length == 0 ? new[] { root } : new[] { root }.Concat(current.Id.Split('.').Select((_, i) => Find(root, string.Join('.', current.Id.Split('.').Take(i + 1)))!).Where(node => node != null));
		foreach (var node in path) {
			var button = new Button { Content = node.Name, Margin = new Thickness(2, 0, 2, 0), IsEnabled = node.Id != current.Id };
			button.Click += (_, _) => { drillRootId = node.Id; Rebuild(); };
			breadcrumbs.Children.Add(button);
		}
		var expand = new Button { Content = "Expand All", Margin = new Thickness(12, 0, 2, 0), ToolTip = "Ctrl+E, X" }; expand.Click += (_, _) => ExpandCurrent(); breadcrumbs.Children.Add(expand);
		var collapse = new Button { Content = "Collapse All", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Ctrl+E, C" }; collapse.Click += (_, _) => CollapseCurrent(); breadcrumbs.Children.Add(collapse);
		var restore = new Button { Content = "Restore", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Ctrl+E, R" }; restore.Click += (_, _) => RestoreView(); breadcrumbs.Children.Add(restore);
		var run = new Button { Content = "Run", Margin = new Thickness(8, 0, 2, 0), ToolTip = "Execute the current workflow" }; run.Click += (_, _) => RunWorkflow(); breadcrumbs.Children.Add(run);
		var stop = new Button { Content = "Stop", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Cancel the active workflow run" }; stop.Click += async (_, _) => await CancelWorkflow(); breadcrumbs.Children.Add(stop);
		var breakpoint = new Button { Content = "Breakpoint", Margin = new Thickness(8, 0, 2, 0), ToolTip = "Toggle a breakpoint on the selected activity (Ctrl+E, B)" }; breakpoint.Click += (_, _) => ToggleBreakpoint(); breadcrumbs.Children.Add(breakpoint);
		var resume = new Button { Content = "Continue", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Resume a paused workflow (Ctrl+E, D)" }; resume.Click += (_, _) => ContinueWorkflow(); breadcrumbs.Children.Add(resume);
		var step = new Button { Content = "Step", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Run to the next activity record (Ctrl+E, S)" }; step.Click += (_, _) => StepWorkflow(); breadcrumbs.Children.Add(step);
		var stepOver = new Button { Content = "Step Over", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Run to the next activity at this scope" }; stepOver.Click += (_, _) => StepOverWorkflow(); breadcrumbs.Children.Add(stepOver);
		var stepOut = new Button { Content = "Step Out", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Run until leaving this activity scope" }; stepOut.Click += (_, _) => StepOutWorkflow(); breadcrumbs.Children.Add(stepOut);
		var connect = new Button { Content = "Connect", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Arm selected Flowchart step, then click its target (Esc cancels)" }; connect.Click += (_, _) => BeginOrConnectFlowSteps(); breadcrumbs.Children.Add(connect);
		var connectTrue = new Button { Content = "True →", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Arm selected Flow Decision's True branch, then click its target" }; connectTrue.Click += (_, _) => BeginOrConnectFlowDecision(true); breadcrumbs.Children.Add(connectTrue);
		var connectFalse = new Button { Content = "False →", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Arm selected Flow Decision's False branch, then click its target" }; connectFalse.Click += (_, _) => BeginOrConnectFlowDecision(false); breadcrumbs.Children.Add(connectFalse);
		var clearNext = new Button { Content = "Clear Next", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Disconnect selected Flowchart step's Next edge" }; clearNext.Click += (_, _) => DisconnectSelectedFlow("Next"); breadcrumbs.Children.Add(clearNext);
		var clearTrue = new Button { Content = "Clear True", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Disconnect selected Flow Decision's True edge" }; clearTrue.Click += (_, _) => DisconnectSelectedFlow("True"); breadcrumbs.Children.Add(clearTrue);
		var clearFalse = new Button { Content = "Clear False", Margin = new Thickness(2, 0, 2, 0), ToolTip = "Disconnect selected Flow Decision's False edge" }; clearFalse.Click += (_, _) => DisconnectSelectedFlow("False"); breadcrumbs.Children.Add(clearFalse);
		var autoLayout = new Button { Content = "Auto Layout", Margin = new Thickness(8, 0, 2, 0), ToolTip = "Reset Flowchart node positions to the default grid" }; autoLayout.Click += (_, _) => AutoLayoutFlowcharts(); breadcrumbs.Children.Add(autoLayout);
		var expression = new Button { Content = "Expression…", Margin = new Thickness(8, 0, 2, 0), ToolTip = "Edit an expression for the selected activity (Ctrl+E, E)" }; expression.Click += (_, _) => ShowExpressionEditor(); breadcrumbs.Children.Add(expression);
		var arguments = new Button { Content = "Arguments", Margin = new Thickness(8, 0, 2, 0) }; arguments.Click += (_, _) => ShowArguments(); breadcrumbs.Children.Add(arguments);
		var variables = new Button { Content = "Variables", Margin = new Thickness(2, 0, 2, 0) }; variables.Click += (_, _) => ShowVariables(); breadcrumbs.Children.Add(variables);
		breadcrumbs.Children.Add(overview);
	}

	void ExpandCurrent()
	{
		collapsed.Clear();
		Rebuild();
	}

	void CollapseCurrent()
	{
		if (state.Tree == null) return;
		var current = Find(state.Tree, drillRootId) ?? state.Tree;
		foreach (var node in Flatten(current).Where(node => node.Children.Count > 0)) collapsed.Add(node.Id);
		Rebuild();
	}

	void RestoreView()
	{
		drillRootId = "";
		collapsed.Clear();
		Rebuild();
	}

	async void RunWorkflow()
	{
		if (host == null) return;
		try {
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = "Workflow running…";
			var running = host.RunAsync(state.Version);
			while (!running.IsCompleted) {
				ApplyDebugState(await host.GetDebugStateAsync());
				await Task.Delay(100);
			}
			var result = await running;
			diagnostic.Foreground = result.Succeeded ? Brushes.ForestGreen : Brushes.OrangeRed;
			diagnostic.Text = result.Message
				+ (result.Outputs.Count == 0 ? "" : " " + string.Join(", ", result.Outputs.Select(output => output.Key + "=" + output.Value)))
				+ (result.Trace.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Trace.Take(12)) + (result.Trace.Count > 12 ? Environment.NewLine + "…" : ""));
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Workflow run failed: " + exception.Message;
		}
	}

	async void ToggleBreakpoint()
	{
		if (host == null || selected == null) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select an activity to toggle its breakpoint.";
			return;
		}
		try {
			ApplyDebugState(await host.ToggleBreakpointAsync(selected.Id));
			SaveLayout();
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = breakpoints.Contains(selected.Id) ? "Breakpoint added." : "Breakpoint removed.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Could not change breakpoint: " + exception.Message;
		}
	}

	async void ContinueWorkflow() => await ControlWorkflow(host == null ? null : () => host.ContinueRunAsync(), "Workflow resumed.");
	async void StepWorkflow() => await ControlWorkflow(host == null ? null : () => host.StepRunAsync(), "Workflow will pause at the next activity.");
	async void StepOverWorkflow() => await ControlWorkflow(host == null ? null : () => host.StepOverRunAsync(), "Workflow will pause at the next activity in this scope.");
	async void StepOutWorkflow() => await ControlWorkflow(host == null ? null : () => host.StepOutRunAsync(), "Workflow will pause after leaving this activity scope.");

	async Task ControlWorkflow(Func<Task<bool>>? command, string message)
	{
		if (command == null) return;
		try {
			if (!await command()) {
				diagnostic.Foreground = Brushes.OrangeRed;
				diagnostic.Text = "No paused workflow is available.";
				return;
			}
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = message;
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Workflow debug command failed: " + exception.Message;
		}
	}

	void ApplyDebugState(WorkflowDebugState debug)
	{
		breakpoints.Clear();
		foreach (var id in debug.BreakpointElementIds) breakpoints.Add(id);
		HighlightSelection();
		if (debug.IsPaused) {
			diagnostic.Foreground = Brushes.DarkOrange;
			diagnostic.Text = "Paused at " + (string.IsNullOrEmpty(debug.CurrentElementId) ? "an activity" : debug.CurrentElementId) + ". Continue or Step to resume.";
			if (!string.IsNullOrEmpty(debug.CurrentElementId) && state.Tree != null && Find(state.Tree, debug.CurrentElementId) is { } node)
				selection.Select(node);
		}
	}

	async Task CancelWorkflow()
	{
		if (host == null) return;
		try {
			if (await host.CancelRunAsync()) {
				diagnostic.Foreground = Brushes.DodgerBlue;
				diagnostic.Text = "Cancelling workflow…";
			} else {
				diagnostic.Foreground = Brushes.OrangeRed;
				diagnostic.Text = "No active workflow run to cancel.";
			}
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Could not cancel workflow: " + exception.Message;
		}
	}

	void BeginOrConnectFlowSteps()
	{
		if (selection.SelectedIds.Count == 1) {
			BeginFlowConnection(selection.SelectedIds.Single(), null);
			return;
		}
		if (host == null || selection.SelectedIds.Count != 2) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a Flowchart step, then click Connect and its target; Ctrl-clicking two nodes still works.";
			return;
		}
		try {
			var ids = selection.SelectedIds.ToArray();
			Mutate(() => host.ConnectFlowAsync(state.Version, ids[0], ids[1]).GetAwaiter().GetResult());
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "Flowchart connection updated.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
	}

	void BeginOrConnectFlowDecision(bool trueBranch)
	{
		if (selection.SelectedIds.Count == 1) {
			BeginFlowConnection(selection.SelectedIds.Single(), trueBranch);
			return;
		}
		if (host == null || selection.SelectedIds.Count != 2) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a Flow Decision, then click True/False and its target; Ctrl-clicking two nodes still works.";
			return;
		}
		try {
			var ids = selection.SelectedIds.ToArray();
			var source = ids.FirstOrDefault(id => id.Contains("$decision-", StringComparison.Ordinal));
			var target = ids.FirstOrDefault(id => id != source);
			if (source == null || target == null) throw new InvalidOperationException("One selected item must be a Flow Decision.");
			Mutate(() => host.ConnectFlowDecisionAsync(state.Version, source, target, trueBranch).GetAwaiter().GetResult());
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "Flow Decision " + (trueBranch ? "True" : "False") + " branch updated.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
	}

	void BeginFlowConnection(string sourceId, bool? decisionBranch)
	{
		if (host == null) return;
		if (decisionBranch.HasValue && !sourceId.Contains("$decision-", StringComparison.Ordinal)) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "True/False connections must start from a Flow Decision.";
			return;
		}
		pendingFlowSourceId = sourceId;
		pendingFlowDecisionBranch = decisionBranch;
		diagnostic.Foreground = Brushes.DodgerBlue;
		diagnostic.Text = "Connection armed. Click a target Flowchart step (Esc cancels).";
	}

	void ConnectArmedFlowTarget(string targetId)
	{
		if (host == null || pendingFlowSourceId == null || targetId == pendingFlowSourceId) return;
		try {
			var source = pendingFlowSourceId;
			var branch = pendingFlowDecisionBranch;
			Mutate(() => branch.HasValue
				? host.ConnectFlowDecisionAsync(state.Version, source, targetId, branch.Value).GetAwaiter().GetResult()
				: host.ConnectFlowAsync(state.Version, source, targetId).GetAwaiter().GetResult());
			pendingFlowSourceId = null;
			pendingFlowDecisionBranch = null;
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "Flowchart connection updated.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
	}

	void CancelFlowConnection()
	{
		pendingFlowSourceId = null;
		pendingFlowDecisionBranch = null;
		diagnostic.Foreground = Brushes.DodgerBlue;
		diagnostic.Text = "Flowchart connection cancelled.";
	}

	void DisconnectSelectedFlow(string branch)
	{
		if (host == null || selected == null) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a Flowchart step or Flow Decision first.";
			return;
		}
		try {
			Mutate(() => host.DisconnectFlowAsync(state.Version, selected.Id, branch).GetAwaiter().GetResult());
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "Flowchart edge disconnected.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
	}

	void AutoLayoutFlowcharts()
	{
		flowNodePositions.Clear();
		Rebuild();
		SaveLayout();
		diagnostic.Foreground = Brushes.DodgerBlue;
		diagnostic.Text = "Flowchart layout reset.";
	}

	void ShowExpressionEditor()
	{
		if (selected == null) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select an activity or Flow Decision first.";
			return;
		}
		var editable = selected.Properties.Where(property => !property.IsReadOnly && property.Name != "$displayName").ToArray();
		if (editable.Length == 0) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "The selected item has no editable expression properties.";
			return;
		}
		var propertiesList = new ComboBox { Margin = new Thickness(8), ItemsSource = editable, DisplayMemberPath = nameof(DesignerPropertyInfo.DisplayName), SelectedIndex = 0 };
		var editor = new TextBox { Margin = new Thickness(8), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 110 };
		var targetStates = new ComboBox { Margin = new Thickness(8), DisplayMemberPath = nameof(DesignerElementNode.Name), SelectedValuePath = nameof(DesignerElementNode.Id), Visibility = Visibility.Collapsed, ToolTip = "Target State" };
		var transitionMarker = selected.Id.LastIndexOf(".$transition-", StringComparison.Ordinal);
		var stateMarker = transitionMarker < 0 ? -1 : selected.Id.LastIndexOf("$state-", transitionMarker, StringComparison.Ordinal);
		var stateMachinePrefix = stateMarker < 0 ? "\0" : selected.Id[..stateMarker];
		targetStates.ItemsSource = state.Tree == null ? Array.Empty<DesignerElementNode>() : Flatten(state.Tree).Where(node => node.Type == "State" && node.Id.StartsWith(stateMachinePrefix, StringComparison.Ordinal) && node.Id.IndexOf("$state-", stateMachinePrefix.Length, StringComparison.Ordinal) == stateMachinePrefix.Length).ToArray();
		var suggestions = new ComboBox { Margin = new Thickness(8, 0, 8, 4), Visibility = Visibility.Collapsed, ToolTip = "Select a symbol to insert" };
		void LoadValue() { if (propertiesList.SelectedItem is DesignerPropertyInfo property) { editor.Text = property.Value; var isTarget = property.Name == "$target"; editor.Visibility = isTarget ? Visibility.Collapsed : Visibility.Visible; targetStates.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed; targetStates.SelectedValue = property.Value; } }
		propertiesList.SelectionChanged += (_, _) => LoadValue();
		LoadValue();
		var hint = new TextBlock { Margin = new Thickness(8, 0, 8, 8), Foreground = Brushes.DimGray, Text = "Use a literal, @variableName, =C#, or =vb: expression." };
		var validation = new TextBlock { Margin = new Thickness(8, 0, 8, 8), TextWrapping = TextWrapping.Wrap };
		var save = new Button { Content = "Apply", Margin = new Thickness(8), MinWidth = 75 };
		var validate = new Button { Content = "Validate", Margin = new Thickness(8), MinWidth = 75 };
		var complete = new Button { Content = "Complete", Margin = new Thickness(8), MinWidth = 75, ToolTip = "Suggest variables, CoreWF types and language terms (Ctrl+Space)" };
		var cancel = new Button { Content = "Cancel", Margin = new Thickness(8), MinWidth = 75 };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(cancel); buttons.Children.Add(complete); buttons.Children.Add(validate); buttons.Children.Add(save);
		var panel = new DockPanel(); DockPanel.SetDock(propertiesList, Dock.Top); DockPanel.SetDock(targetStates, Dock.Top); DockPanel.SetDock(suggestions, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom); DockPanel.SetDock(validation, Dock.Bottom); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(propertiesList); panel.Children.Add(targetStates); panel.Children.Add(suggestions); panel.Children.Add(hint); panel.Children.Add(validation); panel.Children.Add(buttons); panel.Children.Add(editor);
		var window = new Window { Title = "Workflow Expression", Width = 520, Height = 280, Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject) };
		cancel.Click += (_, _) => window.Close();
		async Task CompleteAtCaret()
		{
			if (host == null) return;
			var caret = editor.CaretIndex;
			var start = caret;
			while (start > 0 && (char.IsLetterOrDigit(editor.Text[start - 1]) || editor.Text[start - 1] == '_')) start--;
			var prefix = editor.Text[start..caret];
			try {
				var result = await host.CompleteExpressionAsync(editor.Text, prefix, selected.Id);
				suggestions.ItemsSource = result.Items;
				suggestions.Visibility = result.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
				suggestions.Tag = new Tuple<int, int>(start, caret - start);
				if (result.Items.Count > 0) suggestions.IsDropDownOpen = true;
			} catch (Exception exception) { validation.Foreground = Brushes.OrangeRed; validation.Text = "Completion failed: " + exception.Message; }
		}
		validate.Click += async (_, _) => {
			if (host == null) return;
			try {
				var result = await host.ValidateExpressionAsync(editor.Text, selected.Id);
				validation.Foreground = result.IsValid ? Brushes.ForestGreen : Brushes.OrangeRed;
				validation.Text = result.IsValid ? "Syntax is valid." : string.Join(Environment.NewLine, result.Diagnostics);
			} catch (Exception exception) {
				validation.Foreground = Brushes.OrangeRed;
				validation.Text = "Validation failed: " + exception.Message;
			}
		};
		complete.Click += async (_, _) => await CompleteAtCaret();
		editor.PreviewKeyDown += async (_, e) => {
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Space) { await CompleteAtCaret(); e.Handled = true; }
		};
		suggestions.SelectionChanged += (_, _) => {
			if (suggestions.SelectedItem is not string item || suggestions.Tag is not Tuple<int, int> range) return;
			editor.Text = editor.Text.Remove(range.Item1, range.Item2).Insert(range.Item1, item);
			editor.CaretIndex = range.Item1 + item.Length;
			suggestions.Visibility = Visibility.Collapsed;
		};
		save.Click += (_, _) => {
			if (propertiesList.SelectedItem is DesignerPropertyInfo property && SetProperty(selected.Id, property.Name, property.Name == "$target" ? targetStates.SelectedValue as string ?? "" : editor.Text)) window.Close();
		};
		window.ShowDialog();
	}

	void ShowArguments()
	{
		if (host == null) return;
		var rows = host.GetArgumentsAsync().GetAwaiter().GetResult();
		var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(8) };
		grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Direction", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.Direction)), Width = new DataGridLength(80) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.TypeName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Default", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.DefaultValue)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		var name = new TextBox { Width = 130, Margin = new Thickness(4), ToolTip = "Argument name" };
		var type = new TextBox { Width = 110, Margin = new Thickness(4), Text = "String", ToolTip = "Argument type" };
		var direction = new ComboBox { Width = 75, Margin = new Thickness(4), ItemsSource = new[] { "In", "Out", "InOut" }, SelectedIndex = 0, ToolTip = "Argument direction" };
		var defaultValue = new TextBox { Width = 110, Margin = new Thickness(4), ToolTip = "Literal default, =C#, or =vb: expression (In only)" };
		var add = new Button { Content = "Create Argument", Margin = new Thickness(4) };
		var error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
		void Refresh() { rows = host.GetArgumentsAsync().GetAwaiter().GetResult(); grid.ItemsSource = rows; }
		bool RunMutation(Func<DesignerSessionState> action) { try { error.Text = ""; Mutate(action); Refresh(); return true; } catch (Exception exception) { error.Text = exception.Message; diagnostic.Text = exception.Message; return false; } }
		add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && RunMutation(() => host.AddArgumentAsync(state.Version, name.Text, type.Text, defaultValue.Text, direction.SelectedItem as string ?? "In").GetAwaiter().GetResult())) { name.Clear(); defaultValue.Clear(); } };
		var remove = new Button { Content = "Delete Selected", Margin = new Thickness(4) };
		remove.Click += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument) RunMutation(() => host.RemoveArgumentAsync(state.Version, argument.Name).GetAwaiter().GetResult()); };
		var update = new Button { Content = "Update Selected", Margin = new Thickness(4) };
		update.Click += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument && !string.IsNullOrWhiteSpace(name.Text)) RunMutation(() => host.UpdateArgumentAsync(state.Version, argument.Name, name.Text, type.Text, defaultValue.Text, direction.SelectedItem as string ?? "In").GetAwaiter().GetResult()); };
		grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument) { name.Text = argument.Name; type.Text = argument.TypeName; direction.SelectedItem = argument.Direction; defaultValue.Text = argument.DefaultValue; } };
		grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && grid.SelectedItem is WorkflowArgumentInfo argument) { RunMutation(() => host.RemoveArgumentAsync(state.Version, argument.Name).GetAwaiter().GetResult()); e.Handled = true; } };
		var panel = new DockPanel(); var bar = new StackPanel { Orientation = Orientation.Horizontal }; bar.Children.Add(name); bar.Children.Add(direction); bar.Children.Add(type); bar.Children.Add(defaultValue); bar.Children.Add(add); bar.Children.Add(update); bar.Children.Add(remove); bar.Children.Add(error); DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(grid);
		new Window { Title = "Workflow Arguments", Width = 540, Height = 320, Content = panel }.ShowDialog();
	}

	void ShowVariables()
	{
		if (host == null) return;
		var rows = host.GetVariablesAsync().GetAwaiter().GetResult();
		var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(8) };
		grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.TypeName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Scope", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.Scope)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Default", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.DefaultValue)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		var name = new TextBox { Width = 130, Margin = new Thickness(4), ToolTip = "Variable name" };
		var type = new TextBox { Width = 110, Margin = new Thickness(4), Text = "String", ToolTip = "Variable type" };
		var defaultValue = new TextBox { Width = 110, Margin = new Thickness(4), ToolTip = "Literal default, =C#, or =vb: expression (optional)" };
		var scope = new ComboBox { Width = 130, Margin = new Thickness(4), DisplayMemberPath = nameof(WorkflowVariableInfo.Scope), SelectedValuePath = nameof(WorkflowVariableInfo.ScopeId), ToolTip = "Activity or State scope for this variable" };
		var scopeRows = host.GetVariableScopesAsync().GetAwaiter().GetResult();
		scope.ItemsSource = scopeRows;
		scope.SelectedValue = selected?.Id ?? "";
		var add = new Button { Content = "Create Variable", Margin = new Thickness(4) };
		var update = new Button { Content = "Update Selected", Margin = new Thickness(4) };
		var remove = new Button { Content = "Delete Selected", Margin = new Thickness(4) };
		var error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
		void Refresh() { rows = host.GetVariablesAsync().GetAwaiter().GetResult(); grid.ItemsSource = rows; }
		bool RunMutation(Func<DesignerSessionState> action) { try { error.Text = ""; Mutate(action); Refresh(); return true; } catch (Exception exception) { error.Text = exception.Message; diagnostic.Text = exception.Message; return false; } }
		add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && RunMutation(() => host.AddVariableAsync(state.Version, name.Text, type.Text, defaultValue.Text, scope.SelectedValue as string ?? "").GetAwaiter().GetResult())) { name.Clear(); defaultValue.Clear(); } };
		remove.Click += (_, _) => { if (grid.SelectedItem is WorkflowVariableInfo variable) RunMutation(() => host.RemoveVariableAsync(state.Version, variable.Name, variable.ScopeId).GetAwaiter().GetResult()); };
		update.Click += (_, _) => { if (grid.SelectedItem is WorkflowVariableInfo variable && !string.IsNullOrWhiteSpace(name.Text)) RunMutation(() => host.UpdateVariableAsync(state.Version, variable.Name, name.Text, type.Text, defaultValue.Text, variable.ScopeId).GetAwaiter().GetResult()); };
		grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is WorkflowVariableInfo variable) { name.Text = variable.Name; type.Text = variable.TypeName; defaultValue.Text = variable.DefaultValue; scope.SelectedValue = variable.ScopeId; } };
		grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && grid.SelectedItem is WorkflowVariableInfo variable) { RunMutation(() => host.RemoveVariableAsync(state.Version, variable.Name, variable.ScopeId).GetAwaiter().GetResult()); e.Handled = true; } };
		var panel = new DockPanel(); var bar = new StackPanel { Orientation = Orientation.Horizontal }; bar.Children.Add(name); bar.Children.Add(type); bar.Children.Add(scope); bar.Children.Add(defaultValue); bar.Children.Add(add); bar.Children.Add(update); bar.Children.Add(remove); bar.Children.Add(error); DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(grid);
		new Window { Title = "Workflow Variables", Width = 680, Height = 320, Content = panel }.ShowDialog();
	}

	/// <summary>Activity types with a child-activity collection (Sequence.Activities and
	/// friends) - the ones whose empty body should show VS's "Drop activity here" hint text
	/// rather than rendering as blank (how-to-add-activities-to-the-toolbox.md's toolbox
	/// convention). Leaf activities (WriteLine, Delay, ...) never show it.</summary>
	static bool IsContainer(string type) => type is "Sequence" or "Parallel" or "While" or "DoWhile" or "If" or "TryCatch" or "Flowchart" or "StateMachine" or "State" or "Transition";

	Border Box(DesignerElementNode node)
	{
		var stack = new StackPanel();
		stack.Children.Add(HeaderLabel(node));
		if (node.Children.Count == 0 && IsContainer(node.Type))
			stack.Children.Add(new TextBlock { Text = node.Type == "Flowchart" ? "Drop the first flow step here" : "Drop activity here", FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
		if (!collapsed.Contains(node.Id)) {
			if (node.Type == "Flowchart") stack.Children.Add(FlowchartGraph(node));
			else foreach (var child in node.Children) stack.Children.Add(Box(child));
		}
		var border = new Border {
			BorderBrush = node.Type == "Flowchart" ? Brushes.Teal : Brushes.SlateGray, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
			Background = Brushes.White, Margin = new Thickness(4), Padding = new Thickness(8), Child = stack
		};
		border.AllowDrop = true;
		border.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		border.Drop += (_, e) => { if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) Add(item.TypeName, node.Id); e.Handled = true; };
		border.MouseLeftButtonDown += (_, e) => {
			if (pendingFlowSourceId != null && node.Id != pendingFlowSourceId) {
				ConnectArmedFlowTarget(node.Id);
				e.Handled = true;
				return;
			}
			var toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
			selection.Select(new[] { node }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
			e.Handled = true;
		};
		boxesById[node.Id] = border;
		return border;
	}

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
		var text = new TextBlock { Text = label, Foreground = color, FontSize = 10, IsHitTestVisible = false };
		Canvas.SetLeft(text, (line.X1 + line.X2) / 2 - 12);
		Canvas.SetTop(text, (line.Y1 + line.Y2) / 2 - 14);
		graph.Children.Add(text);
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

	/// <summary>DisplayName header label - double-click swaps it for a TextBox that commits on
	/// Enter/lost-focus, matching sequence-activity-designer.md's "the value can be edited in the
	/// property grid or directly on the header of the activity designer".</summary>
	FrameworkElement HeaderLabel(DesignerElementNode node)
	{
		var label = new TextBlock { Text = $"{node.Type}" + (string.IsNullOrEmpty(node.Name) || node.Name == node.Type ? "" : $" \"{node.Name}\""), FontWeight = FontWeights.Bold };
		label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) BeginEditHeader(node, label); else { drillRootId = node.Id; Rebuild(); } e.Handled = true; } };
		if (node.Children.Count == 0) return label;
		var header = new StackPanel { Orientation = Orientation.Horizontal };
		var chevron = new Button { Content = collapsed.Contains(node.Id) ? "›" : "⌄", Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), ToolTip = collapsed.Contains(node.Id) ? "Expand" : "Collapse" };
		chevron.Click += (_, _) => { if (!collapsed.Add(node.Id)) collapsed.Remove(node.Id); Rebuild(); };
		header.Children.Add(chevron);
		header.Children.Add(label);
		return header;
	}

	void BeginEditHeader(DesignerElementNode node, TextBlock label)
	{
		if (label.Parent is not Panel parent) return;
		var index = parent.Children.IndexOf(label);
		var editor = new TextBox { Text = node.Name ?? "", MinWidth = 80 };
		void Commit()
		{
			parent.Children.RemoveAt(index);
			parent.Children.Insert(index, HeaderLabel(node));
			if (editor.Text != node.Name) SetProperty(node.Id, "$displayName", editor.Text);
		}
		editor.LostFocus += (_, _) => Commit();
		editor.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) { Keyboard.ClearFocus(); e.Handled = true; }
			else if (e.Key == Key.Escape) { editor.Text = node.Name ?? ""; Keyboard.ClearFocus(); e.Handled = true; }
		};
		parent.Children.RemoveAt(index);
		parent.Children.Insert(index, editor);
		editor.Focus();
		editor.SelectAll();
	}

	void HighlightSelection()
	{
		foreach (var pair in boxesById) pair.Value.Background = breakpoints.Contains(pair.Key) ? Brushes.MistyRose : Brushes.White;
		foreach (var id in selection.SelectedIds)
			if (boxesById.TryGetValue(id, out var selectedBox))
				selectedBox.Background = Brushes.LightSteelBlue;
	}

	static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));

	DesignerDocumentSnapshot Snapshot(string text, long version)
	{
		var project = SD.ProjectService.FindProjectContainingFile(xamlFile.FileName);
		var output = project?.OutputAssemblyFullPath.ToString() ?? "";
		var managedOutput = string.IsNullOrEmpty(output) || output.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? output : Path.ChangeExtension(output, ".dll");
		var snapshot = new DesignerDocumentSnapshot {
		Version = version, ProjectFileName = project?.FileName.ToString() ?? "", TargetFramework = (project as MSBuildBasedProject)?.GetEvaluatedProperty("TargetFramework") ?? "",
		ProjectAssemblyPath = File.Exists(managedOutput) ? managedOutput : "", PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", DesignerFileName = xamlFile.FileName.ToString(),
		Files = { new DesignerSourceFileSnapshot { FileName = xamlFile.FileName.ToString(), Kind = "Designer", Text = text } }
		};
		// Project references that can contribute custom Activities are normally copy-local beside
		// the built project output. Enumerating that directory is cheap and deterministic, unlike
		// invoking ResolveAssemblyReferences on the workbench dispatcher. The child still owns all
		// assembly loading and filters every candidate to safe constructible Activity types.
		var outputDirectory = Path.GetDirectoryName(snapshot.ProjectAssemblyPath);
		if (!string.IsNullOrEmpty(outputDirectory) && Directory.Exists(outputDirectory))
			snapshot.ReferencedAssemblyPaths.AddRange(Directory.EnumerateFiles(outputDirectory, "*.dll")
				.Where(path => !string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase)));
		return snapshot;
	}

	void LoadLayout()
	{
		flowNodePositions.Clear();
		breakpoints.Clear();
		try {
			if (!File.Exists(LayoutFileName)) return;
			var layout = JsonSerializer.Deserialize<WorkflowLayoutFile>(File.ReadAllText(LayoutFileName));
			if (layout == null) return;
			foreach (var item in layout.Nodes) flowNodePositions[item.Key] = item.Value;
			foreach (var id in layout.BreakpointElementIds) breakpoints.Add(id);
		} catch (Exception exception) {
			diagnostic.Text = "Workflow layout could not be read: " + exception.Message;
		}
	}

	void SaveLayout()
	{
		try {
			File.WriteAllText(LayoutFileName, JsonSerializer.Serialize(new WorkflowLayoutFile {
				Nodes = flowNodePositions,
				BreakpointElementIds = breakpoints.OrderBy(id => id, StringComparer.Ordinal).ToList()
			}, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception exception) {
			diagnostic.Text = "Workflow layout could not be saved: " + exception.Message;
		}
	}

	protected override void LoadInternal(OpenedFile file, Stream stream)
	{
		using var reader = new StreamReader(stream, leaveOpen: true);
		loadedXamlText = reader.ReadToEnd();
		LoadLayout();
		if (host == null) host = WorkflowDesignerHostClient.CreateAsync().GetAwaiter().GetResult();
		state = host.OpenAsync(Snapshot(loadedXamlText, 1)).GetAwaiter().GetResult();
		// Breakpoints are user-facing design metadata, not CoreWF runtime/XAML state. Restore the
		// sidecar's structural paths only after the host has reconstructed the activity tree.
		foreach (var id in breakpoints.ToArray()) {
			try { host.ToggleBreakpointAsync(id).GetAwaiter().GetResult(); }
			catch { breakpoints.Remove(id); }
		}
		try {
			var capabilities = host.GetCapabilitiesAsync().GetAwaiter().GetResult();
			if (capabilities.Toolbox.Count > 0) toolboxModel.SetItems(capabilities.Toolbox);
		} catch (Exception exception) {
			diagnostic.Text = "Using fallback toolbox: " + exception.Message;
		}
		Rebuild();
		OutputChannel.Write("WorkflowDesigner", $"Host started (PID {host.ProcessId}) for {xamlFile.FileName}");
	}

	protected override void SaveInternal(OpenedFile file, Stream stream)
	{
		var text = host == null ? loadedXamlText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text;
		using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
		writer.Write(text);
		writer.Flush();
		loadedXamlText = text;
		SaveLayout();
	}

	public override void Dispose()
	{
		properties.Clear();
		OutputChannel.Write("WorkflowDesigner", "Designer view disposed");
		host?.Dispose();
		base.Dispose();
	}
}
