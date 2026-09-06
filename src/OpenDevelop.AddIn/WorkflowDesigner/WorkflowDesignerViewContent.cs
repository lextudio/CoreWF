using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;

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
	readonly ListBox toolbox = new();
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
	// The command buttons live on a real toolbar built ONCE (BuildToolBar), above the breadcrumb
	// trail. They used to be appended to `breadcrumbs` inside BuildBreadcrumbs, which rebuilt ~26
	// buttons on every Rebuild() and mixed fixed commands into what should only be the drill path.
	readonly ToolBarTray toolBarTray = new() { IsLocked = true };
	readonly ToolBar toolBar = new();
	readonly Canvas overview = new() { Width = 150, Height = 72, Background = Brushes.Gainsboro, Margin = new Thickness(8, 2, 8, 2) };
	readonly Border overviewViewport = new() { BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), IsHitTestVisible = false };
	readonly HashSet<string> collapsed = new();
	readonly HashSet<string> breakpoints = new();
	readonly Dictionary<string, WorkflowNodePosition> flowNodePositions = new(StringComparer.Ordinal);
	readonly Dictionary<string, bool> validationMarkers = new(StringComparer.Ordinal);
	readonly System.Windows.Threading.DispatcherTimer validationTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

	WorkflowDesignerHostClient? host;
	DesignerSessionState state = new();
	DesignerElementNode? selected;
	string drillRootId = "";
	string loadedXamlText = "";
	double zoom = 1;
	bool awaitingWorkflowShortcut;
	string? pendingFlowSourceId;
	bool? pendingFlowDecisionBranch;
	string? pendingStateTransitionId;
	Task<WorkflowRunResult>? runningWorkflow;
	readonly EventHandler<BuildEventArgs> buildFinishedHandler;

	string LayoutFileName => xamlFile.FileName + ".workflowdesigner.layout.json";
	sealed class WorkflowNodePosition { public double X { get; set; } public double Y { get; set; } }
	sealed class WorkflowLayoutFile {
		public Dictionary<string, WorkflowNodePosition> Nodes { get; set; } = new(StringComparer.Ordinal);
		public List<string> BreakpointElementIds { get; set; } = new();
	}
	sealed class WorkflowValidationListItem {
		public WorkflowValidationDiagnostic Diagnostic { get; init; } = new();
		public string Severity => Diagnostic.IsWarning ? "Warning" : "Error";
		public string Location => string.IsNullOrWhiteSpace(Diagnostic.ElementId) ? "Workflow" : Diagnostic.ElementId + (string.IsNullOrWhiteSpace(Diagnostic.PropertyName) ? "" : "." + Diagnostic.PropertyName);
		public string Text => Severity + " · " + Location + " · " + Diagnostic.Message;
	}
	sealed class WorkflowSearchItem {
		public DesignerElementNode Node { get; init; } = new();
		public string Text => Node.Type + " · " + (string.IsNullOrWhiteSpace(Node.Name) ? "(unnamed)" : Node.Name) + " · " + (string.IsNullOrEmpty(Node.Id) ? "Workflow" : Node.Id);
	}

	public WorkflowDesignerViewContent(OpenedFile file) : base(file)
	{
		validationTimer.Tick += async (_, _) => { validationTimer.Stop(); await RefreshWorkflowValidationAsync(); };
		toolbox.ItemTemplate = CreateToolboxItemTemplate();
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
		toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) AddToolboxItem(item); };
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
		buildFinishedHandler = BuildFinished;
		SD.BuildService.BuildFinished += buildFinishedHandler;
		TabPageText = "Design";
		ConfigureCanvas();
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // breadcrumb trail
		grid.RowDefinitions.Add(new RowDefinition());                              // canvas
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // diagnostics
		BuildToolBar();
		toolBarTray.ToolBars.Add(toolBar);
		grid.Children.Add(toolBarTray);
		grid.Children.Add(breadcrumbs);
		Grid.SetRow(breadcrumbs, 1);
		breadcrumbs.Children.Add(overview);
		grid.Children.Add(canvas);
		Grid.SetRow(canvas, 2);
		Grid.SetRow(diagnostic, 3);
		grid.Children.Add(diagnostic);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => commands.Execute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => commands.Execute(DesignerCommandNames.Redo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, (_, _) => CutSelected()));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => PasteIntoSelected()));
		grid.PreviewKeyDown += (_, e) => {
			if (awaitingWorkflowShortcut) {
				awaitingWorkflowShortcut = false;
				switch (e.Key) {
					case Key.A: ShowArguments(); break;
					case Key.V: ShowVariables(); break;
					case Key.I: ShowImports(); break;
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
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.A && e.OriginalSource is not TextBox) { SelectAllActivities(); e.Handled = true; return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.D && e.OriginalSource is not TextBox) { DuplicateSelected(); e.Handled = true; return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F) { ShowWorkflowSearch(); e.Handled = true; return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.T) { toolboxFilter.Focus(); toolboxFilter.SelectAll(); e.Handled = true; return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.E) { awaitingWorkflowShortcut = true; e.Handled = true; }
			if (e.Key == Key.Escape && pendingFlowSourceId != null) { CancelFlowConnection(); e.Handled = true; }
			else if (e.Key == Key.Escape && pendingStateTransitionId != null) { CancelStateTransitionConnection(); e.Handled = true; }
		};
		UserContent = grid;
	}

	static DataTemplate CreateToolboxItemTemplate()
	{
		var panel = new FrameworkElementFactory(typeof(StackPanel));
		var name = new FrameworkElementFactory(typeof(TextBlock));
		name.SetBinding(TextBlock.TextProperty, new Binding(nameof(DesignerToolboxItemInfo.DisplayName)));
		name.SetBinding(TextBlock.ToolTipProperty, new Binding(nameof(DesignerToolboxItemInfo.Template)));
		panel.AppendChild(name);
		var category = new FrameworkElementFactory(typeof(TextBlock));
		category.SetBinding(TextBlock.TextProperty, new Binding(nameof(DesignerToolboxItemInfo.Category)));
		category.SetValue(TextBlock.FontSizeProperty, 10d);
		category.SetValue(TextBlock.ForegroundProperty, Brushes.DimGray);
		panel.AppendChild(category);
		return new DataTemplate(typeof(DesignerToolboxItemInfo)) { VisualTree = panel };
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
		surface.Drop += (_, e) => { if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) AddToolboxItem(item); e.Handled = true; };

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
	public string? SelectedElementId => selected?.Id;
	public string? SelectedElementType => selected?.Type;
	public bool CanUndo => state.CanUndo && host?.IsAlive == true;
	public bool CanRedo => state.CanRedo && host?.IsAlive == true;

	/// <summary>Test and automation entry point that follows the same selection path as an
	/// outline/surface click. It deliberately works with structural IDs, which are stable across
	/// the OOP protocol and do not require display names to be unique.</summary>
	public bool SelectElement(string id)
	{
		if (state.Tree == null) return false;
		var node = Find(state.Tree, id);
		if (node == null) return false;
		selection.Select(node);
		return true;
	}

	public void FilterToolbox(string text) => toolboxModel.Filter(text ?? "");

	/// <summary>Runs model validation without opening the interactive diagnostics dialog. The
	/// returned result is also applied to the design surface, exactly as the Validate toolbar
	/// command does.</summary>
	public WorkflowValidationResult ValidateModel()
	{
		if (host == null) return new WorkflowValidationResult { IsValid = false, Diagnostics = { new WorkflowValidationDiagnostic { Message = "Workflow designer host is unavailable." } } };
		var result = host.ValidateWorkflowAsync(state.Version).GetAwaiter().GetResult();
		ApplyWorkflowValidation(result);
		return result;
	}

	public bool UndoLastEdit() => Undo();
	public bool RedoLastEdit() => Redo();

	/// <summary>Executes the current document through the same isolated CoreWF host used by the
	/// Run command, without displaying the interactive argument dialog. Automation callers pass
	/// only literal input values; normal UI callers continue through <see cref="RunWorkflow"/>.</summary>
	public WorkflowRunResult RunModel(IReadOnlyDictionary<string, string>? inputs = null)
	{
		if (host == null) return new WorkflowRunResult { Message = "Workflow designer host is unavailable." };
		return host.RunAsync(state.Version, inputs).GetAwaiter().GetResult();
	}

	/// <summary>Starts a run without blocking the UI, so a debugger client can observe a
	/// breakpoint and issue Continue/Step over the same OOP connection.</summary>
	public bool BeginRunModel(IReadOnlyDictionary<string, string>? inputs = null)
	{
		if (host == null || (runningWorkflow != null && !runningWorkflow.IsCompleted)) return false;
		runningWorkflow = host.RunAsync(state.Version, inputs);
		return true;
	}

	public WorkflowDebugState GetDebugModel()
	{
		if (host == null) return new WorkflowDebugState();
		var debug = host.GetDebugStateAsync().GetAwaiter().GetResult();
		ApplyDebugState(debug);
		return debug;
	}

	public bool ToggleSelectedBreakpointModel()
	{
		if (host == null || selected == null) return false;
		ApplyDebugState(host.ToggleBreakpointAsync(selected.Id).GetAwaiter().GetResult());
		SaveLayout();
		return true;
	}

	public bool ContinueRunModel() => host?.ContinueRunAsync().GetAwaiter().GetResult() == true;
	public bool ResumeBookmarkModel(string bookmarkName, string? value = null) => host?.ResumeBookmarkAsync(bookmarkName, value).GetAwaiter().GetResult() == true;
	public bool StepRunModel() => host?.StepRunAsync().GetAwaiter().GetResult() == true;
	public bool StepOverRunModel() => host?.StepOverRunAsync().GetAwaiter().GetResult() == true;
	public bool StepOutRunModel() => host?.StepOutRunAsync().GetAwaiter().GetResult() == true;
	public bool IsRunModelCompleted => runningWorkflow?.IsCompleted == true;
	public WorkflowRunResult? CompletedRunModel => runningWorkflow is { IsCompletedSuccessfully: true } ? runningWorkflow.Result : null;

	/// <summary>Inserts a toolbox item, asking for the type argument first when the item is an
	/// open generic activity (ForEach&lt;T&gt;, Switch&lt;T&gt;, ...). The chosen type travels to the
	/// host appended to the item's type name.</summary>
	public bool AddToolboxItem(DesignerToolboxItemInfo item, string? targetParentId = null)
	{
		if (!IsOpenGenericToolboxItem(item)) return Add(item.TypeName, targetParentId);
		var typeArgument = AskForTypeArgument(item);
		return typeArgument != null && Add(item.TypeName + "|" + typeArgument, targetParentId);
	}

	/// <summary>An open generic type name carries its arity (<c>ForEach`1</c>) but no bracketed
	/// type arguments, which is how the host advertises "this item needs a type".</summary>
	public static bool IsOpenGenericToolboxItem(DesignerToolboxItemInfo item)
	{
		var name = item.TypeName;
		var tick = name.IndexOf('`');
		return tick >= 0 && name.IndexOf('[', tick) < 0;
	}

	/// <summary>Prompts for the type argument of a generic activity. Returns null when cancelled.</summary>
	string? AskForTypeArgument(DesignerToolboxItemInfo item)
	{
		var chosen = (string?)null;
		var types = new ComboBox { Margin = new Thickness(8), IsEditable = true, ItemsSource = CommonTypeArguments, Text = "String" };
		var hint = new TextBlock { Margin = new Thickness(8, 0, 8, 8), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Text = "Pick the element type, or type any type name the project can see." };
		var insert = new Button { Content = "Insert", Margin = new Thickness(8), MinWidth = 75, IsDefault = true };
		var cancel = new Button { Content = "Cancel", Margin = new Thickness(8), MinWidth = 75, IsCancel = true };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(cancel); buttons.Children.Add(insert);
		var panel = new DockPanel();
		DockPanel.SetDock(types, Dock.Top); DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
		panel.Children.Add(types); panel.Children.Add(hint); panel.Children.Add(buttons);
		var window = new Window {
			Title = item.DisplayName.Replace(" <T>", "") + " - type argument",
			Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
			Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject)
		};
		insert.Click += (_, _) => { chosen = types.Text?.Trim(); if (!string.IsNullOrEmpty(chosen)) window.Close(); };
		cancel.Click += (_, _) => { chosen = null; window.Close(); };
		types.Loaded += (_, _) => types.Focus();
		window.ShowDialog();
		return string.IsNullOrWhiteSpace(chosen) ? null : chosen;
	}

	/// <summary>Renders a type name for the status line: a bare activity name, or "ForEach&lt;String&gt;"
	/// for the open-generic-plus-argument form the toolbox sends to the host.</summary>
	static string DescribeInsertedType(string type)
	{
		var separator = type.LastIndexOf('|');
		var definition = separator > 0 ? type[..separator] : type;
		var comma = definition.IndexOf(',');
		if (comma > 0) definition = definition[..comma];
		var dot = definition.LastIndexOf('.');
		if (dot >= 0) definition = definition[(dot + 1)..];
		var tick = definition.IndexOf('`');
		if (tick >= 0) definition = definition[..tick];
		return separator > 0 ? definition + "<" + type[(separator + 1)..] + ">" : definition;
	}

	static readonly string[] CommonTypeArguments = { "String", "Int32", "Int64", "Boolean", "Double", "Decimal", "DateTime", "TimeSpan", "Guid", "Object" };

	public bool Add(string type, string? targetParentId = null)
	{
		if (host == null || state.Tree == null) return false;
		var parentId = targetParentId ?? selected?.Id ?? state.Tree.Id;
		var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet();
		try {
			Mutate(() => host.AddElementAsync(state.Version, parentId, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult());
		} catch (Exception exception) {
			diagnostic.Text = $"Cannot insert {DescribeInsertedType(type)}: {exception.Message}";
			return false;
		}
		// The host is authoritative about the inserted structural ID. Collection edits can
		// reindex siblings during XAML round-tripping, so inferring the new node purely from
		// a before/after ID set can report a successful insertion as a failure.
		var added = state.Tree == null ? null : !string.IsNullOrWhiteSpace(state.CreatedElementId) ? Find(state.Tree, state.CreatedElementId) : null;
		// A collection may be normalized by a workflow activity while rebuilding its child
		// list. In that rare case the response path is stale by the time the visual tree is
		// projected, so retain the before/after fallback rather than losing a successful drop.
		added ??= state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id));
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

	bool CopySelected()
	{
		if (selection.SelectedIds.Count == 0 || host == null) return false;
		var copied = host.CopyElementsAsync(selection.SelectedIds.ToArray()).GetAwaiter().GetResult();
		diagnostic.Text = copied ? $"Copied {selection.SelectedIds.Count} workflow item(s)." : "The selected item cannot be copied.";
		return copied;
	}

	bool CutSelected()
	{
		if (selection.SelectedIds.Count == 0 || host == null) return false;
		try {
			Mutate(() => host.CutElementsAsync(state.Version, selection.SelectedIds.ToArray()).GetAwaiter().GetResult());
			return true;
		} catch (Exception exception) {
			diagnostic.Text = "Cannot cut the selected workflow item: " + exception.Message;
			return false;
		}
	}

	bool PasteIntoSelected()
	{
		if (selected == null || host == null) return false;
		try {
			Mutate(() => host.PasteElementsAsync(state.Version, selected.Id).GetAwaiter().GetResult());
			if (state.CreatedElementId is { } id && selection.Find(id) is { } pasted) selection.Select(pasted);
			return true;
		} catch (Exception exception) {
			diagnostic.Text = "Cannot paste into the selected activity: " + exception.Message;
			return false;
		}
	}

	void SelectAllActivities()
	{
		if (state.Tree == null) return;
		selection.Select(Flatten(state.Tree), DesignerSelectionOperation.Replace);
	}

	bool DuplicateSelected()
	{
		if (selected == null || host == null || string.IsNullOrEmpty(selected.Id)) return false;
		var separator = selected.Id.LastIndexOf('.');
		var parentId = separator < 0 ? "" : selected.Id[..separator];
		try {
			if (!host.CopyElementsAsync(new[] { selected.Id }).GetAwaiter().GetResult()) return false;
			Mutate(() => host.PasteElementsAsync(state.Version, parentId).GetAwaiter().GetResult());
			if (state.CreatedElementId is { } id && selection.Find(id) is { } duplicate) selection.Select(duplicate);
			return true;
		} catch (Exception exception) {
			diagnostic.Text = "Cannot duplicate the selected activity: " + exception.Message;
			return false;
		}
	}

	const string WorkflowElementDragFormat = "WorkflowDesigner.ElementId";

	/// <summary>A node drawn inside a Flowchart or StateMachine graph, whose drag gesture already
	/// means "position this node" rather than "move it elsewhere".</summary>
	bool IsGraphChild(DesignerElementNode node)
	{
		if (state.Tree == null) return false;
		var parent = Flatten(state.Tree).FirstOrDefault(candidate => candidate.Children.Any(child => child.Id == node.Id));
		return parent?.Type is "Flowchart" or "StateMachine";
	}

	/// <summary>Whether a drag of <paramref name="sourceId"/> onto <paramref name="targetId"/> is a
	/// move the designer can make: a container cannot be moved into itself or into its own subtree,
	/// and only a container can receive one.</summary>
	bool CanMoveInto(string? sourceId, string targetId)
	{
		if (string.IsNullOrEmpty(sourceId) || sourceId == targetId || state.Tree == null) return false;
		if (targetId.StartsWith(sourceId + ".", StringComparison.Ordinal)) return false;
		var target = Find(state.Tree, targetId);
		return target != null && IsContainer(target.Type);
	}

	/// <summary>Moves an activity subtree into another container. This goes through the host's
	/// cut/paste, which round-trips the subtree as XAML, so the moved activity keeps its
	/// properties, variables and children instead of being rebuilt from its type name.</summary>
	bool MoveInto(string sourceId, string targetId)
	{
		if (host == null || !CanMoveInto(sourceId, targetId)) return false;
		try {
			Mutate(() => host.CutElementsAsync(state.Version, new[] { sourceId }).GetAwaiter().GetResult());
			Mutate(() => host.PasteElementsAsync(state.Version, targetId).GetAwaiter().GetResult());
			if (state.CreatedElementId is { } id && selection.Find(id) is { } moved) selection.Select(moved);
			return true;
		} catch (Exception exception) {
			diagnostic.Text = "Cannot move the activity there: " + exception.Message;
			return false;
		}
	}

	/// <summary>Test/automation entry point for the drag-to-move gesture.</summary>
	public bool MoveElementInto(string elementId, string targetParentId) => MoveInto(elementId, targetParentId);

	public string[] ImportedNamespaces => host == null ? Array.Empty<string>() : host.GetImportsAsync().GetAwaiter().GetResult().ToArray();

	bool MoveSelected(int offset)
	{
		if (selected == null || host == null) return false;
		try {
			Mutate(() => host.MoveElementAsync(state.Version, selected.Id, offset).GetAwaiter().GetResult());
			if (state.CreatedElementId is { } id && selection.Find(id) is { } moved) selection.Select(moved);
			return true;
		} catch (Exception exception) {
			diagnostic.Text = "Cannot move the selected activity: " + exception.Message;
			return false;
		}
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
		validationMarkers.Clear();
		Rebuild();
		commands.Invalidate();
		validationTimer.Stop();
		validationTimer.Start();
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

	void BuildFinished(object sender, BuildEventArgs args)
	{
		if (args.Results?.Result != BuildResultCode.Success || host?.IsAlive != true) return;
		var project = SD.ProjectService.FindProjectContainingFile(xamlFile.FileName);
		if (project == null || !args.Projects.Contains(project)) return;
		try {
			var capabilities = host.RefreshCapabilitiesAsync(Snapshot(loadedXamlText, state.Version)).GetAwaiter().GetResult();
			toolboxModel.SetItems(capabilities.Toolbox);
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = "Workflow toolbox refreshed from the successful project build (" + capabilities.Toolbox.Count + " activities).";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Workflow toolbox could not refresh after build: " + exception.Message;
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

	/// <summary>
	/// Adds one icon button to the toolbar. <paramref name="icon"/> names an embedded
	/// Icons/{icon}_16x.xaml (see <see cref="WorkflowIcons"/>); when it cannot be loaded the button
	/// falls back to its text label, so a missing icon degrades a single button instead of leaving
	/// a blank one. <paramref name="text"/> is kept either way - as the tooltip's first part and as
	/// the automation name - because these commands are not all self-evident from an icon alone.
	/// </summary>
	void AddToolButton(string text, string icon, string tip, Action onClick)
	{
		var image = WorkflowIcons.Load(icon);
		var button = new Button {
			Content = image == null
				? text
				: new Image { Source = image, Width = 16, Height = 16, Stretch = Stretch.Uniform },
			ToolTip = string.IsNullOrEmpty(tip) ? text : text + " - " + tip,
			Padding = new Thickness(4, 2, 4, 2)
		};
		// Keeps the button findable by name for UI automation and screen readers now that its
		// visible content is a picture rather than the label.
		System.Windows.Automation.AutomationProperties.SetName(button, text);
		button.Click += (_, _) => onClick();
		toolBar.Items.Add(button);
	}

	void AddToolSeparator() => toolBar.Items.Add(new Separator());

	/// <summary>
	/// Builds the designer's command toolbar once, at construction. The separator grouping follows
	/// what the old inline buttons expressed through wider margins: view, reorder, run, debugging,
	/// flowchart wiring, state wiring, layout, then the editors.
	/// </summary>
	void BuildToolBar()
	{
		AddToolButton("Expand All", "ExpandAll", "Ctrl+E, X", ExpandCurrent);
		AddToolButton("Collapse All", "CollapseAll", "Ctrl+E, C", CollapseCurrent);
		AddToolButton("Restore", "RestoreDefaultView", "Ctrl+E, R", RestoreView);
		AddToolSeparator();
		AddToolButton("Move Up", "GlyphUp", "Move the selected activity earlier in its Sequence/Parallel collection", () => MoveSelected(-1));
		AddToolButton("Move Down", "GlyphDown", "Move the selected activity later in its Sequence/Parallel collection", () => MoveSelected(1));
		AddToolSeparator();
		AddToolButton("Run", "Run", "Execute the current workflow", RunWorkflow);
		AddToolButton("Validate", "ValidateDocument", "Validate the workflow model without running it", ValidateWorkflow);
		AddToolButton("Stop", "Stop", "Cancel the active workflow run", () => _ = CancelWorkflow());
		AddToolSeparator();
		AddToolButton("Breakpoint", "BreakpointEnable", "Toggle a breakpoint on the selected activity (Ctrl+E, B)", ToggleBreakpoint);
		AddToolButton("Continue", "Next", "Resume a paused workflow (Ctrl+E, D)", ContinueWorkflow);
		AddToolButton("Resume Bookmark", "Bookmark", "Supply an external bookmark value to an idle workflow", ResumeBookmarkWorkflow);
		AddToolButton("Step", "StepIn", "Run to the next activity record (Ctrl+E, S)", StepWorkflow);
		AddToolButton("Step Over", "StepOver", "Run to the next activity at this scope", StepOverWorkflow);
		AddToolButton("Step Out", "StepOut", "Run until leaving this activity scope", StepOutWorkflow);
		AddToolSeparator();
		AddToolButton("Connect", "Link", "Arm selected Flowchart step, then click its target (Esc cancels)", BeginOrConnectFlowSteps);
		AddToolButton("True Branch", "CheckMark", "Arm selected Flow Decision's True branch, then click its target", () => BeginOrConnectFlowDecision(true));
		AddToolButton("False Branch", "Cancel", "Arm selected Flow Decision's False branch, then click its target", () => BeginOrConnectFlowDecision(false));
		AddToolButton("Set Start", "Entry", "Set the selected Flowchart step or decision as the entry node", SetSelectedFlowStart);
		AddToolButton("Clear Next", "Unlink", "Disconnect selected Flowchart step's Next edge", () => DisconnectSelectedFlow("Next"));
		AddToolButton("Clear True", "RemoveLink", "Disconnect selected Flow Decision's True edge", () => DisconnectSelectedFlow("True"));
		AddToolButton("Clear False", "Disconnect", "Disconnect selected Flow Decision's False edge", () => DisconnectSelectedFlow("False"));
		AddToolSeparator();
		AddToolButton("Set State Target", "AddTransition", "Arm the selected StateMachine Transition, then click its target State", BeginStateTransitionConnection);
		AddToolButton("Clear State Target", "ClearWindowContent", "Disconnect the selected StateMachine Transition", ClearStateTransitionTarget);
		AddToolSeparator();
		AddToolButton("Auto Layout", "AutoArrangeShapes", "Reset Flowchart node positions to the default grid", AutoLayoutFlowcharts);
		AddToolSeparator();
		AddToolButton("Expression", "CustomExpression", "Edit an expression for the selected activity (Ctrl+E, E)", ShowExpressionEditor);
		AddToolButton("Arguments", "Parameter", "Edit the workflow's arguments", ShowArguments);
		AddToolButton("Variables", "LocalVariable", "Edit the workflow's variables", ShowVariables);
		AddToolButton("Imports", "Link", "Edit the namespaces the workflow's expressions are compiled against (Ctrl+E, I)", ShowImports);
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

	Dictionary<string, string>? ShowRunInputsDialog()
	{
		if (host == null) return null;
		var arguments = host.GetArgumentsAsync().GetAwaiter().GetResult()
			.Where(argument => !string.Equals(argument.Direction, "Out", StringComparison.OrdinalIgnoreCase)).ToArray();
		if (arguments.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
		var fields = new Dictionary<string, TextBox>(StringComparer.Ordinal);
		var panel = new StackPanel { Margin = new Thickness(12) };
		panel.Children.Add(new TextBlock { Text = "Optional values for the workflow's In/InOut arguments. Leave a field empty to use its declared default.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
		foreach (var argument in arguments) {
			var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
			row.ColumnDefinitions.Add(new ColumnDefinition());
			var label = new TextBlock { Text = $"{argument.Name} ({argument.TypeName})", VerticalAlignment = VerticalAlignment.Center, ToolTip = string.IsNullOrEmpty(argument.DefaultValue) ? "No declared default" : "Default: " + argument.DefaultValue };
			var value = new TextBox { MinWidth = 220, ToolTip = "Enter a literal value" };
			fields.Add(argument.Name, value);
			row.Children.Add(label); Grid.SetColumn(value, 1); row.Children.Add(value); panel.Children.Add(row);
		}
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var run = new Button { Content = "Run", IsDefault = true, MinWidth = 75, Margin = new Thickness(4) };
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75, Margin = new Thickness(4) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
		buttons.Children.Add(cancel); buttons.Children.Add(run); panel.Children.Add(buttons);
		var window = new Window { Title = "Run Workflow", Width = 480, SizeToContent = SizeToContent.Height, Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject) };
		run.Click += (_, _) => { foreach (var field in fields.Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))) result.Add(field.Key, field.Value.Text); window.DialogResult = true; };
		return window.ShowDialog() == true ? result : null;
	}

	async void RunWorkflow()
	{
		if (host == null) return;
		try {
			var inputs = ShowRunInputsDialog();
			if (inputs == null) return;
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = "Workflow running…";
			var running = host.RunAsync(state.Version, inputs);
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

	async void ValidateWorkflow()
	{
		if (host == null) return;
		try {
			var result = await host.ValidateWorkflowAsync(state.Version);
			ApplyWorkflowValidation(result);
			var first = result.Diagnostics.FirstOrDefault(diagnostic => !string.IsNullOrEmpty(diagnostic.ElementId));
			if (first != null) NavigateToValidationDiagnostic(first);
			diagnostic.Foreground = result.IsValid ? Brushes.ForestGreen : Brushes.OrangeRed;
			diagnostic.Text = result.IsValid ? "Workflow model is valid." : string.Join(Environment.NewLine, result.Diagnostics.Take(8).Select(item => (item.IsWarning ? "Warning: " : "Error: ") + item.Message))
				+ (result.Diagnostics.Count > 8 ? Environment.NewLine + "…" : "");
			if (result.Diagnostics.Count > 0) ShowValidationResults(result.Diagnostics);
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Workflow validation failed: " + exception.Message;
		}
	}

	async Task RefreshWorkflowValidationAsync()
	{
		if (host == null || !state.Accepted) return;
		var version = state.Version;
		try {
			var result = await host.ValidateWorkflowAsync(version);
			if (state.Version != version) return;
			ApplyWorkflowValidation(result);
			diagnostic.Foreground = result.IsValid ? Brushes.ForestGreen : Brushes.OrangeRed;
			var errors = result.Diagnostics.Count(item => !item.IsWarning);
			var warnings = result.Diagnostics.Count - errors;
			diagnostic.Text = result.IsValid ? "Workflow model is valid." : $"Workflow validation: {errors} error(s), {warnings} warning(s).";
		} catch {
			// Live validation is advisory. An explicit Validate command still reports a transport
			// failure to the user, but a transient host restart must not interrupt editing.
		}
	}

	void ApplyWorkflowValidation(WorkflowValidationResult result)
	{
		validationMarkers.Clear();
		foreach (var group in result.Diagnostics.Where(item => !string.IsNullOrWhiteSpace(item.ElementId)).GroupBy(item => item.ElementId, StringComparer.Ordinal))
			validationMarkers[group.Key] = group.All(item => item.IsWarning);
		Rebuild();
	}

	Brush ValidationBorder(DesignerElementNode node, Brush normal)
	{
		return validationMarkers.TryGetValue(node.Id, out var warning) ? warning ? Brushes.Goldenrod : Brushes.OrangeRed : normal;
	}

	void NavigateToValidationDiagnostic(WorkflowValidationDiagnostic validation)
	{
		if (state.Tree != null && !string.IsNullOrWhiteSpace(validation.ElementId) && Find(state.Tree, validation.ElementId) is { } node) selection.Select(node);
	}

	void ShowValidationResults(IReadOnlyList<WorkflowValidationDiagnostic> validations)
	{
		var items = validations.Select(validation => new WorkflowValidationListItem { Diagnostic = validation }).ToArray();
		var list = new ListBox { Margin = new Thickness(8), ItemsSource = items, DisplayMemberPath = nameof(WorkflowValidationListItem.Text), MinWidth = 620, MinHeight = 180 };
		var window = new Window { Title = "Workflow Validation", Owner = Window.GetWindow(UserContent as DependencyObject), Width = 760, Height = 320, Content = list, WindowStartupLocation = WindowStartupLocation.CenterOwner };
		void Navigate()
		{
			if (list.SelectedItem is WorkflowValidationListItem item) NavigateToValidationDiagnostic(item.Diagnostic);
		}
		list.MouseDoubleClick += (_, _) => { Navigate(); window.Close(); };
		list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(); window.Close(); e.Handled = true; } };
		list.SelectedIndex = 0;
		window.ShowDialog();
	}

	void ShowWorkflowSearch()
	{
		if (state.Tree == null) return;
		var query = new TextBox { Margin = new Thickness(8), ToolTip = "Search activity name, type, property, or value" };
		var results = new ListBox { Margin = new Thickness(8), DisplayMemberPath = nameof(WorkflowSearchItem.Text), MinHeight = 220 };
		var panel = new DockPanel();
		DockPanel.SetDock(query, Dock.Top);
		panel.Children.Add(query);
		panel.Children.Add(results);
		var all = Flatten(state.Tree).Select(node => new WorkflowSearchItem { Node = node }).ToArray();
		void Filter()
		{
			var text = query.Text.Trim();
			results.ItemsSource = all.Where(item => string.IsNullOrEmpty(text) || (item.Node.Type + "\n" + item.Node.Name + "\n" + item.Node.Id + "\n" + string.Join("\n", item.Node.Properties.Select(property => property.DisplayName + " " + property.Value))).Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
			results.SelectedIndex = 0;
		}
		var window = new Window { Title = "Find Workflow Activity", Width = 620, Height = 370, Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject), WindowStartupLocation = WindowStartupLocation.CenterOwner };
		void Navigate()
		{
			if (results.SelectedItem is WorkflowSearchItem item) selection.Select(item.Node);
		}
		query.TextChanged += (_, _) => Filter();
		results.MouseDoubleClick += (_, _) => { Navigate(); window.Close(); };
		results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(); window.Close(); e.Handled = true; } };
		query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(); window.Close(); e.Handled = true; } };
		Filter();
		window.ShowDialog();
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
	async void ResumeBookmarkWorkflow()
	{
		if (host == null) return;
		try {
			var debug = await host.GetDebugStateAsync();
			if (debug.Bookmarks.Count == 0) {
				diagnostic.Foreground = Brushes.OrangeRed;
				diagnostic.Text = "The active workflow is not waiting for an external bookmark.";
				return;
			}
			var bookmarks = new ComboBox { Margin = new Thickness(8), ItemsSource = debug.Bookmarks, DisplayMemberPath = nameof(WorkflowBookmarkInfo.Name), SelectedValuePath = nameof(WorkflowBookmarkInfo.Name), SelectedIndex = 0, ToolTip = "Bookmark to resume" };
			var value = new TextBox { Margin = new Thickness(8), ToolTip = "Optional string value supplied to the bookmark callback" };
			var submit = new Button { Content = "Resume", IsDefault = true, Margin = new Thickness(8) };
			var panel = new StackPanel();
			panel.Children.Add(new TextBlock { Text = "Bookmark", Margin = new Thickness(8, 8, 8, 0) }); panel.Children.Add(bookmarks);
			panel.Children.Add(new TextBlock { Text = "Value", Margin = new Thickness(8, 4, 8, 0) }); panel.Children.Add(value); panel.Children.Add(submit);
			var window = new Window { Title = "Resume Workflow Bookmark", Width = 420, Height = 210, Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject), WindowStartupLocation = WindowStartupLocation.CenterOwner };
			submit.Click += async (_, _) => {
				var name = bookmarks.SelectedValue as string;
				if (string.IsNullOrWhiteSpace(name)) return;
				if (await host.ResumeBookmarkAsync(name, value.Text)) { window.Close(); diagnostic.Foreground = Brushes.DodgerBlue; diagnostic.Text = "Bookmark '" + name + "' resumed."; }
				else { diagnostic.Foreground = Brushes.OrangeRed; diagnostic.Text = "Bookmark '" + name + "' could not be resumed."; }
			};
			window.ShowDialog();
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Could not inspect workflow bookmarks: " + exception.Message;
		}
	}
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
			var current = debug.CallStack.LastOrDefault();
			var location = current == null ? (string.IsNullOrEmpty(debug.CurrentElementId) ? "an activity" : debug.CurrentElementId) : current.DisplayName + " (" + current.TypeName + ")";
			var stack = debug.CallStack.Count > 1 ? " Stack: " + string.Join(" › ", debug.CallStack.Select(frame => frame.DisplayName)) + "." : "";
			var trace = debug.Trace.Count == 0 ? "" : " Trace: " + debug.Trace[^1] + ".";
			var inputs = debug.Inputs.Count == 0 ? "" : " Inputs: " + string.Join(", ", debug.Inputs.Select(input => input.Key + "=" + input.Value)) + ".";
			diagnostic.Text = "Paused at " + location + "." + stack + inputs + trace + " Continue or Step to resume.";
			if (!string.IsNullOrEmpty(debug.CurrentElementId) && state.Tree != null && Find(state.Tree, debug.CurrentElementId) is { } node)
				selection.Select(node);
		} else if (debug.IsIdle) {
			diagnostic.Foreground = Brushes.DodgerBlue;
			diagnostic.Text = debug.Bookmarks.Count == 0 ? "Workflow is idle." : "Workflow is waiting for Bookmark: " + string.Join(", ", debug.Bookmarks.Select(bookmark => bookmark.Name)) + ". Use Resume Bookmark or Stop.";
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

	void BeginStateTransitionConnection()
	{
		if (selected?.Type != "Transition") {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a StateMachine Transition first.";
			return;
		}
		pendingStateTransitionId = selected.Id;
		diagnostic.Foreground = Brushes.DodgerBlue;
		diagnostic.Text = "State transition target armed. Click a State card (Esc cancels).";
	}

	void ConnectArmedStateTarget(string stateId)
	{
		if (host == null || pendingStateTransitionId == null) return;
		try {
			var transition = pendingStateTransitionId;
			Mutate(() => host.SetPropertyAsync(state.Version, transition, "$target", stateId).GetAwaiter().GetResult());
			pendingStateTransitionId = null;
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "State transition target updated.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
	}

	void CancelStateTransitionConnection()
	{
		pendingStateTransitionId = null;
		diagnostic.Foreground = Brushes.DodgerBlue;
		diagnostic.Text = "State transition connection cancelled.";
	}

	void ClearStateTransitionTarget()
	{
		if (host == null || selected?.Type != "Transition") {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a StateMachine Transition first.";
			return;
		}
		try {
			Mutate(() => host.SetPropertyAsync(state.Version, selected.Id, "$target", "").GetAwaiter().GetResult());
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "State transition target cleared.";
		} catch (Exception exception) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = exception.Message;
		}
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

	void SetSelectedFlowStart()
	{
		if (host == null || selected == null) {
			diagnostic.Foreground = Brushes.OrangeRed;
			diagnostic.Text = "Select a Flowchart step or Flow Decision first.";
			return;
		}
		try {
			Mutate(() => host.SetFlowStartAsync(state.Version, selected.Id).GetAwaiter().GetResult());
			diagnostic.Foreground = Brushes.ForestGreen;
			diagnostic.Text = "Flowchart start node updated.";
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
		// A real code editor rather than a TextBox: a workflow expression is C# (or VB) source, and
		// gets the same highlighting and completion-window behavior it would have in a code file.
		var editor = new ICSharpCode.AvalonEdit.TextEditor {
			Margin = new Thickness(8), MinHeight = 110, FontFamily = new FontFamily("Menlo, Consolas, monospace"),
			ShowLineNumbers = false, WordWrap = true,
			SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("C#")
		};
		editor.Options.EnableEmailHyperlinks = false;
		editor.Options.EnableHyperlinks = false;
		var targetStates = new ComboBox { Margin = new Thickness(8), DisplayMemberPath = nameof(DesignerElementNode.Name), SelectedValuePath = nameof(DesignerElementNode.Id), Visibility = Visibility.Collapsed, ToolTip = "Target State" };
		var transitionMarker = selected.Id.LastIndexOf(".$transition-", StringComparison.Ordinal);
		var stateMarker = transitionMarker < 0 ? -1 : selected.Id.LastIndexOf("$state-", transitionMarker, StringComparison.Ordinal);
		var stateMachinePrefix = stateMarker < 0 ? "\0" : selected.Id[..stateMarker];
		targetStates.ItemsSource = state.Tree == null ? Array.Empty<DesignerElementNode>() : Flatten(state.Tree).Where(node => node.Type == "State" && node.Id.StartsWith(stateMachinePrefix, StringComparison.Ordinal) && node.Id.IndexOf("$state-", stateMachinePrefix.Length, StringComparison.Ordinal) == stateMachinePrefix.Length).ToArray();
		ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow? completionWindow = null;
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
		var panel = new DockPanel(); DockPanel.SetDock(propertiesList, Dock.Top); DockPanel.SetDock(targetStates, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom); DockPanel.SetDock(validation, Dock.Bottom); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(propertiesList); panel.Children.Add(targetStates); panel.Children.Add(hint); panel.Children.Add(validation); panel.Children.Add(buttons); panel.Children.Add(editor);
		var window = new Window { Title = "Workflow Expression", Width = 520, Height = 280, Content = panel, Owner = Window.GetWindow(UserContent as DependencyObject) };
		cancel.Click += (_, _) => window.Close();
		async Task ValidateExpressionAsync()
		{
			if (host == null) return;
			try {
				var result = await host.ValidateExpressionAsync(editor.Text, selected.Id);
				validation.Foreground = result.IsValid ? Brushes.ForestGreen : Brushes.OrangeRed;
				validation.Text = result.IsValid ? "Expression is valid." : result.Details.Count > 0
					? string.Join(Environment.NewLine, result.Details.Select(detail => $"{detail.Code} ({detail.Line},{detail.Column}): {detail.Message}"))
					: string.Join(Environment.NewLine, result.Diagnostics);
			} catch (Exception exception) {
				validation.Foreground = Brushes.OrangeRed;
				validation.Text = "Validation failed: " + exception.Message;
			}
		}
		var validationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
		validationTimer.Tick += async (_, _) => { validationTimer.Stop(); await ValidateExpressionAsync(); };
		editor.TextChanged += (_, _) => { validationTimer.Stop(); validationTimer.Start(); };
		window.Closed += (_, _) => validationTimer.Stop();
		async Task CompleteAtCaret()
		{
			if (host == null) return;
			var caret = editor.CaretOffset;
			var start = caret;
			while (start > 0 && (char.IsLetterOrDigit(editor.Text[start - 1]) || editor.Text[start - 1] == '_')) start--;
			var prefix = editor.Text[start..caret];
			try {
				var result = await host.CompleteExpressionAsync(editor.Text, prefix, selected.Id);
				completionWindow?.Close();
				if (result.Items.Count == 0) { validation.Foreground = Brushes.DimGray; validation.Text = "No completions here."; return; }
				var window = new ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow(editor.TextArea) { StartOffset = start, EndOffset = caret };
				foreach (var item in result.Items) window.CompletionList.CompletionData.Add(new WorkflowCompletionData(item));
				window.Closed += (_, _) => completionWindow = null;
				completionWindow = window;
				window.Show();
			} catch (Exception exception) { validation.Foreground = Brushes.OrangeRed; validation.Text = "Completion failed: " + exception.Message; }
		}
		validate.Click += async (_, _) => await ValidateExpressionAsync();
		complete.Click += async (_, _) => await CompleteAtCaret();
		editor.PreviewKeyDown += async (_, e) => {
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Space) { await CompleteAtCaret(); e.Handled = true; }
		};
		// Member access is where completion earns its keep, so offer it without waiting for the
		// explicit Ctrl+Space the previous editor required.
		editor.TextArea.TextEntered += async (_, e) => { if (e.Text == ".") await CompleteAtCaret(); };
		window.Closed += (_, _) => completionWindow?.Close();
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

	/// <summary>Edits the workflow's imported namespaces - the same list CoreWF compiles its text
	/// expressions against, so an import added here is what makes an unqualified type name resolve
	/// both in the expression editor and at run time.</summary>
	void ShowImports()
	{
		if (host == null) return;
		var list = new ListBox { Margin = new Thickness(8) };
		var namespaceName = new TextBox { Width = 260, Margin = new Thickness(4), ToolTip = "Namespace to import, e.g. System.Text" };
		var add = new Button { Content = "Add Import", Margin = new Thickness(4), IsDefault = true };
		var remove = new Button { Content = "Remove Selected", Margin = new Thickness(4) };
		var error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
		void Refresh() => list.ItemsSource = host.GetImportsAsync().GetAwaiter().GetResult();
		bool RunMutation(Func<DesignerSessionState> action) { try { error.Text = ""; Mutate(action); Refresh(); return true; } catch (Exception exception) { error.Text = exception.Message; diagnostic.Text = exception.Message; return false; } }
		add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(namespaceName.Text) && RunMutation(() => host.AddImportAsync(state.Version, namespaceName.Text.Trim()).GetAwaiter().GetResult())) namespaceName.Clear(); };
		remove.Click += (_, _) => { if (list.SelectedItem is string selectedImport) RunMutation(() => host.RemoveImportAsync(state.Version, selectedImport).GetAwaiter().GetResult()); };
		list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && list.SelectedItem is string selectedImport) { RunMutation(() => host.RemoveImportAsync(state.Version, selectedImport).GetAwaiter().GetResult()); e.Handled = true; } };
		Refresh();
		var bar = new StackPanel { Orientation = Orientation.Horizontal };
		bar.Children.Add(namespaceName); bar.Children.Add(add); bar.Children.Add(remove); bar.Children.Add(error);
		var panel = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(list);
		new Window { Title = "Workflow Imports", Width = 560, Height = 320, Content = panel }.ShowDialog();
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
		foreach (var row in PropertyRows(node)) stack.Children.Add(row);
		if (node.Children.Count == 0 && IsContainer(node.Type))
			stack.Children.Add(new TextBlock { Text = node.Type == "Flowchart" ? "Drop the first flow step here" : "Drop activity here", FontStyle = FontStyles.Italic, Foreground = HintInk, Margin = new Thickness(8, 6, 8, 8) });
		if (!collapsed.Contains(node.Id)) {
			if (node.Type == "Flowchart") stack.Children.Add(FlowchartGraph(node));
			else if (node.Type == "StateMachine") stack.Children.Add(StateMachineGraph(node));
			else {
				// A Sequence-like body runs top to bottom, so draw the order instead of leaving the
				// reader to infer it from stacking - this is the "▽ between steps" the WF designer
				// uses. Only bodies that actually execute in order get connectors: If/TryCatch hold
				// branches, where a downward arrow would assert a sequence that does not exist.
				var ordered = node.Type is "Sequence" or "Parallel" or "While" or "DoWhile" or "State";
				for (var index = 0; index < node.Children.Count; index++) {
					if (ordered && index > 0) stack.Children.Add(StepConnector());
					stack.Children.Add(Box(node.Children[index]));
				}
			}
		}
		var border = new Border {
			BorderBrush = ValidationBorder(node, node.Type switch { "Flowchart" => Brushes.Teal, "FlowDecision" => DecisionEdge, _ => ActivityEdge }), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
			Background = ActivityBody, Margin = new Thickness(4), Child = stack
		};
		// The workbench theme can be dark, and these boxes are deliberately light (WF's canvas is a
		// document, not chrome). Foreground is inherited, so without pinning it here every label
		// inside rendered in the theme's near-white ink on our white body - the "washed out,
		// unreadable text" this canvas used to show. Pin once on the box; descendants inherit.
		TextElement.SetForeground(border, ActivityInk);
		border.AllowDrop = true;
		border.DragOver += (_, e) => {
			e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy
				: CanMoveInto(e.Data.GetData(WorkflowElementDragFormat) as string, node.Id) ? DragDropEffects.Move
				: DragDropEffects.None;
			e.Handled = true;
		};
		border.Drop += (_, e) => {
			if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) AddToolboxItem(item, node.Id);
			else if (e.Data.GetData(WorkflowElementDragFormat) is string movedId) MoveInto(movedId, node.Id);
			e.Handled = true;
		};
		// Dragging an activity box onto another container moves the whole subtree there. Flowchart
		// and StateMachine children own their drag gesture already (it positions the node on the
		// graph), so this applies to the ordinary nested-box surface only.
		if (node.Id.Length > 0 && !IsGraphChild(node)) {
			var pressedAt = (Point?)null;
			border.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(surface);
			border.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
			// MouseMove bubbles, so the innermost box under the cursor claims the gesture; the
			// system drag threshold keeps a plain click on a box from becoming a move.
			border.MouseMove += (_, e) => {
				if (e.LeftButton != MouseButtonState.Pressed || pendingFlowSourceId != null || pressedAt == null) return;
				var moved = e.GetPosition(surface) - pressedAt.Value;
				if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
				pressedAt = null;
				e.Handled = true;
				DragDrop.DoDragDrop(border, new DataObject(WorkflowElementDragFormat, node.Id), DragDropEffects.Move);
			};
		}
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

	/// <summary>DisplayName header label - double-click swaps it for a TextBox that commits on
	/// Enter/lost-focus, matching sequence-activity-designer.md's "the value can be edited in the
	/// property grid or directly on the header of the activity designer".</summary>
	// The activity canvas is a light "document" surface regardless of the workbench theme, matching
	// what the WF designer draws. Keeping the palette in one place stops individual elements from
	// falling back to inherited theme brushes, which is what made the old boxes unreadable.
	static readonly Brush ActivityInk = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
	static readonly Brush HintInk = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));
	static readonly Brush ActivityBody = Brushes.White;
	static readonly Brush ActivityHeader = new SolidColorBrush(Color.FromRgb(0xEF, 0xF1, 0xF4));
	static readonly Brush ActivityEdge = new SolidColorBrush(Color.FromRgb(0xA5, 0xAF, 0xBA));
	static readonly Brush ConnectorInk = new SolidColorBrush(Color.FromRgb(0x8A, 0x96, 0xA3));
	static readonly Brush DecisionEdge = new SolidColorBrush(Color.FromRgb(0xB0, 0x76, 0x0E));

	/// <summary>
	/// The downward step marker WF draws between consecutive activities in a sequence body, so
	/// execution order is something the diagram states rather than something the reader infers.
	/// </summary>
	static FrameworkElement StepConnector() => new Polygon {
		Points = new PointCollection { new Point(0, 0), new Point(10, 0), new Point(5, 7) },
		Fill = ConnectorInk,
		HorizontalAlignment = HorizontalAlignment.Center,
		Margin = new Thickness(0, 1, 0, 1),
		IsHitTestVisible = false
	};

	/// <summary>
	/// The WF designer shows an activity's own values in its body (Assign renders
	/// <c>Target = New System.Random</c>), which is most of what makes its diagram readable at a
	/// glance. Ours previously showed nothing, so a WriteLine was an unlabelled box whose text only
	/// appeared in the Properties pad. Rendered for leaf activities only - a container's body is
	/// already occupied by its children, and WF does not annotate containers either.
	/// </summary>
	IEnumerable<FrameworkElement> PropertyRows(DesignerElementNode node)
	{
		if (node.Children.Count > 0 || node.Properties == null) yield break;
		foreach (var property in node.Properties) {
			// $displayName is the header's own text, and empty values would render as a blank row.
			if (property.Name == "$displayName" || string.IsNullOrWhiteSpace(property.Value)) continue;
			var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3, 8, 3) };
			row.Children.Add(new TextBlock {
				Text = property.DisplayName is { Length: > 0 } display ? display : property.Name,
				Foreground = HintInk, Margin = new Thickness(0, 0, 8, 0), MinWidth = 54
			});
			row.Children.Add(new TextBlock { Text = property.Value, Foreground = ActivityInk, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
			yield return row;
		}
	}

	/// <summary>Embedded icon for an activity type, falling back to a generic activity glyph.</summary>
	static ImageSource? ActivityIcon(string type) => WorkflowIcons.Load(type switch {
		"Sequence" => "Sequence",
		"Parallel" => "Parallel",
		"While" or "DoWhile" or "ForEach" => "Loop",
		"If" or "FlowDecision" => "If",
		"TryCatch" => "TryCatch",
		"StateMachine" => "StateMachine",
		"State" or "Transition" => "Event",
		"Flowchart" or "FlowStep" => "DecisionTree",
		"Assign" => "Assign",
		"WriteLine" => "Console",
		"Delay" => "Time",
		_ => "Activity"
	});

	/// <summary>
	/// CLR generic type names arrive with their arity suffix (<c>Literal`1</c>), an implementation
	/// detail no workflow author recognises. Strip it - the readable form is the node's own Name,
	/// which the host already expands to <c>Literal&lt;String&gt;</c>.
	/// </summary>
	static string FriendlyType(string type)
	{
		var tick = type.IndexOf('`');
		return tick < 0 ? type : type.Substring(0, tick);
	}

	static string HeaderText(DesignerElementNode node)
	{
		var friendly = FriendlyType(node.Type);
		if (!string.IsNullOrEmpty(node.Name)) {
			// Name often already contains the type ("Literal<String>"), which the old format
			// rendered as: Literal`1 "Literal<String>" - the same word twice around a backtick.
			if (node.Name!.StartsWith(friendly, StringComparison.Ordinal)) return node.Name!;
			if (node.Name != node.Type) return $"{friendly} \"{node.Name}\"";
		}
		return friendly;
	}

	FrameworkElement HeaderLabel(DesignerElementNode node)
	{
		var label = new TextBlock {
			Text = HeaderText(node),
			FontWeight = FontWeights.SemiBold, Foreground = ActivityInk, VerticalAlignment = VerticalAlignment.Center
		};
		label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) BeginEditHeader(node, label); else { drillRootId = node.Id; Rebuild(); } e.Handled = true; } };

		// WF gives every activity a header band - icon, name, and (for containers) a collapse
		// chevron - visually separated from the body. Previously this was a bare bold TextBlock,
		// so an activity read as an undifferentiated rounded rectangle.
		var header = new StackPanel { Orientation = Orientation.Horizontal };
		if (node.Type == "FlowDecision") {
			// WF draws a decision as a diamond. The box itself has to stay a Border (drag, drop,
			// selection and boxesById all depend on it), so the diamond is carried in the header
			// as a drawn glyph rather than by rotating the container - which would break
			// hit-testing and the layout of everything inside it.
			var diamond = new Polygon {
				Points = new PointCollection { new Point(7, 0), new Point(14, 7), new Point(7, 14), new Point(0, 7) },
				Fill = Brushes.White, Stroke = DecisionEdge, StrokeThickness = 1.6,
				Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
			};
			header.Children.Add(diamond);
		} else if (ActivityIcon(node.Type) is { } icon) {
			header.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
		}
		header.Children.Add(label);

		if (node.Children.Count > 0) {
			// A themed Button rendered here as a filled dark square against the light body; a flat
			// glyph keeps the affordance without importing the workbench's control chrome.
			var isCollapsed = collapsed.Contains(node.Id);
			var chevron = new TextBlock {
				Text = isCollapsed ? "›" : "⌄", Foreground = HintInk, FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
				ToolTip = isCollapsed ? "Expand" : "Collapse"
			};
			chevron.MouseLeftButtonDown += (_, e) => { if (!collapsed.Add(node.Id)) collapsed.Remove(node.Id); Rebuild(); e.Handled = true; };
			header.Children.Insert(0, chevron);
		}

		return new Border {
			Background = ActivityHeader,
			BorderBrush = ActivityEdge, BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(8, 5, 8, 5),
			Child = header
		};
	}

	void BeginEditHeader(DesignerElementNode node, TextBlock label)
	{
		if (label.Parent is not Panel parent) return;
		var index = parent.Children.IndexOf(label);
		var editor = new TextBox { Text = node.Name ?? "", MinWidth = 80 };
		void Commit()
		{
			parent.Children.RemoveAt(index);
			// Restore the label itself, not HeaderLabel(node): the label now lives inside the
			// header band's StackPanel, so re-inserting a whole header Border here would nest a
			// second header inside the first.
			parent.Children.Insert(index, label);
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
		SD.BuildService.BuildFinished -= buildFinishedHandler;
		properties.Clear();
		OutputChannel.Write("WorkflowDesigner", "Designer view disposed");
		host?.Dispose();
		base.Dispose();
	}
}
