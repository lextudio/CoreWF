using System;
using System.IO;
using System.Xml;

using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>
/// Registers the Workflow dialect and the child host that serves it, so the workbench routes
/// activity XAML to this addin. This replaces the previous arrangement, where this addin relied
/// on <c>WpfSecondaryDisplayBinding.CanAttachTo</c> happening to exclude a root element named
/// <c>Activity</c> - which worked, but only because of a coincidence between two independently
/// maintained files, and would have broken silently if WPF's exclusion list were ever trimmed.
/// </summary>
public static class WorkflowDesignerDialect
{
	public const string Dialect = "Workflow";

	public const string HostAssemblyName = "WorkflowDesigner.Host.dll";

	const string ActivitiesNamespace = "http://schemas.microsoft.com/netfx/2009/xaml/activities";

	public static void Register()
	{
		XamlDialectRegistry.Register(new XamlDialectRegistration(Dialect, Matches, HostAssemblyName));
	}

	/// <summary>
	/// The same root element/namespace sniff <see cref="WorkflowDesignerDisplayBinding"/> uses.
	/// The markup is the deciding evidence, matching how WPF and WinUI identify themselves.
	/// </summary>
	public static bool Matches(string xamlFileName)
	{
		if (string.IsNullOrEmpty(xamlFileName) || !string.Equals(Path.GetExtension(xamlFileName), ".xaml", StringComparison.OrdinalIgnoreCase))
			return false;
		if (!File.Exists(xamlFileName))
			return false;
		try {
			using var reader = new XmlTextReader(new StringReader(File.ReadAllText(xamlFileName))) { XmlResolver = null };
			while (reader.Read() && reader.NodeType != XmlNodeType.Element) { }
			return reader.LocalName == "Activity" && reader.NamespaceURI == ActivitiesNamespace;
		} catch (Exception) {
			return false;
		}
	}
}
