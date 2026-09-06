using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>
/// Integration hooks for the CoreWF designer. These intentionally drive the real secondary view
/// and its OOP host, rather than creating a parallel in-memory workflow model in tests.
/// </summary>
[DevFlowUIThread]
public static class WorkflowDesignerDevFlowActions
{
	static WorkflowDesignerViewContent? Activate()
	{
		var window = SD.Workbench.ActiveWorkbenchWindow;
		if (window == null) return null;
		for (var index = 0; index < window.ViewContents.Count; index++) {
			if (window.ViewContents[index] is not WorkflowDesignerViewContent view) continue;
			if (window.ActiveViewContent != view) window.SwitchView(index);
			return view;
		}
		return null;
	}

	[DevFlowAction("od.workflow-designer.status", Description = "Inspect the active CoreWF workflow designer and its isolated host")]
	public static string Status()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new {
			active = view != null,
			status = view?.Status,
			elementCount = view?.ElementCount ?? 0,
			hostProcessId = view?.HostProcessId ?? 0,
			selectedElementId = view?.SelectedElementId,
			selectedElementType = view?.SelectedElementType,
			canUndo = view?.CanUndo ?? false,
			canRedo = view?.CanRedo ?? false,
			toolboxItemCount = view?.ToolboxItemCount ?? 0,
			projectToolboxItemCount = view?.ProjectToolboxItemCount ?? 0,
			referencedToolboxItemCount = view?.ReferencedToolboxItemCount ?? 0
		});
	}

	[DevFlowAction("od.workflow-designer.select", Description = "Select a CoreWF activity by its structural workflow ID")]
	public static string Select(string elementId)
	{
		var view = Activate();
		var success = view?.SelectElement(elementId) == true;
		return JsonSerializer.Serialize(new { success, selectedElementId = view?.SelectedElementId, selectedElementType = view?.SelectedElementType });
	}

	[DevFlowAction("od.workflow-designer.toolbox.filter", Description = "Filter the active CoreWF workflow Toolbox")]
	public static string FilterToolbox(string text)
	{
		var view = Activate();
		view?.FilterToolbox(text);
		return JsonSerializer.Serialize(new { success = view != null, text, itemCount = view?.ToolboxItemCount ?? 0 });
	}

	[DevFlowAction("od.workflow-designer.toolbox.insert", Description = "Insert a workflow activity into the selected CoreWF activity")]
	public static string Insert(string typeName)
	{
		var view = Activate();
		var success = view?.Add(typeName) == true;
		return JsonSerializer.Serialize(new { success, elementCount = view?.ElementCount ?? 0, selectedElementId = view?.SelectedElementId, diagnostic = view?.Diagnostic });
	}

	[DevFlowAction("od.workflow-designer.move-into", Description = "Move a CoreWF activity subtree into another container, as dragging its box does")]
	public static string MoveInto(string elementId, string targetParentId)
	{
		var view = Activate();
		var success = view?.MoveElementInto(elementId, targetParentId) == true;
		return JsonSerializer.Serialize(new { success, elementCount = view?.ElementCount ?? 0, selectedElementId = view?.SelectedElementId, diagnostic = view?.Diagnostic });
	}

	[DevFlowAction("od.workflow-designer.imports", Description = "Read the namespaces the active CoreWF workflow imports")]
	public static string Imports()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { imports = view?.ImportedNamespaces ?? Array.Empty<string>() });
	}

	[DevFlowAction("od.workflow-designer.set-property", Description = "Set a property on the selected CoreWF activity")]
	public static string SetProperty(string name, string value)
	{
		var view = Activate();
		var success = view?.SetSelectedProperty(name, value) == true;
		return JsonSerializer.Serialize(new { success, selectedElementId = view?.SelectedElementId, propertyName = name });
	}

	[DevFlowAction("od.workflow-designer.delete", Description = "Delete the current CoreWF workflow selection")]
	public static string Delete()
	{
		var view = Activate();
		var success = view?.DeleteSelected() == true;
		return JsonSerializer.Serialize(new { success, elementCount = view?.ElementCount ?? 0 });
	}

	[DevFlowAction("od.workflow-designer.undo", Description = "Undo the latest CoreWF workflow edit")]
	public static string Undo()
	{
		var view = Activate();
		var success = view?.UndoLastEdit() == true;
		return JsonSerializer.Serialize(new { success, canUndo = view?.CanUndo ?? false, canRedo = view?.CanRedo ?? false, elementCount = view?.ElementCount ?? 0 });
	}

	[DevFlowAction("od.workflow-designer.redo", Description = "Redo the latest CoreWF workflow edit")]
	public static string Redo()
	{
		var view = Activate();
		var success = view?.RedoLastEdit() == true;
		return JsonSerializer.Serialize(new { success, canUndo = view?.CanUndo ?? false, canRedo = view?.CanRedo ?? false, elementCount = view?.ElementCount ?? 0 });
	}

	[DevFlowAction("od.workflow-designer.validate", Description = "Validate the active CoreWF workflow through its OOP host")]
	public static string Validate()
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { success = false, error = "CoreWF workflow designer is not loaded." });
		try {
			var result = view.ValidateModel();
			return JsonSerializer.Serialize(new { success = true, isValid = result.IsValid, errorCount = result.Diagnostics.Count(diagnostic => !diagnostic.IsWarning), warningCount = result.Diagnostics.Count(diagnostic => diagnostic.IsWarning), diagnostics = result.Diagnostics.Select(diagnostic => new { diagnostic.ElementId, diagnostic.PropertyName, diagnostic.Message, diagnostic.IsWarning }).ToArray() });
		} catch (Exception exception) {
			return JsonSerializer.Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.workflow-designer.run", Description = "Run the active CoreWF workflow through the isolated designer host without opening the input dialog")]
	public static string Run()
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { success = false, error = "CoreWF workflow designer is not loaded." });
		try {
			var result = view.RunModel();
			return JsonSerializer.Serialize(new { success = result.Succeeded, result.Message, outputs = result.Outputs, trace = result.Trace });
		} catch (Exception exception) {
			return JsonSerializer.Serialize(new { success = false, error = exception.Message });
		}
	}

	[DevFlowAction("od.workflow-designer.breakpoint.toggle", Description = "Toggle a breakpoint on the selected CoreWF activity")]
	public static string ToggleBreakpoint()
	{
		var view = Activate();
		var success = view?.ToggleSelectedBreakpointModel() == true;
		var debug = view?.GetDebugModel();
		return JsonSerializer.Serialize(new { success, breakpointElementIds = debug?.BreakpointElementIds.ToArray() ?? Array.Empty<string>() });
	}

	[DevFlowAction("od.workflow-designer.debug.start", Description = "Begin a CoreWF workflow run asynchronously so breakpoints can pause it")]
	public static string StartDebugRun()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.BeginRunModel() == true });
	}

	[DevFlowAction("od.workflow-designer.debug.state", Description = "Read the active CoreWF workflow debug state and any completed run result")]
	public static string DebugState()
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { active = false });
		try {
			var debug = view.GetDebugModel();
			var result = view.CompletedRunModel;
			return JsonSerializer.Serialize(new { active = true, isRunning = debug.IsRunning, isPaused = debug.IsPaused, isIdle = debug.IsIdle, currentElementId = debug.CurrentElementId, breakpointElementIds = debug.BreakpointElementIds, inputs = debug.Inputs, bookmarks = debug.Bookmarks.Select(bookmark => new { name = bookmark.Name, ownerDisplayName = bookmark.OwnerDisplayName }).ToArray(), callStack = debug.CallStack.Select(frame => new { elementId = frame.ElementId, displayName = frame.DisplayName, typeName = frame.TypeName }).ToArray(), liveTrace = debug.Trace, runCompleted = view.IsRunModelCompleted, succeeded = result?.Succeeded, message = result?.Message, trace = result?.Trace });
		} catch (Exception exception) {
			// Keep polling clients on a predictable response contract while an OOP host is
			// restarting or has rejected an overlapping request.
			return JsonSerializer.Serialize(new { active = true, isRunning = false, isPaused = false, isIdle = false, currentElementId = "", breakpointElementIds = Array.Empty<string>(), inputs = new Dictionary<string, string>(), bookmarks = Array.Empty<object>(), callStack = Array.Empty<object>(), liveTrace = Array.Empty<string>(), runCompleted = view.IsRunModelCompleted, succeeded = (bool?)null, error = exception.Message, trace = Array.Empty<string>() });
		}
	}

	[DevFlowAction("od.workflow-designer.debug.continue", Description = "Continue the paused CoreWF workflow debugger")]
	public static string ContinueDebugRun()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.ContinueRunModel() == true });
	}

	[DevFlowAction("od.workflow-designer.bookmark.resume", Description = "Resume an external CoreWF workflow bookmark with an optional string value")]
	public static string ResumeBookmark(string name, string? value = null)
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.ResumeBookmarkModel(name, value) == true, name });
	}

	[DevFlowAction("od.workflow-designer.debug.step", Description = "Step the paused CoreWF workflow debugger to its next activity")]
	public static string StepDebugRun()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.StepRunModel() == true });
	}

	[DevFlowAction("od.workflow-designer.debug.step-over", Description = "Step over the current CoreWF workflow activity")]
	public static string StepOverDebugRun()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.StepOverRunModel() == true });
	}

	[DevFlowAction("od.workflow-designer.debug.step-out", Description = "Step out of the current CoreWF workflow activity")]
	public static string StepOutDebugRun()
	{
		var view = Activate();
		return JsonSerializer.Serialize(new { success = view?.StepOutRunModel() == true });
	}
}

/// <summary>Loads this addin before DevFlow performs its one-time action discovery.</summary>
public sealed class RegisterDevFlowActionsCommand : ICSharpCode.Core.AbstractCommand
{
	public override void Run() { }
}
