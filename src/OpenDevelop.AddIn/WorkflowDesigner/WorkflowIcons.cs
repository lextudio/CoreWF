using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ICSharpCode.WorkflowDesigner
{
	/// <summary>
	/// Loads this addin's toolbar icons from the VS2017 Image Library's own pre-converted XAML
	/// vector format (Microsoft's "AiToXaml" output, embedded under Icons/*.xaml - no SVG/PNG
	/// rasterizing involved). Each file is a
	/// <c>&lt;Viewbox&gt;&lt;Rectangle Fill="{DrawingBrush}"/&gt;&lt;/Viewbox&gt;</c> wrapper around a
	/// <see cref="DrawingGroup"/>; this unwraps it to a plain <see cref="ImageSource"/>.
	///
	/// Deliberately NOT ICSharpCode.SharpDevelop.IconService: that resolves against resources
	/// embedded in the host's ICSharpCode.Core.Presentation assembly, so every icon this addin
	/// wanted would have to be added to - and shipped by - OpenDevelop itself. This addin lives in
	/// its own repository, so it carries its own icons instead. Mirrors ILSpyAddIn's VsIconLoader.
	/// </summary>
	internal static class WorkflowIcons
	{
		static readonly ConcurrentDictionary<string, ImageSource?> cache = new();

		/// <summary>
		/// Returns the icon embedded as "Icons/{name}_16x.xaml", or null when there is no such
		/// resource - callers fall back to a text-only button rather than failing, so a missing or
		/// misspelled icon degrades the toolbar instead of breaking the designer.
		/// </summary>
		public static ImageSource? Load(string name) => cache.GetOrAdd(name, LoadCore);

		static ImageSource? LoadCore(string name)
		{
			try {
				using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Icons.{name}_16x.xaml");
				if (stream == null)
					return null;
				if (XamlReader.Load(stream) is not Viewbox { Child: Rectangle { Fill: DrawingBrush brush } })
					return null;
				var image = new DrawingImage(brush.Drawing);
				if (image.CanFreeze)
					image.Freeze();
				return image;
			} catch (Exception) {
				// A malformed icon must never take down the designer's toolbar.
				return null;
			}
		}
	}
}
