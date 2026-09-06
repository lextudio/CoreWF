using System.Activities;
using System.Activities.Expressions;
using System.Activities.Statements;
using System.Activities.Tracking;
using System.Activities.Validation;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.VisualBasic;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WorkflowDesigner.Host;

sealed class WorkflowDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly DesignerDocumentRegistry<DocumentSession> documents = new();
	List<string> clipboard = new();
	string sessionId = "";

	public WorkflowDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
			throw new UnauthorizedAccessException();
		if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException();
		documents.Initialize(sessionId);
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "CoreWF ActivityBuilder", ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")] public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	[JsonRpcMethod("session/update")] public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot);

	[JsonRpcMethod("design/capabilities")]
	public DesignerCapabilities GetCapabilities(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Capabilities(Get(sessionId, documentId).Snapshot);
	}

	[JsonRpcMethod("workflow/refresh-capabilities")]
	public DesignerCapabilities RefreshCapabilities(string sessionId, string documentId, DesignerDocumentSnapshot snapshot)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		// A project build changes the activity assemblies, not the in-memory XAML document.
		// Keep its undo history and unsaved edits intact while refreshing only the safe metadata
		// consumed by the toolbox/expression catalog.
		session.Snapshot.ProjectFileName = snapshot.ProjectFileName;
		session.Snapshot.TargetFramework = snapshot.TargetFramework;
		session.Snapshot.ProjectAssemblyPath = snapshot.ProjectAssemblyPath;
		session.Snapshot.ReferencedAssemblyPaths = snapshot.ReferencedAssemblyPaths;
		session.Snapshot.ProjectCodeMode = snapshot.ProjectCodeMode;
		session.CompletionTypeNames = null;
		return Capabilities(session.Snapshot);
	}

	DesignerCapabilities Capabilities(DesignerDocumentSnapshot snapshot)
	{
		return new DesignerCapabilities {
			Runtime = "CoreWF ActivityBuilder", Version = typeof(Activity).Assembly.GetName().Version?.ToString() ?? "", SessionId = sessionId,
			Toolbox = StandardActivities()
				.Concat(VirtualToolboxItems())
				.Concat(string.Equals(snapshot.ProjectCodeMode, "Enabled", StringComparison.OrdinalIgnoreCase) ? ProjectActivities(snapshot) : Enumerable.Empty<DesignerToolboxItemInfo>())
				.OrderBy(item => item.Category, StringComparer.Ordinal).ThenBy(item => item.DisplayName, StringComparer.Ordinal)
				.ToList()
		};
	}

	static IEnumerable<DesignerToolboxItemInfo> StandardActivities() => ActivityTypes(typeof(Sequence).Assembly)
		.Where(type => type.Namespace == typeof(Sequence).Namespace)
		.Select(type => ToolboxItem(type, "CoreWF"));

	/// <summary>Toolbox entries that are not CLR activity types of their own: the structural child
	/// shapes a container owns (a State, a Catch, a Switch case) which the designer creates through
	/// its parent rather than by constructing a type.</summary>
	static IEnumerable<DesignerToolboxItemInfo> VirtualToolboxItems() =>
		new[] {
			("State", "State"), ("Transition", "Transition"), ("FlowDecision", "Flow Decision"),
			("Catch", "Catch (Exception)"), ("PickBranch", "Pick Branch"),
			("SwitchCase", "Switch Case"), ("SwitchDefault", "Switch Default")
		}.Select(entry => new DesignerToolboxItemInfo { Name = entry.Item1, DisplayName = entry.Item2, TypeName = entry.Item1, Category = "CoreWF" });

	static IEnumerable<DesignerToolboxItemInfo> ProjectActivities(DesignerDocumentSnapshot snapshot)
	{
		var paths = new[] { snapshot.ProjectAssemblyPath }.Concat(snapshot.ReferencedAssemblyPaths)
			.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths) {
			var assembly = LoadProjectAssembly(path);
			if (assembly == null) continue;
			var category = string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase) ? "Project" : "Referenced activity";
			foreach (var type in ActivityTypes(assembly)) yield return ToolboxItem(type, category);
		}
	}

	static readonly Dictionary<string, (DateTime Stamp, Assembly? Assembly)> projectAssemblies = new(StringComparer.OrdinalIgnoreCase);
	static readonly HashSet<string> projectProbePaths = new(StringComparer.OrdinalIgnoreCase);
	static bool projectResolverInstalled;

	/// <summary>Loads a project or referenced assembly for the toolbox. The bytes are read rather
	/// than <c>Assembly.LoadFrom</c>-ed for two reasons: LoadFrom keeps the build output locked,
	/// and it returns the assembly already loaded from that path, so a rebuilt project kept
	/// showing its previous activity list until the host restarted. Results are cached per
	/// (path, write time) so a refresh that changed nothing does not reload anything.</summary>
	static Assembly? LoadProjectAssembly(string path)
	{
		DateTime stamp;
		try { stamp = File.GetLastWriteTimeUtc(path); } catch { return null; }
		lock (projectAssemblies) {
			if (projectAssemblies.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Assembly;
			InstallProjectAssemblyResolver(Path.GetDirectoryName(path));
			Assembly? assembly = null;
			try { assembly = Assembly.Load(File.ReadAllBytes(path)); }
			catch (Exception exception) {
				// A referenced assembly the designer cannot read is not fatal - the rest of the
				// toolbox still works - but silently dropping it made a missing custom activity
				// impossible to explain.
				Console.Error.WriteLine("WorkflowDesigner: could not load activities from " + path + ": " + exception.Message);
			}
			projectAssemblies[path] = (stamp, assembly);
			return assembly;
		}
	}

	/// <summary>Lets a byte-loaded project assembly find its own dependencies, which sit next to
	/// the file it was read from rather than next to the designer host.</summary>
	static void InstallProjectAssemblyResolver(string? probePath)
	{
		if (!string.IsNullOrEmpty(probePath)) projectProbePaths.Add(probePath!);
		if (projectResolverInstalled) return;
		projectResolverInstalled = true;
		AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
			var simpleName = new AssemblyName(args.Name).Name;
			if (string.IsNullOrEmpty(simpleName)) return null;
			string[] directories;
			lock (projectAssemblies) directories = projectProbePaths.ToArray();
			foreach (var directory in directories) {
				var candidate = Path.Combine(directory, simpleName + ".dll");
				if (!File.Exists(candidate)) continue;
				try { return Assembly.Load(File.ReadAllBytes(candidate)); } catch { }
			}
			return null;
		};
	}

	static IEnumerable<Type> ActivityTypes(Assembly assembly)
	{
		IEnumerable<Type> types;
		try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException exception) { types = exception.Types.Where(type => type != null)!; }
		// A single-parameter generic activity (ForEach<T>, Switch<T>, ...) belongs in the toolbox as
		// an open definition: the designer asks for the type argument when it is dropped. Wider
		// generic shapes have no such prompt and stay out.
		return types.Where(type => !type.IsAbstract && typeof(Activity).IsAssignableFrom(type)
			&& (type.IsGenericTypeDefinition
				? type.GetGenericArguments().Length == 1
				: !type.ContainsGenericParameters && type.GetConstructor(Type.EmptyTypes) != null));
	}

	static DesignerToolboxItemInfo ToolboxItem(Type type, string category)
	{
		// Activity authors should be able to make their toolbox entry useful without
		// implementing an OpenDevelop-specific interface. These standard component-model
		// attributes also work for activities shared with other WF hosts.
		var displayName = type.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName;
		var declaredCategory = type.GetCustomAttribute<System.ComponentModel.CategoryAttribute>()?.Category;
		var description = type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
		var isOpenGeneric = type.IsGenericTypeDefinition;
		var bareName = isOpenGeneric && type.Name.IndexOf('`') is var tick and >= 0 ? type.Name[..tick] : type.Name;
		var readableName = string.IsNullOrWhiteSpace(displayName) ? HumanizeTypeName(bareName) : displayName;
		return new DesignerToolboxItemInfo {
			Name = bareName,
			// The trailing <T> is the designer's signal - and the user's - that dropping this item
			// asks for a type argument first.
			DisplayName = isOpenGeneric ? readableName + " <T>" : readableName,
			TypeName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
			Category = string.IsNullOrWhiteSpace(declaredCategory) ? category : declaredCategory,
			Template = description ?? "",
			XamlNamespace = type.Namespace ?? ""
		};
	}

	static string HumanizeTypeName(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)([A-Z])", " $1");

	DesignerSessionState Load(DesignerDocumentSnapshot snapshot)
	{
		documents.ValidateSession(snapshot.SessionId);
		var session = documents.GetOrAdd(snapshot.SessionId, snapshot.DocumentId, () => new DocumentSession(snapshot.DocumentId));
		session.Version = snapshot.Version;
		var file = snapshot.Files.FirstOrDefault(f => f.Kind == "Designer") ?? snapshot.Files.FirstOrDefault();
		session.FileName = file?.FileName ?? snapshot.DesignerFileName;
		session.Snapshot = snapshot;
		session.CompletionTypeNames = null;
		session.Document.Reset(file?.Text ?? "");
		session.Undo.Clear();
		session.Redo.Clear();
		return State(session);
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.SetProperty(elementId, propertyName, value))
			throw new InvalidOperationException("Workflow property mutation was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName)
		=> SetProperty(sessionId, documentId, baseVersion, elementId, "$displayName", newName);

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		var addedId = session.Document.AddChild(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName);
		if (addedId == null) throw new InvalidOperationException("Workflow element insertion was rejected.");
		RecordMutation(session, before);
		var state = State(session);
		state.CreatedElementId = addedId;
		return state;
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveMany(elementIds))
			throw new InvalidOperationException("Workflow element deletion was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/copy-elements")]
	public bool CopyElements(string sessionId, string documentId, string[] elementIds)
	{
		documents.ValidateSession(sessionId);
		var document = Get(sessionId, documentId).Document;
		if (!document.Copy(elementIds)) return false;
		clipboard = document.Clipboard.ToList();
		return true;
	}

	[JsonRpcMethod("design/cut-elements")]
	public DesignerSessionState CutElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		if (!session.Document.Copy(elementIds)) throw new InvalidOperationException("The selected workflow item cannot be cut.");
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveMany(elementIds)) throw new InvalidOperationException("Workflow cut was rejected.");
		clipboard = session.Document.Clipboard.ToList();
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/paste-elements")]
	public DesignerSessionState PasteElements(string sessionId, string documentId, long baseVersion, string parentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		var pasted = session.Document.Paste(parentId, clipboard);
		if (pasted == null || pasted.Count == 0) throw new InvalidOperationException("Workflow paste was rejected by the selected container.");
		RecordMutation(session, before);
		var state = State(session);
		state.CreatedElementId = pasted[^1];
		return state;
	}

	[JsonRpcMethod("workflow/move-element")]
	public DesignerSessionState MoveElement(string sessionId, string documentId, long baseVersion, string elementId, int offset)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		var movedId = session.Document.MoveSibling(elementId, offset);
		if (movedId == null) throw new InvalidOperationException("The selected activity cannot move in this container.");
		RecordMutation(session, before);
		var state = State(session);
		state.CreatedElementId = movedId;
		return state;
	}

	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(sessionId, documentId), baseVersion, undo: true); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(sessionId, documentId), baseVersion, undo: false); }

	DesignerSessionState RestoreHistory(DocumentSession session, long baseVersion, bool undo)
	{
		EnsureVersion(session, baseVersion);
		var source = undo ? session.Undo : session.Redo;
		var destination = undo ? session.Redo : session.Undo;
		if (source.Count == 0) throw new InvalidOperationException(undo ? "Nothing to undo." : "Nothing to redo.");
		destination.Push(session.Document.ToXaml());
		session.Document.Reset(source.Pop());
		if (!session.Document.LastParseSucceeded) throw new InvalidOperationException("Workflow history could not be restored.");
		session.Version++;
		return State(session);
	}

	static void RecordMutation(DocumentSession session, string before)
	{
		session.Undo.Push(before);
		session.Redo.Clear();
		session.Version++;
	}

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		return new DesignerEditSet {
			SessionId = sessionId, DocumentId = documentId, BaseVersion = session.Version,
			Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Document.ToXaml() } }
		};
	}

	[JsonRpcMethod("session/close")]
	public object Close(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		documents.Remove(sessionId, documentId, _ => { });
		return new();
	}

	[JsonRpcMethod("workflow/get-arguments")]
	public List<WorkflowArgumentInfo> GetArguments(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(sessionId, documentId).Document.GetArguments().Select(argument => new WorkflowArgumentInfo {
			Name = argument.Name, TypeName = argument.TypeName, Direction = argument.Direction, DefaultValue = argument.DefaultValue
		}).ToList();
	}

	[JsonRpcMethod("workflow/add-argument")]
	public DesignerSessionState AddArgument(string sessionId, string documentId, long baseVersion, string name, string typeName, string defaultValue, string direction = "In")
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.AddArgument(name, typeName, defaultValue, direction)) throw new InvalidOperationException("Workflow argument creation was rejected. Check its name, direction, type and default value.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/remove-argument")]
	public DesignerSessionState RemoveArgument(string sessionId, string documentId, long baseVersion, string name)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveArgument(name)) throw new InvalidOperationException("Workflow argument removal was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/update-argument")]
	public DesignerSessionState UpdateArgument(string sessionId, string documentId, long baseVersion, string oldName, string newName, string typeName, string defaultValue, string direction = "In")
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.UpdateArgument(oldName, newName, typeName, defaultValue, direction)) throw new InvalidOperationException("Workflow argument update was rejected. Check its name, direction, type and default value.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/get-variables")]
	public List<WorkflowVariableInfo> GetVariables(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(sessionId, documentId).Document.GetVariables().Select(variable => new WorkflowVariableInfo { Name = variable.Name, TypeName = variable.TypeName, Scope = variable.Scope, ScopeId = variable.ScopeId, DefaultValue = variable.DefaultValue }).ToList();
	}

	[JsonRpcMethod("workflow/get-variable-scopes")]
	public List<WorkflowVariableInfo> GetVariableScopes(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(sessionId, documentId).Document.GetVariableScopes().Select(scope => new WorkflowVariableInfo { Scope = scope.Scope, ScopeId = scope.ScopeId }).ToList();
	}

	[JsonRpcMethod("workflow/add-variable")]
	public DesignerSessionState AddVariable(string sessionId, string documentId, long baseVersion, string name, string typeName, string defaultValue, string scopeId = "")
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.AddVariable(name, typeName, defaultValue, scopeId)) throw new InvalidOperationException("Variable creation was rejected. Check its scope, name, type and default value.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/update-variable")]
	public DesignerSessionState UpdateVariable(string sessionId, string documentId, long baseVersion, string oldName, string newName, string typeName, string defaultValue, string scopeId = "")
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.UpdateVariable(oldName, newName, typeName, defaultValue, scopeId)) throw new InvalidOperationException("Variable update was rejected. Check its name, type and default value.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/remove-variable")]
	public DesignerSessionState RemoveVariable(string sessionId, string documentId, long baseVersion, string name, string scopeId = "")
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveVariable(name, scopeId)) throw new InvalidOperationException("Workflow variable removal was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/get-imports")]
	public List<string> GetImports(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(sessionId, documentId).Document.GetImports().ToList();
	}

	[JsonRpcMethod("workflow/add-import")]
	public DesignerSessionState AddImport(string sessionId, string documentId, long baseVersion, string namespaceName)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.AddImport(namespaceName)) throw new InvalidOperationException("'" + namespaceName + "' is not a namespace this workflow can import, or it is already imported.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/remove-import")]
	public DesignerSessionState RemoveImport(string sessionId, string documentId, long baseVersion, string namespaceName)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveImport(namespaceName)) throw new InvalidOperationException("The workflow does not import '" + namespaceName + "'.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/validate-expression")]
	public WorkflowExpressionValidation ValidateExpression(string sessionId, string documentId, string expression, string? elementId = null)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		var isVisualBasic = expression.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
		var text = expression.StartsWith("=", StringComparison.Ordinal) ? expression[(isVisualBasic ? 4 : 1)..] : expression;
		if (string.IsNullOrWhiteSpace(text)) return new WorkflowExpressionValidation { Diagnostics = { "Expression cannot be empty." } };
		var variables = session.Document.GetExpressionSymbolsVisibleAt(elementId).Where(variable => System.Text.RegularExpressions.Regex.IsMatch(variable.Name, "^[A-Za-z_][A-Za-z0-9_]*$")).GroupBy(variable => variable.Name, StringComparer.Ordinal).Select(group => group.First()).ToArray();
		// The document's imports are what CoreWF compiles the expression against at run time, so
		// validating without them rejected every expression that is not fully qualified.
		var imports = ResolvableImports(session);
		var usings = isVisualBasic
			? string.Concat(imports.Select(name => "Imports " + name + "\n"))
			: string.Concat(imports.Select(name => "using " + name + "; "));
		// Visual Basic requires Option statements to precede any Imports, so the two languages
		// place the import block differently.
		var prefix = (isVisualBasic ? "" : usings) + (isVisualBasic ? "Option Strict Off\n" + usings + "Module WorkflowExpression\nFunction Value() As Object\n" + string.Join("\n", variables.Select(variable => "Dim " + variable.Name + " As " + ExpressionVariableTypeName(session, variable.TypeName) + " = Nothing")) + "\nReturn (" : "class WorkflowExpression { object Value() { " + string.Join(" ", variables.Select(variable => ExpressionVariableTypeName(session, variable.TypeName) + " " + variable.Name + " = default;")) + " return (");
		var suffix = isVisualBasic ? ")\nEnd Function\nEnd Module" : "); } }";
		var source = prefix + text + suffix;
		Compilation compilation = isVisualBasic ? VisualBasicCompilation.Create("WorkflowExpression", new[] { VisualBasicSyntaxTree.ParseText(source) }, MetadataReferences(session), new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)) : CSharpCompilation.Create("WorkflowExpression", new[] { CSharpSyntaxTree.ParseText(source) }, MetadataReferences(session), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
		var details = errors.Select(diagnostic => ToExpressionDiagnostic(diagnostic, prefix.Length, text)).Where(detail => detail != null).Cast<WorkflowExpressionDiagnostic>().ToList();
		var diagnostics = errors.Select(diagnostic => diagnostic.GetMessage()).Distinct(StringComparer.Ordinal).ToList();
		return new WorkflowExpressionValidation { IsValid = errors.Length == 0, Diagnostics = diagnostics, Details = details };
	}

	[JsonRpcMethod("workflow/validate")]
	public WorkflowValidationResult ValidateWorkflow(string sessionId, string documentId, long baseVersion)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var rootActivity = session.Document.Root;
		if (rootActivity == null) return new WorkflowValidationResult { Diagnostics = { new WorkflowValidationDiagnostic { Message = "No workflow implementation is loaded." } } };
		try {
			// Validate the visual root directly. The DynamicActivity wrapper used by Run is
			// necessary for workflow arguments, but CoreWF reports inner violations against
			// that synthetic wrapper instead of the design node that the user can select.
			var paths = BuildRuntimeIds(rootActivity);
			var results = ActivityValidationServices.Validate(rootActivity);
			var diagnostics = results.Errors.Concat(results.Warnings).Select(error => new WorkflowValidationDiagnostic {
				Message = error.Message,
				ElementId = ValidationElementPath(error, paths),
				PropertyName = error.PropertyName ?? "",
				IsWarning = error.IsWarning
			}).ToList();
			return new WorkflowValidationResult { IsValid = results.Errors.Count == 0, Diagnostics = diagnostics };
		} catch (Exception exception) {
			return new WorkflowValidationResult { Diagnostics = { new WorkflowValidationDiagnostic { Message = exception.GetBaseException().Message } } };
		}
	}

	static string ValidationElementPath(ValidationError error, IReadOnlyDictionary<string, string> paths)
	{
		foreach (var activity in new[] { error.Source, error.SourceDetail as Activity })
			if (activity != null && !string.IsNullOrEmpty(activity.Id) && paths.TryGetValue(activity.Id, out var path)) return path;
		return "";
	}

	static WorkflowExpressionDiagnostic? ToExpressionDiagnostic(Diagnostic diagnostic, int expressionStart, string expression)
	{
		if (!diagnostic.Location.IsInSource) return null;
		var relativeStart = diagnostic.Location.SourceSpan.Start - expressionStart;
		if (relativeStart < 0 || relativeStart > expression.Length) return null;
		var before = expression[..relativeStart];
		var lastNewLine = before.LastIndexOf('\n');
		return new WorkflowExpressionDiagnostic {
			Code = diagnostic.Id,
			Message = diagnostic.GetMessage(),
			Line = before.Count(character => character == '\n') + 1,
			Column = relativeStart - lastNewLine,
			Length = Math.Min(diagnostic.Location.SourceSpan.Length, expression.Length - relativeStart)
		};
	}

	static string ExpressionVariableTypeName(DocumentSession session, string? typeName)
	{
		var type = ResolveVariableCompletionType(typeName);
		if (type != null) return type.FullName ?? "object";
		if (!System.Text.RegularExpressions.Regex.IsMatch(typeName ?? "", "^[A-Za-z_][A-Za-z0-9_.]*$")) return "object";
		if (typeName!.Contains('.', StringComparison.Ordinal)) return typeName;
		var compilation = CSharpCompilation.Create("WorkflowExpressionVariableTypes", references: MetadataReferences(session));
		return DescendantTypes(compilation.GlobalNamespace).FirstOrDefault(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal))?
			.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal) ?? "object";
	}


	[JsonRpcMethod("workflow/complete-expression")]
	public WorkflowExpressionCompletion CompleteExpression(string sessionId, string documentId, string expression, string prefix, string? elementId = null)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		var roslyn = CompleteWithRoslyn(session, expression, prefix, elementId);
		var visualBasic = expression.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
		var terms = visualBasic ? new[] { "True", "False", "Nothing", "If", "AndAlso", "OrElse", "Not", "String", "Math" } : new[] { "true", "false", "null", "this", "new", "nameof", "string", "Math" };
		var owner = System.Text.RegularExpressions.Regex.Match(expression[..Math.Max(0, expression.Length - prefix.Length)], @"([A-Za-z_][A-Za-z0-9_.]*)\.$").Groups[1].Value;
		if (ResolveCompletionType(owner, visualBasic) is { } staticType) return new WorkflowExpressionCompletion { Items = staticType.GetMembers(BindingFlags.Public | BindingFlags.Static).Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Field).Select(member => member.Name).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Concat(roslyn.Items).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
		var variables = session.Document.GetExpressionSymbolsVisibleAt(elementId).ToArray();
		var typeName = variables.FirstOrDefault(variable => string.Equals(variable.Name, owner, StringComparison.OrdinalIgnoreCase)).TypeName;
		var instanceType = session.Document.GetVariableRuntimeTypeVisibleAt(owner, elementId) ?? ResolveVariableCompletionType(typeName);
		if (instanceType != null) return new WorkflowExpressionCompletion { Items = CompleteRuntimeInstanceMembers(instanceType, prefix).Concat(roslyn.Items).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
		var members = CompletionInstanceMembers(session, typeName, prefix); if (members.Count != 0) return new WorkflowExpressionCompletion { Items = members.Concat(roslyn.Items).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
		members = CompletionStaticMembers(session, owner, prefix); if (members.Count != 0) return new WorkflowExpressionCompletion { Items = members.Concat(roslyn.Items).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
		return new WorkflowExpressionCompletion { Items = terms.Concat(variables.Select(variable => variable.Name)).Concat(StandardActivities().Select(item => item.DisplayName)).Concat(roslyn.Items).Concat(CompletionTypeNames(session)).Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
	}

	static WorkflowExpressionCompletion CompleteWithRoslyn(DocumentSession session, string expression, string prefix, string? elementId)
	{
		try {
			var visualBasic = expression.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
			var text = expression.StartsWith("=", StringComparison.Ordinal) ? expression[(visualBasic ? 4 : 1)..] : expression;
			var variables = session.Document.GetExpressionSymbolsVisibleAt(elementId)
				.Where(variable => System.Text.RegularExpressions.Regex.IsMatch(variable.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))
				.GroupBy(variable => variable.Name, StringComparer.Ordinal).Select(group => group.First()).ToArray();
			var before = visualBasic
				? "Option Strict Off\nModule WorkflowExpression\nFunction Value() As Object\n" + string.Join("\n", variables.Select(variable => "Dim " + variable.Name + " As " + ExpressionVariableTypeName(session, variable.TypeName) + " = Nothing")) + "\nReturn ("
				: "class WorkflowExpression { object Value() { " + string.Join(" ", variables.Select(variable => ExpressionVariableTypeName(session, variable.TypeName) + " " + variable.Name + " = default;")) + " return (";
			var after = visualBasic ? ")\nEnd Function\nEnd Module" : "); } }";
			using var workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
			var projectId = ProjectId.CreateNewId();
			var documentId = DocumentId.CreateNewId(projectId);
			var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "WorkflowExpression", "WorkflowExpression", visualBasic ? LanguageNames.VisualBasic : LanguageNames.CSharp,
				metadataReferences: MetadataReferences(session).ToArray()))
				.AddDocument(documentId, "WorkflowExpression." + (visualBasic ? "vb" : "cs"), before + text + after);
			if (!workspace.TryApplyChanges(solution)) return new WorkflowExpressionCompletion();
			var document = workspace.CurrentSolution.GetDocument(documentId);
			if (document == null) return new WorkflowExpressionCompletion();
			var completion = CompletionService.GetService(document);
			if (completion == null) return new WorkflowExpressionCompletion();
			var result = completion.GetCompletionsAsync(document, before.Length + text.Length).GetAwaiter().GetResult();
			if (result == null) return new WorkflowExpressionCompletion();
			return new WorkflowExpressionCompletion { Items = result.ItemsList.Select(item => item.DisplayText)
				.Where(item => string.IsNullOrEmpty(prefix) || item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).Take(128).ToList() };
		} catch {
			// Roslyn features are intentionally optional at runtime: fall back to the lightweight,
			// metadata-only completion path if a deployment is missing a feature assembly.
			return new WorkflowExpressionCompletion();
		}
	}

	static List<string> CompleteRuntimeInstanceMembers(Type type, string prefix)
	{
		var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
			.Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property)
			.Select(member => member.Name);

		// Workflow expressions commonly operate on collection variables.  Extension
		// methods are not reflected as instance members, but they are valid C# calls
		// and therefore must appear in the designer completion list.
		if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) {
			members = members.Concat(typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(method => method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false))
				.Select(method => method.Name));
		}

		return members.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(64).ToList();
	}

	static List<string> CompletionInstanceMembers(DocumentSession session, string? typeName, string prefix)
	{
		if (string.IsNullOrWhiteSpace(typeName)) return new List<string>();
		var compilation = CSharpCompilation.Create("WorkflowExpressionInstanceCompletion", references: MetadataReferences(session));
		return DescendantTypes(compilation.GlobalNamespace)
			.Where(type => string.Equals(type.Name, typeName, StringComparison.Ordinal))
			.SelectMany(type => type.GetMembers())
			.Where(member => member.DeclaredAccessibility == Accessibility.Public && !member.IsStatic && member.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field)
			.Select(member => member.Name).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(64).ToList();
	}

	static List<string> CompletionStaticMembers(DocumentSession session, string typeName, string prefix)
	{
		if (string.IsNullOrEmpty(typeName)) return new List<string>();
		var compilation = CSharpCompilation.Create("WorkflowExpressionMemberCompletion", references: MetadataReferences(session));
		var type = compilation.GetTypeByMetadataName(typeName);
		if (type == null) return new List<string>();
		return type.GetMembers().Where(member => member.DeclaredAccessibility == Accessibility.Public && member.IsStatic && member.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field)
			.Select(member => member.Name).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(64).ToList();
	}

	/// <summary>
	/// Gets types from the same metadata graph used by expression validation.  In particular this
	/// does not use <see cref="Assembly.LoadFrom(string)"/>: completion must not run user code just
	/// because a workflow refers to a project or a package assembly.
	/// </summary>
	static IEnumerable<string> CompletionTypeNames(DocumentSession session)
	{
		if (session.CompletionTypeNames != null) return session.CompletionTypeNames;
		var compilation = CSharpCompilation.Create("WorkflowExpressionCompletion", references: MetadataReferences(session));
		return session.CompletionTypeNames = DescendantTypes(compilation.GlobalNamespace)
			.Where(type => type.DeclaredAccessibility == Accessibility.Public && !type.IsImplicitlyDeclared && !type.IsGenericType)
			.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal))
			.Where(name => name.IndexOf('.') >= 0)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	static IEnumerable<INamedTypeSymbol> DescendantTypes(INamespaceSymbol @namespace)
	{
		foreach (var type in @namespace.GetTypeMembers()) {
			yield return type;
			foreach (var nested in DescendantTypes(type)) yield return nested;
		}
		foreach (var child in @namespace.GetNamespaceMembers())
			foreach (var type in DescendantTypes(child)) yield return type;
	}

	static IEnumerable<INamedTypeSymbol> DescendantTypes(INamedTypeSymbol type)
	{
		foreach (var nested in type.GetTypeMembers()) {
			yield return nested;
			foreach (var descendant in DescendantTypes(nested)) yield return descendant;
		}
	}

	static Type? ResolveCompletionType(string name, bool visualBasic) => name switch {
		"Math" => typeof(Math), "DateTime" => typeof(DateTime), "String" when visualBasic => typeof(string),
		"string" when !visualBasic => typeof(string), _ => null
	};

	static Type? ResolveVariableCompletionType(string? name) => name?.Trim().ToLowerInvariant() switch {
		"string" => typeof(string), "int" or "int32" => typeof(int), "bool" or "boolean" => typeof(bool),
		"double" => typeof(double), "decimal" => typeof(decimal), "datetime" => typeof(DateTime), _ => null
	};

	/// <summary>The document's imports, minus any namespace the expression compilation cannot see.
	/// The metadata references available here are the trusted platform assemblies plus the
	/// project's own, which need not cover every namespace a workflow declares; emitting a using
	/// for a namespace that is not there would fail every expression with an unrelated error.</summary>
	static IReadOnlyList<string> ResolvableImports(DocumentSession session)
	{
		var imports = session.Document.GetImports();
		var probe = CSharpCompilation.Create("WorkflowImports", Array.Empty<SyntaxTree>(), MetadataReferences(session));
		return imports.Where(name => Resolves(probe.GlobalNamespace, name)).ToList();

		static bool Resolves(INamespaceSymbol root, string name)
		{
			var current = root;
			foreach (var part in name.Split('.')) {
				current = current.GetNamespaceMembers().FirstOrDefault(member => string.Equals(member.Name, part, StringComparison.Ordinal))!;
				if (current == null) return false;
			}
			return true;
		}
	}

	static IEnumerable<MetadataReference> MetadataReferences(DocumentSession session)
	{
		var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
		var paths = (trusted?.Split(Path.PathSeparator) ?? Array.Empty<string>())
			.Concat(new[] { session.Snapshot.ProjectAssemblyPath })
			.Concat(session.Snapshot.ReferencedAssemblyPaths)
			.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths) {
			MetadataReference? reference = null;
			try { reference = MetadataReference.CreateFromFile(path); }
			catch (BadImageFormatException) { }
			if (reference != null) yield return reference;
		}
	}

	// Keep the service implementation synchronous for direct host tests, but never execute it on
	// StreamJsonRpc's dispatch path. A breakpoint deliberately waits for a later Continue/Step
	// request on the very same connection; a synchronous RPC handler would starve that request and
	// turn every real designer breakpoint into a 30-second timeout.
	[JsonRpcMethod("workflow/run")]
	public Task<WorkflowRunResult> RunRpc(string sessionId, string documentId, long baseVersion, Dictionary<string, string>? inputs = null)
		=> Task.Run(() => Run(sessionId, documentId, baseVersion, inputs));

	public WorkflowRunResult Run(string sessionId, string documentId, long baseVersion, Dictionary<string, string>? inputs = null)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		if (session.Document.Root == null) return new WorkflowRunResult { Message = "No workflow is loaded." };
		var expressionLanguages = UnavailableExpressionLanguages(session.Document.Root);
		if (expressionLanguages.Count != 0)
			return new WorkflowRunResult { Message = "This workflow contains " + string.Join(" and ", expressionLanguages) + " expressions. Editing and validation are supported, but expression execution is unavailable in the current CoreWF runtime." };
		var waitingStateMachines = StateMachinesWaitingForExternalEvent(session.Document.Root);
		if (waitingStateMachines.Count != 0)
			return new WorkflowRunResult { Message = "StateMachine " + string.Join(", ", waitingStateMachines) + " starts with transitions that have no Trigger. This designer run has no external state-machine event source; add a Trigger such as Delay before running." };
		if (!session.Document.TryCreateRunInputs(inputs, out var runInputs, out var inputError)) return new WorkflowRunResult { Message = inputError };
		try {
			var runtimeActivity = session.Document.CreateRuntimeActivity();
			if (runtimeActivity == null) return new WorkflowRunResult { Message = "No workflow implementation is loaded." };
			var application = new WorkflowApplication(runtimeActivity, runInputs);
			WorkflowDebugRun debug;
			lock (session.RunGate) {
				if (session.RunningApplication != null) return new WorkflowRunResult { Message = "A workflow run is already active." };
				debug = new WorkflowDebugRun(BuildRuntimeIds(runtimeActivity), session.Breakpoints,
					runInputs.ToDictionary(input => input.Key, input => input.Value?.ToString() ?? ""));
				session.WasCancelled = false;
				session.RunningApplication = application;
				session.DebugRun = debug;
			}
			var trace = new WorkflowTraceParticipant(debug);
			application.Extensions.Add(trace);
			using var completed = new ManualResetEventSlim();
			IDictionary<string, object> outputs = new Dictionary<string, object>();
			Exception? failure = null;
			application.Completed += args => { outputs = args.Outputs; completed.Set(); };
			application.Aborted += args => { failure = args.Reason; completed.Set(); };
			application.OnUnhandledException = args => { failure = args.UnhandledException; completed.Set(); return UnhandledExceptionAction.Abort; };
			application.Idle += args => debug.SetBookmarks(args.Bookmarks.Select(bookmark => new WorkflowBookmarkInfo { Name = bookmark.BookmarkName, OwnerDisplayName = bookmark.OwnerDisplayName }));
			application.Run();
			// A workflow that has reached Idle with external bookmarks is not hung: it is waiting
			// for its host. Do not impose the old fixed 30-second wall here, or a perfectly valid
			// event-driven activity becomes impossible to debug. Stop/Cancel or Resume Bookmark
			// remain explicit user-controlled ways to finish that wait.
			completed.Wait();
			if (failure != null) return new WorkflowRunResult { Message = session.WasCancelled ? "Workflow cancelled." : failure.GetBaseException().Message, Trace = trace.Records };
			return new WorkflowRunResult {
				Succeeded = true,
				Message = outputs.Count == 0 ? "Workflow completed." : "Workflow completed with outputs.",
				Outputs = outputs.ToDictionary(output => output.Key, output => output.Value?.ToString() ?? ""),
				Trace = trace.Records
			};
		} catch (Exception exception) {
			return new WorkflowRunResult { Message = session.WasCancelled ? "Workflow cancelled." : exception.GetBaseException().Message };
		} finally {
			lock (session.RunGate) {
				session.RunningApplication = null;
				session.DebugRun = null;
			}
		}
	}

	static List<string> StateMachinesWaitingForExternalEvent(Activity root)
	{
		var result = new List<string>();
		void Visit(Activity activity)
		{
			if (activity is StateMachine stateMachine && stateMachine.InitialState is { } initial && initial.Transitions.Count != 0 && initial.Transitions.All(transition => transition.Trigger == null)) result.Add(string.IsNullOrWhiteSpace(stateMachine.DisplayName) ? "(unnamed)" : stateMachine.DisplayName);
			foreach (var child in InspectChildren(activity)) Visit(child);
		}
		Visit(root);
		return result;
	}


	static List<string> UnavailableExpressionLanguages(Activity root)
	{
		var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var pending = new Stack<Activity>();
		pending.Push(root);
		while (pending.Count > 0) {
			var activity = pending.Pop();
			// CoreWF's current ITextExpression contract does not expose the legacy
			// RequiresCompilation flag. Every text expression in a dynamically-loaded
			// workflow still needs generated-expression compilation before execution,
			// so detect the interface itself rather than binding to the removed member.
			if (activity is ITextExpression expression)
				languages.Add(string.Equals(expression.Language, "VB", StringComparison.OrdinalIgnoreCase) || expression.Language.Contains("VisualBasic", StringComparison.OrdinalIgnoreCase) ? "VB" : expression.Language);
			foreach (var child in InspectChildren(activity)) pending.Push(child);
		}
		return languages.OrderBy(language => language, StringComparer.Ordinal).ToList();
	}

	static Dictionary<string, string> BuildRuntimeIds(Activity runtimeRoot)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		// The executable root is a DynamicActivity wrapper so declared workflow arguments
		// are available. Its implementation remains the visual root and must therefore
		// retain path "" rather than becoming a synthetic "0" node for breakpoints.
		var visualRoot = runtimeRoot is DynamicActivity ? InspectChildren(runtimeRoot).FirstOrDefault() : runtimeRoot;
		if (visualRoot != null) AddRuntimeIds(visualRoot, "", result);
		return result;
	}

	static void AddRuntimeIds(Activity activity, string path, Dictionary<string, string> result)
	{
		result[activity.Id] = path;
		var children = InspectChildren(activity);
		if (GenericActivityShapes.SwitchShape.For(activity) is { } switchForChildren)
			children = children.Where(child => !switchForChildren.Branches.Any(branch => ReferenceEquals(branch, child)) && !ReferenceEquals(switchForChildren.Default, child)).ToArray();
		if (activity is TryCatch tryCatchForChildren)
			children = children.Where(child => !tryCatchForChildren.Catches.Any(@catch => ReferenceEquals((@catch.GetType().GetProperty("Action")?.GetValue(@catch) as ActivityDelegate)?.Handler, child))).ToArray();
		if (activity is Pick pickForChildren)
			children = children.Where(child => !pickForChildren.Branches.Any(branch => ReferenceEquals(branch.Trigger, child) || ReferenceEquals(branch.Action, child))).ToArray();
		if (GenericActivityShapes.ForEachShape.For(activity) is { } loopForChildren)
			children = children.Where(child => !ReferenceEquals(loopForChildren.Handler, child) && !ReferenceEquals(loopForChildren.ValuesExpression, child)).ToArray();
		// StateMachine inspection exposes implementation activities from State/Transition as if
		// they were direct children. They belong exclusively to the virtual State nodes below;
		// rendering them here duplicates the tree and gives them unstable ordinary paths.
		if (activity is StateMachine) children = Array.Empty<Activity>();
		for (var index = 0; index < children.Length; index++)
			AddRuntimeIds(children[index], path.Length == 0 ? index.ToString() : path + "." + index, result);
		if (activity is StateMachine stateMachineForRuntimeIds) {
			for (var index = 0; index < stateMachineForRuntimeIds.States.Count; index++) {
				var state = stateMachineForRuntimeIds.States[index];
				var statePath = (path.Length == 0 ? "" : path + ".") + "$state-" + index;
				if (state.Entry != null) AddRuntimeIds(state.Entry, statePath + ".0", result);
				if (state.Exit != null) AddRuntimeIds(state.Exit, statePath + ".1", result);
				for (var transitionIndex = 0; transitionIndex < state.Transitions.Count; transitionIndex++) {
					var transition = state.Transitions[transitionIndex];
					var transitionPath = statePath + ".$transition-" + transitionIndex;
					if (transition.Trigger != null) AddRuntimeIds(transition.Trigger, transitionPath + ".0", result);
					if (transition.Action != null) AddRuntimeIds(transition.Action, transitionPath + ".1", result);
				}
			}
		}
		if (GenericActivityShapes.SwitchShape.For(activity) is { } switchForRuntimeIds) {
			// Case element ids are ordinal, so this must walk the cases in the same enumeration
			// order the tree projection uses.
			var caseKeys = switchForRuntimeIds.Keys;
			for (var index = 0; index < caseKeys.Count; index++)
				if (switchForRuntimeIds.ActivityAt(index) is { } branch) AddRuntimeIds(branch, (path.Length == 0 ? "" : path + ".") + "$case-" + index + ".0", result);
			if (switchForRuntimeIds.Default != null) AddRuntimeIds(switchForRuntimeIds.Default, (path.Length == 0 ? "" : path + ".") + "$default.0", result);
		}
		if (activity is TryCatch tryCatchForRuntimeIds)
			for (var index = 0; index < tryCatchForRuntimeIds.Catches.Count; index++)
				if (tryCatchForRuntimeIds.Catches[index].GetType().GetProperty("Action")?.GetValue(tryCatchForRuntimeIds.Catches[index]) is ActivityDelegate action && action.Handler != null)
					AddRuntimeIds(action.Handler, (path.Length == 0 ? "" : path + ".") + "$catch-" + index + ".0", result);
		if (activity is Pick pickForRuntimeIds)
			for (var index = 0; index < pickForRuntimeIds.Branches.Count; index++) {
				var branchPath = (path.Length == 0 ? "" : path + ".") + "$pick-" + index;
				if (pickForRuntimeIds.Branches[index].Trigger != null) AddRuntimeIds(pickForRuntimeIds.Branches[index].Trigger!, branchPath + ".0", result);
				if (pickForRuntimeIds.Branches[index].Action != null) AddRuntimeIds(pickForRuntimeIds.Branches[index].Action!, branchPath + ".1", result);
			}
		if (GenericActivityShapes.ForEachShape.For(activity) is { Handler: { } loopHandler } loopForRuntimeIds)
			AddRuntimeIds(loopHandler, (path.Length == 0 ? "" : path + ".") + (loopForRuntimeIds.IsParallel ? "$parallelforeach-body.0" : "$foreach-body.0"), result);
	}

	[JsonRpcMethod("workflow/cancel-run")]
	public bool CancelRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) {
			if (session.RunningApplication == null) return false;
			session.WasCancelled = true;
			session.DebugRun?.Cancel();
			session.RunningApplication.Abort("Cancelled from Workflow Designer.");
			return true;
		}
	}

	[JsonRpcMethod("workflow/toggle-breakpoint")]
	public WorkflowDebugState ToggleBreakpoint(string sessionId, string documentId, string elementId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		if (session.Document.Find(elementId) == null) throw new InvalidOperationException("Breakpoints can only be set on activity nodes.");
		lock (session.RunGate) {
			if (!session.Breakpoints.Add(elementId)) session.Breakpoints.Remove(elementId);
			return DebugState(session);
		}
	}

	[JsonRpcMethod("workflow/get-debug-state")]
	public WorkflowDebugState GetDebugState(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) return DebugState(session);
	}

	[JsonRpcMethod("workflow/continue")]
	public bool ContinueRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.None) == true;
	}

	[JsonRpcMethod("workflow/resume-bookmark")]
	public bool ResumeBookmark(string sessionId, string documentId, string bookmarkName, string? value = null)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) {
			if (session.RunningApplication == null || string.IsNullOrWhiteSpace(bookmarkName)) return false;
			session.DebugRun?.ClearBookmarks();
			return session.RunningApplication.ResumeBookmark(bookmarkName, value) == BookmarkResumptionResult.Success;
		}
	}

	[JsonRpcMethod("workflow/step")]
	public bool StepRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Next) == true;
	}

	[JsonRpcMethod("workflow/step-over")]
	public bool StepOverRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Over) == true;
	}

	[JsonRpcMethod("workflow/step-out")]
	public bool StepOutRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Out) == true;
	}

	[JsonRpcMethod("workflow/connect-flow")]
	public DesignerSessionState ConnectFlow(string sessionId, string documentId, long baseVersion, string sourceId, string targetId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.ConnectFlowSteps(sourceId, targetId)) throw new InvalidOperationException("The selected activities are not connectable Flowchart steps, or the connection would create a cycle.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/connect-flow-decision")]
	public DesignerSessionState ConnectFlowDecision(string sessionId, string documentId, long baseVersion, string sourceId, string targetId, bool trueBranch)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.ConnectFlowDecision(sourceId, targetId, trueBranch)) throw new InvalidOperationException("Select a Flow Decision and a Flowchart step in the same Flowchart.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/set-flow-start")]
	public DesignerSessionState SetFlowStart(string sessionId, string documentId, long baseVersion, string elementId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.SetFlowStart(elementId)) throw new InvalidOperationException("Select a Flowchart step or Flow Decision to set the start node.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/disconnect-flow")]
	public DesignerSessionState DisconnectFlow(string sessionId, string documentId, long baseVersion, string sourceId, string branch)
	{
		documents.ValidateSession(sessionId);
		var session = Get(sessionId, documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.DisconnectFlow(sourceId, branch)) throw new InvalidOperationException("The selected Flowchart edge cannot be disconnected.");
		RecordMutation(session, before);
		return State(session);
	}

	static WorkflowDebugState DebugState(DocumentSession session)
	{
		var debugRun = session.DebugRun;
		var debug = debugRun?.Snapshot();
		var currentElementId = debug?.CurrentElementId ?? "";
		return new WorkflowDebugState {
			IsRunning = session.RunningApplication != null,
			IsPaused = debug?.IsPaused == true,
			IsIdle = debug?.IsIdle == true,
			CurrentElementId = currentElementId,
			BreakpointElementIds = session.Breakpoints.OrderBy(id => id, StringComparer.Ordinal).ToList(),
			Inputs = debugRun?.Inputs ?? new Dictionary<string, string>(),
			Bookmarks = debugRun?.Bookmarks ?? new List<WorkflowBookmarkInfo>(),
			Trace = debug?.Trace ?? new List<string>(),
			CallStack = debug?.IsPaused == true ? DebugStack(session.Document, currentElementId) : new List<WorkflowDebugFrame>()
		};
	}

	static List<WorkflowDebugFrame> DebugStack(WorkflowDocument document, string elementId)
	{
		var result = new List<WorkflowDebugFrame>();
		var ids = new List<string> { "" };
		if (!string.IsNullOrEmpty(elementId)) {
			var current = "";
			foreach (var part in elementId.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				current = current.Length == 0 ? part : current + "." + part;
				ids.Add(current);
			}
		}
		foreach (var id in ids) {
			var activity = document.Find(id);
			var marker = id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(part => part.StartsWith('$'));
			var typeName = activity?.GetType().Name ?? (marker?.StartsWith("$case-", StringComparison.Ordinal) == true ? "Switch case" : marker?.StartsWith("$state-", StringComparison.Ordinal) == true ? "State" : marker?.StartsWith("$transition-", StringComparison.Ordinal) == true ? "Transition" : "Workflow scope");
			var displayName = activity?.DisplayName;
			result.Add(new WorkflowDebugFrame { ElementId = id, TypeName = typeName, DisplayName = string.IsNullOrWhiteSpace(displayName) ? (id.Length == 0 ? "Workflow" : typeName) : displayName });
		}
		return result;
	}

	sealed class WorkflowTraceParticipant : TrackingParticipant
	{
		readonly WorkflowDebugRun debug;
		public List<string> Records { get; } = new();
		public WorkflowTraceParticipant(WorkflowDebugRun debug)
		{
			this.debug = debug;
			TrackingProfile = new TrackingProfile();
			var activityStates = new ActivityStateQuery();
			activityStates.States.Add("*");
			TrackingProfile.Queries.Add(activityStates);
		}

		protected override void Track(TrackingRecord record, TimeSpan timeout)
		{
			if (record is ActivityStateRecord state) {
				var entry = state.Activity.Name + ": " + state.State;
				Records.Add(entry);
				debug.Record(entry);
				if (string.Equals(state.State, "Executing", StringComparison.Ordinal)) debug.Observe(state.Activity.Id);
			}
		}
	}

	/// <summary>Blocks the workflow tracking callback at configured activities. CoreWF invokes a
	/// tracking participant on the execution path, which makes this a real runtime pause rather
	/// than a post-run trace viewer. The RPC service releases it through Continue/Step/Cancel.</summary>
	sealed class WorkflowDebugRun
	{
		readonly Dictionary<string, string> activityIds;
		readonly HashSet<string> breakpoints;
		readonly Dictionary<string, string> inputs;
		readonly List<WorkflowBookmarkInfo> bookmarks = new();
		readonly ManualResetEventSlim resume = new(false);
		readonly object gate = new();
		StepMode stepMode;
		string stepOrigin = "";
		bool cancelled;
		readonly List<string> trace = new();
		public bool IsPaused { get; private set; }
		public bool IsIdle { get; private set; }
		public string CurrentElementId { get; private set; } = "";
		public Dictionary<string, string> Inputs { get { lock (gate) return new Dictionary<string, string>(inputs); } }
		public List<WorkflowBookmarkInfo> Bookmarks { get { lock (gate) return bookmarks.Select(bookmark => new WorkflowBookmarkInfo { Name = bookmark.Name, OwnerDisplayName = bookmark.OwnerDisplayName }).ToList(); } }
		public (bool IsPaused, bool IsIdle, string CurrentElementId, List<string> Trace) Snapshot()
		{
			lock (gate) return (IsPaused, IsIdle, CurrentElementId, trace.ToList());
		}
		public void Record(string entry)
		{
			lock (gate) trace.Add(entry);
		}
		public void SetBookmarks(IEnumerable<WorkflowBookmarkInfo> values)
		{
			lock (gate) { bookmarks.Clear(); bookmarks.AddRange(values); IsIdle = bookmarks.Count != 0; }
		}
		public void ClearBookmarks()
		{
			lock (gate) { bookmarks.Clear(); IsIdle = false; }
		}
		public enum StepMode { None, Next, Over, Out }

		public WorkflowDebugRun(Dictionary<string, string> activityIds, IEnumerable<string> breakpoints, Dictionary<string, string> inputs)
		{
			this.activityIds = activityIds;
			this.breakpoints = new HashSet<string>(breakpoints, StringComparer.Ordinal);
			this.inputs = inputs;
		}

		public void Observe(string runtimeActivityId)
		{
			lock (gate) {
				if (!activityIds.TryGetValue(runtimeActivityId, out var elementId)) return;
				var pauseForStep = stepMode switch {
					StepMode.Next => true,
					StepMode.Over => Parent(elementId) == Parent(stepOrigin) && elementId != stepOrigin,
					StepMode.Out => OutsideParentScope(elementId, stepOrigin),
					_ => false
				};
				if (!breakpoints.Contains(elementId) && !pauseForStep) return;
				CurrentElementId = elementId;
				stepMode = StepMode.None;
				IsPaused = true;
				resume.Reset();
			}
			resume.Wait();
		}

		static string Parent(string id) => id.LastIndexOf('.') is var index and >= 0 ? id[..index] : "";
		static bool OutsideParentScope(string candidate, string origin)
		{
			var scope = Parent(origin);
			return scope.Length > 0 && candidate != scope && !candidate.StartsWith(scope + ".", StringComparison.Ordinal);
		}

		public bool Resume(StepMode mode)
		{
			lock (gate) {
				if (!IsPaused || cancelled) return false;
				stepOrigin = CurrentElementId;
				stepMode = mode;
				IsPaused = false;
				resume.Set();
				return true;
			}
		}

		public void Cancel()
		{
			lock (gate) {
				cancelled = true;
				IsPaused = false;
				resume.Set();
			}
		}
	}

	DesignerSessionState State(DocumentSession session)
	{
		var root = session.Document.Root;
		return new DesignerSessionState {
			SessionId = sessionId, DocumentId = session.DocumentId, Version = session.Version,
			Accepted = session.Document.LastParseSucceeded, Error = session.Document.Error, CanUndo = session.Undo.Count > 0, CanRedo = session.Redo.Count > 0,
			RootType = root?.GetType().Name ?? "", ComponentCount = root == null ? 0 : Count(root),
			Tree = root == null ? null : Node(session.Document, root, "")
		};
	}

	DesignerElementNode Node(WorkflowDocument document, Activity activity, string path)
	{
		var children = InspectChildren(activity);
		if (activity is Pick pickForChildren)
			children = children.Where(child => !pickForChildren.Branches.Any(branch => ReferenceEquals(branch.Trigger, child) || ReferenceEquals(branch.Action, child))).ToArray();
		if (GenericActivityShapes.ForEachShape.For(activity) is { } loopForChildren)
			children = children.Where(child => !ReferenceEquals(loopForChildren.Handler, child) && !ReferenceEquals(loopForChildren.ValuesExpression, child)).ToArray();
		if (activity is StateMachine) children = Array.Empty<Activity>();
		var properties = document.GetProperties(activity)
			.Where(p => IsBrowsableProperty(activity, p.Name))
			.Select(p => ToPropertyInfo(activity, p.Name, p.Value, p.TypeName, p.IsReadOnly))
			.Prepend(new DesignerPropertyInfo { Name = "$displayName", DisplayName = "DisplayName", Value = activity.DisplayName, Category = "Identity" })
			.ToList();
		var childNodes = children.Select((child, index) => Node(document, child, path.Length == 0 ? index.ToString() : path + "." + index)).ToList();
		if (activity is StateMachine stateMachine) {
			for (var index = 0; index < stateMachine.States.Count; index++) {
				var state = stateMachine.States[index];
				var id = (path.Length == 0 ? "" : path + ".") + "$state-" + index;
				childNodes.Add(new DesignerElementNode {
					Id = id, Path = path, Name = state.DisplayName, Type = "State",
					Properties = new List<DesignerPropertyInfo> {
						new() { Name = "$displayName", DisplayName = "DisplayName", Value = state.DisplayName, TypeName = "String", IsReadOnly = false, Category = "State" },
						new() { Name = "IsFinal", DisplayName = "Is final", Value = state.IsFinal.ToString(), TypeName = "Boolean", IsReadOnly = false, Category = "State" },
						new() { Name = "$initial", DisplayName = "Initial", Value = ReferenceEquals(stateMachine.InitialState, state).ToString(), TypeName = "Boolean", IsReadOnly = false, Category = "State" },
						new() { Name = "$entry", DisplayName = "Entry", Value = state.Entry?.DisplayName ?? "(empty)", TypeName = "Activity", IsReadOnly = true, Category = "State" },
						new() { Name = "$exit", DisplayName = "Exit", Value = state.Exit?.DisplayName ?? "(empty)", TypeName = "Activity", IsReadOnly = true, Category = "State" }
					},
					Children = new[] { state.Entry == null ? null : Node(document, state.Entry, id + ".0"), state.Exit == null ? null : Node(document, state.Exit, id + ".1") }.Where(node => node != null).Cast<DesignerElementNode>().ToList()
				});
				var stateNode = childNodes[^1];
				for (var transitionIndex = 0; transitionIndex < state.Transitions.Count; transitionIndex++) {
					var transition = state.Transitions[transitionIndex];
					var transitionId = id + ".$transition-" + transitionIndex;
					var targetIndex = transition.To == null ? -1 : stateMachine.States.IndexOf(transition.To);
					stateNode.Children.Add(new DesignerElementNode {
						Id = transitionId, Path = id, Name = transition.DisplayName, Type = "Transition",
						Properties = new List<DesignerPropertyInfo> {
							new() { Name = "$displayName", DisplayName = "DisplayName", Value = transition.DisplayName, TypeName = "String", IsReadOnly = false, Category = "Transition" },
							new() { Name = "$target", DisplayName = "Target state", Value = targetIndex < 0 ? "" : (path.Length == 0 ? "" : path + ".") + "$state-" + targetIndex, TypeName = "State", IsReadOnly = false, Category = "Transition" },
							new() { Name = nameof(Transition.Condition), DisplayName = "Condition", Value = document.GetBooleanExpressionValue(transition.Condition).Value, TypeName = "Boolean", IsReadOnly = !document.GetBooleanExpressionValue(transition.Condition).IsEditable, Category = "Transition" }
						},
						Children = new[] { transition.Trigger == null ? null : Node(document, transition.Trigger, transitionId + ".0"), transition.Action == null ? null : Node(document, transition.Action, transitionId + ".1") }.Where(node => node != null).Cast<DesignerElementNode>().ToList()
					});
				}
			}
		}
		if (activity is Flowchart flowchart) {
			string? FlowNodeId(FlowNode? flowNode)
			{
				if (flowNode is FlowStep step) {
					var index = Array.FindIndex(children, child => ReferenceEquals(child, step.Action));
					return index < 0 ? null : (path.Length == 0 ? index.ToString() : path + "." + index);
				}
				if (flowNode is FlowDecision decision) {
					var index = flowchart.Nodes.IndexOf(decision);
					return index < 0 ? null : (path.Length == 0 ? "" : path + ".") + "$decision-" + index;
				}
				return null;
			}
			foreach (var step in flowchart.Nodes.OfType<FlowStep>()) {
				var id = FlowNodeId(step);
				if (id == null || childNodes.FirstOrDefault(node => node.Id == id) is not { } childNode) continue;
				childNode.Properties.Add(new DesignerPropertyInfo { Name = "$flowNext", DisplayName = "Next", Value = FlowNodeId(step.Next) ?? "", IsReadOnly = true, Category = "Flowchart" });
			}
			// Which node the flowchart actually starts at. The designer already had a "Set Start"
			// command (SetFlowStartAsync), but nothing published the CURRENT start back, so the
			// canvas had no way to draw WF's green Start node or its arrow into the entry step.
			properties.Add(new DesignerPropertyInfo {
				Name = "$flowStartNode", DisplayName = "Start node",
				Value = FlowNodeId(flowchart.StartNode) ?? "", IsReadOnly = true, Category = "Flowchart"
			});
			for (var index = 0; index < flowchart.Nodes.Count; index++) {
				if (flowchart.Nodes[index] is not FlowDecision decision) continue;
				childNodes.Add(new DesignerElementNode {
					Id = (path.Length == 0 ? "" : path + ".") + "$decision-" + index, Path = path,
					Name = "Decision", Type = "FlowDecision",
					Properties = document.GetProperties(decision).Select(property => new DesignerPropertyInfo { Name = property.Name, DisplayName = property.Name, Value = property.Value, TypeName = property.TypeName, IsReadOnly = property.IsReadOnly, Category = "Workflow" })
						.Append(new DesignerPropertyInfo { Name = "$flowTrue", DisplayName = "True", Value = FlowNodeId(decision.True) ?? "", IsReadOnly = true, Category = "Flowchart" })
						.Append(new DesignerPropertyInfo { Name = "$flowFalse", DisplayName = "False", Value = FlowNodeId(decision.False) ?? "", IsReadOnly = true, Category = "Flowchart" }).ToList()
				});
			}
		}
		if (activity is TryCatch tryCatch) {
			for (var index = 0; index < tryCatch.Catches.Count; index++) {
				var @catch = tryCatch.Catches[index];
				var action = @catch.GetType().GetProperty("Action")?.GetValue(@catch) as ActivityDelegate;
				var exceptionName = (action?.GetType().GetProperty("Argument")?.GetValue(action) as DelegateArgument)?.Name ?? "exception";
				childNodes.Add(new DesignerElementNode {
					Id = (path.Length == 0 ? "" : path + ".") + "$catch-" + index,
					Path = path, Name = "Catch (" + @catch.ExceptionType.Name + ")", Type = "Catch",
					Properties = new List<DesignerPropertyInfo> {
						new() { Name = "$exceptionType", DisplayName = "Exception type", Value = @catch.ExceptionType.AssemblyQualifiedName ?? @catch.ExceptionType.FullName ?? @catch.ExceptionType.Name, TypeName = "Type", IsReadOnly = false, Category = "Catch" },
						new() { Name = "$exceptionName", DisplayName = "Exception name", Value = exceptionName, TypeName = "String", IsReadOnly = false, Category = "Catch" }
					},
					Children = action?.Handler == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, action.Handler, (path.Length == 0 ? "" : path + ".") + "$catch-" + index + ".0") }
				});
			}
		}
		if (activity is Pick pick) {
			for (var index = 0; index < pick.Branches.Count; index++) {
				var branch = pick.Branches[index];
				var id = (path.Length == 0 ? "" : path + ".") + "$pick-" + index;
				childNodes.Add(new DesignerElementNode {
					Id = id, Path = path, Name = "Pick Branch " + (index + 1), Type = "PickBranch",
					Properties = new List<DesignerPropertyInfo> {
						new() { Name = "$pickTrigger", DisplayName = "Trigger", Value = branch.Trigger?.DisplayName ?? "(empty)", TypeName = "Activity", IsReadOnly = true, Category = "Pick" },
						new() { Name = "$pickAction", DisplayName = "Action", Value = branch.Action?.DisplayName ?? "(empty)", TypeName = "Activity", IsReadOnly = true, Category = "Pick" }
					},
					Children = new[] { branch.Trigger == null ? null : Node(document, branch.Trigger, id + ".0"), branch.Action == null ? null : Node(document, branch.Action, id + ".1") }.Where(node => node != null).Cast<DesignerElementNode>().ToList()
				});
			}
		}
		if (GenericActivityShapes.ForEachShape.For(activity) is { } loop) {
			var marker = loop.IsParallel ? "$parallelforeach-body" : "$foreach-body";
			var id = (path.Length == 0 ? "" : path + ".") + marker;
			var category = loop.IsParallel ? "Parallel For Each" : "For Each";
			childNodes.Add(new DesignerElementNode {
				Id = id, Path = path, Name = "Body", Type = loop.IsParallel ? "ParallelForEachBody" : "ForEachBody",
				Properties = new List<DesignerPropertyInfo> {
					new() { Name = "$itemName", DisplayName = "Item name", Value = loop.Argument?.Name ?? "item", TypeName = "String", IsReadOnly = false, Category = category },
					new() { Name = "$itemType", DisplayName = "Item type", Value = loop.ItemType.Name, TypeName = "Type", IsReadOnly = true, Category = category }
				},
				Children = loop.Handler == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, loop.Handler, id + ".0") }
			});
		}
		if (GenericActivityShapes.SwitchShape.For(activity) is { } @switch) {
			// Cases are addressed by their ordinal position (a key of an arbitrary T cannot be put
			// into a dotted element id), so the enumeration order here defines those ids and must
			// match the runtime-id walk.
			var caseKeys = @switch.Keys;
			for (var index = 0; index < caseKeys.Count; index++) {
				var key = caseKeys[index];
				var keyText = GenericActivityShapes.FormatValue(key);
				var id = (path.Length == 0 ? "" : path + ".") + "$case-" + index;
				var branch = @switch.ActivityAt(index);
				childNodes.Add(new DesignerElementNode {
					Id = id, Path = path, Name = "Case " + keyText, Type = "SwitchCase",
					Properties = new List<DesignerPropertyInfo> { new() { Name = "$caseValue", DisplayName = "Case value", Value = keyText, TypeName = @switch.KeyType.Name, IsReadOnly = false, Category = "Switch" } },
					Children = branch == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, branch, id + ".0") }
				});
			}
			if (@switch.Default != null) {
				var id = (path.Length == 0 ? "" : path + ".") + "$default";
				childNodes.Add(new DesignerElementNode { Id = id, Path = path, Name = "Default", Type = "SwitchDefault", Properties = new List<DesignerPropertyInfo>(), Children = new List<DesignerElementNode> { Node(document, @switch.Default, id + ".0") } });
			}
		}
		return new DesignerElementNode {
			Id = path, Name = activity.DisplayName, Type = activity.GetType().Name, Path = path,
			Properties = properties,
			Children = childNodes
		};
	}

	static DesignerPropertyInfo ToPropertyInfo(Activity activity, string name, string value, string typeName, bool isReadOnly)
	{
		// Several CoreWF generic activities hide a base Result property. GetProperty(name)
		// throws AmbiguousMatchException for them, so select the same public instance surface
		// the document projection uses instead of asking reflection for a unique match.
		var property = FindDesignProperty(activity, name);
		// The pad edits the *value* of a property, so a property held as InArgument<T> or
		// Activity<T> must be described by its T: otherwise an enum argument looks like a plain
		// string and loses its list of values, which is most of CoreWF's enum surface.
		var type = EffectiveValueType(property?.PropertyType);
		var isEnum = type?.IsEnum == true;
		return new DesignerPropertyInfo {
			Name = name,
			DisplayName = property?.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName ?? name,
			Description = property?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "",
			Value = value, TypeName = typeName,
			IsReadOnly = isReadOnly || property?.GetCustomAttribute<System.ComponentModel.ReadOnlyAttribute>()?.IsReadOnly == true,
			Category = property?.GetCustomAttribute<System.ComponentModel.CategoryAttribute>()?.Category ?? "Workflow",
			Kind = PropertyKind(type),
			AllowedValues = isEnum ? Enum.GetNames(type!).ToList() : new List<string>()
		};
	}

	/// <summary>Unwraps the argument/expression wrappers CoreWF holds designable values in, so the
	/// property pad sees the type the user actually edits.</summary>
	static Type? EffectiveValueType(Type? propertyType)
	{
		for (var type = propertyType; type != null; type = type.BaseType) {
			if (!type.IsGenericType) continue;
			var definition = type.GetGenericTypeDefinition();
			if (definition == typeof(InArgument<>) || definition == typeof(OutArgument<>) || definition == typeof(InOutArgument<>) || definition == typeof(Activity<>))
				return Nullable.GetUnderlyingType(type.GetGenericArguments()[0]) ?? type.GetGenericArguments()[0];
		}
		return propertyType == null ? null : Nullable.GetUnderlyingType(propertyType) ?? propertyType;
	}

	static string PropertyKind(Type? type) =>
		type == null ? "String"
		: type.IsEnum ? "Enum"
		: type == typeof(bool) ? "Boolean"
		: type == typeof(Uri) ? "Uri"
		: IsNumericType(type) ? "Number"
		: "String";

	static bool IsNumericType(Type type) =>
		type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
		|| type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
		|| type == typeof(float) || type == typeof(double) || type == typeof(decimal);

	static bool IsBrowsableProperty(Activity activity, string name) => FindDesignProperty(activity, name)?.GetCustomAttribute<System.ComponentModel.BrowsableAttribute>()?.Browsable != false;

	static PropertyInfo? FindDesignProperty(Activity activity, string name) => activity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));

	static Activity[] InspectChildren(Activity activity)
	{
		try { return WorkflowInspectionServices.GetActivities(activity).ToArray(); }
		catch (InvalidWorkflowException) { return Array.Empty<Activity>(); }
	}

	static int Count(Activity activity) => 1 + InspectChildren(activity).Sum(Count);

	DocumentSession Get(string sessionId, string documentId) => documents.Get(sessionId, documentId);
	static void EnsureVersion(DocumentSession session, long candidate)
	{
		if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}.");
	}

	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown() { documents.CloseAll(_ => { }); shutdown.Set(); return new(); }
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();

	sealed class DocumentSession
	{
		public DocumentSession(string documentId) => DocumentId = documentId;
		public string DocumentId { get; }
		public WorkflowDocument Document { get; } = new();
		public string FileName = "";
		public DesignerDocumentSnapshot Snapshot { get; set; } = new();
		public string[]? CompletionTypeNames;
		public long Version;
		public Stack<string> Undo { get; } = new();
		public Stack<string> Redo { get; } = new();
		public object RunGate { get; } = new();
		public WorkflowApplication? RunningApplication;
		public WorkflowDebugRun? DebugRun;
		public HashSet<string> Breakpoints { get; } = new(StringComparer.Ordinal);
		public bool WasCancelled;
	}
}
