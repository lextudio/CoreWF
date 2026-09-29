using ICSharpCode.Core;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>Startup hook that registers the Workflow dialect. See
/// <see cref="WorkflowDesignerDialect"/> for why this is registered rather than assumed.
/// <para>
/// Three constraints shape this type, all of them found the hard way:
/// </para>
/// <list type="bullet">
/// <item>It must be instantiable. An addin <c>Class</c> codon is built by
/// <c>AddIn.CreateObject</c>, so a <c>static</c> class (abstract) aborts startup with a
/// MissingMethodException before any window appears.</item>
/// <item>It must be an <see cref="System.Windows.Input.ICommand"/>. The
/// <c>/SharpDevelop/Workbench/AutostartAfterWorkbenchInitialized</c> path casts every item it builds to
/// ICommand, so a plain class fails with an InvalidCastException.</item>
/// <item>It must run late. <c>Register()</c> touches a type in <c>ICSharpCode.SharpDevelop</c>,
/// and an addin references the host's assemblies with <c>Private="false"</c>, so that reference
/// only resolves once the workbench is up; registering from the earlier
/// <c>/SharpDevelop/Autostart</c> path JITs the call during
/// <c>CoreStartup.RunInitialization</c> and fails with a FileNotFoundException on
/// <c>ICSharpCode.SharpDevelop</c>.</item>
/// </list>
/// <para>The path is <c>/SharpDevelop/Workbench/AutostartAfterWorkbenchInitialized</c>:
/// <c>CallHelper.RunWorkbenchInitializedCommands</c> builds each codon and calls
/// <see cref="Run"/>. A mistyped path is never built and fails silently.</para></summary>
public sealed class RegisterWorkflowDialect : AbstractCommand
{
	public override void Run()
	{
		WorkflowDesignerDialect.Register();
	}
}
