using System;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>One entry in the expression editor's completion window. The host returns completions
/// as plain strings (workflow variables, arguments, CoreWF types and language keywords), so this
/// carries no symbol model of its own - it exists to put those strings into AvalonEdit's
/// completion list, which binds against <see cref="ICompletionData"/>.</summary>
sealed class WorkflowCompletionData : ICompletionData
{
	public WorkflowCompletionData(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

	public ImageSource Image => null;
	public string Text { get; }
	public object Content => Text;
	public object Description => Text;
	public double Priority => 0;

	public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
		textArea.Document.Replace(completionSegment, Text);
}
