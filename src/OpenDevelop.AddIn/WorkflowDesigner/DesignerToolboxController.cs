using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Runtime-neutral catalogue, filter and selection state for a designer Toolbox pad.
/// <para>
/// This used to live in OpenDevelop's Designer.Shell; the host replaced it with the shared
/// per-document <c>SharedToolbox</c> (commit cf95a72f81), which owns a single ListBox and only
/// shows the scopes its active document registered. The workflow designer keeps its own
/// self-contained Toolbox pad (one document shape, no tool-service scopes), so it carries this
/// small state machine itself instead of joining the shared pad. Behaviour is unchanged from the
/// removed class so the existing toolbox UI/tests keep their semantics.
/// </para></summary>
sealed class DesignerToolboxController
{
	IReadOnlyList<DesignerToolboxItemInfo> allItems = Array.Empty<DesignerToolboxItemInfo>();
	IReadOnlyList<DesignerToolboxItemInfo> visibleItems = Array.Empty<DesignerToolboxItemInfo>();
	string filterText = "";
	string? preferredKey;

	public event EventHandler? ItemsChanged;
	public event EventHandler? SelectionChanged;
	public IReadOnlyList<DesignerToolboxItemInfo> AllItems => allItems;
	public IReadOnlyList<DesignerToolboxItemInfo> VisibleItems => visibleItems;
	public DesignerToolboxItemInfo? SelectedItem { get; private set; }
	public string FilterText => filterText;

	public void SetItems(IEnumerable<DesignerToolboxItemInfo>? items)
	{
		preferredKey ??= SelectedItem == null ? null : Key(SelectedItem);
		allItems = items?.Where(item => item != null)
			.GroupBy(Key, StringComparer.Ordinal).Select(group => group.First()).ToArray()
			?? Array.Empty<DesignerToolboxItemInfo>();
		ApplyFilter();
		RestorePreferredSelection();
	}

	public void Filter(string? text)
	{
		filterText = text?.Trim() ?? "";
		ApplyFilter();
		RestorePreferredSelection();
	}

	public bool Select(string? key)
	{
		var item = key == null ? null : visibleItems.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(Key(candidate), key));
		if (key != null && item == null) return false;
		preferredKey = key;
		SetSelected(item);
		return true;
	}

	void RestorePreferredSelection()
	{
		var item = preferredKey == null ? null : visibleItems.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(Key(candidate), preferredKey));
		SetSelected(item);
	}

	void SetSelected(DesignerToolboxItemInfo? item)
	{
		if (ReferenceEquals(item, SelectedItem)) return;
		SelectedItem = item;
		SelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	void ApplyFilter()
	{
		visibleItems = string.IsNullOrEmpty(filterText) ? allItems : allItems.Where(item =>
			Contains(item.Name) || Contains(item.DisplayName) || Contains(item.TypeName) || Contains(item.Category)).ToArray();
		ItemsChanged?.Invoke(this, EventArgs.Empty);
	}

	bool Contains(string? value) => value?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true;
	static string Key(DesignerToolboxItemInfo? item) => item == null ? "" : !string.IsNullOrEmpty(item.TypeName) ? item.TypeName : item.Name;
}
