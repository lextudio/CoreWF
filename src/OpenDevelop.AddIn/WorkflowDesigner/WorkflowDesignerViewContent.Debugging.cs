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

/// <summary>Debugging the workflow in the designer host: breakpoints, continue/step, bookmark
/// resumption, cancellation, and reporting the paused state on the diagnostic line. Split out of
/// WorkflowDesignerViewContent.cs, which keeps the editing surface.</summary>
public sealed partial class WorkflowDesignerViewContent
{
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
}
