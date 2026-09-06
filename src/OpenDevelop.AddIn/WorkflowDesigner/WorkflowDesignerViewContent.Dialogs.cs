using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>The designer's modal editors - expression, type argument, arguments, variables and
/// imports. They are the parts of the view content that own a window of their own rather than the
/// canvas, and are split out of WorkflowDesignerViewContent.cs to keep that file about the
/// surface.</summary>
public sealed partial class WorkflowDesignerViewContent
{
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
}
