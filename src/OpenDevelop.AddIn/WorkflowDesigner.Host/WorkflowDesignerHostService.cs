using System.Activities;
using System.Activities.Expressions;
using System.Activities.Statements;
using System.Activities.Tracking;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WorkflowDesigner.Host;

sealed class WorkflowDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly DesignerDocumentRegistry<DocumentSession> documents = new();
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
		var snapshot = Get(documentId).Snapshot;
		return new DesignerCapabilities {
			Runtime = "CoreWF ActivityBuilder", Version = typeof(Activity).Assembly.GetName().Version?.ToString() ?? "", SessionId = sessionId,
			Toolbox = StandardActivities().Append(new DesignerToolboxItemInfo { Name = "State", DisplayName = "State", TypeName = "State", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "Transition", DisplayName = "Transition", TypeName = "Transition", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "FlowDecision", DisplayName = "Flow Decision", TypeName = "FlowDecision", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "Catch", DisplayName = "Catch (Exception)", TypeName = "Catch", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "PickBranch", DisplayName = "Pick Branch", TypeName = "PickBranch", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "ForEachInt32", DisplayName = "For Each (Int32)", TypeName = "ForEachInt32", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "ParallelForEachInt32", DisplayName = "Parallel For Each (Int32)", TypeName = "ParallelForEachInt32", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "SwitchInt32", DisplayName = "Switch (Int32)", TypeName = "SwitchInt32", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "SwitchCase", DisplayName = "Switch Case", TypeName = "SwitchCase", Category = "CoreWF" }).Append(new DesignerToolboxItemInfo { Name = "SwitchDefault", DisplayName = "Switch Default", TypeName = "SwitchDefault", Category = "CoreWF" }).Concat(string.Equals(snapshot.ProjectCodeMode, "Enabled", StringComparison.OrdinalIgnoreCase) ? ProjectActivities(snapshot) : Enumerable.Empty<DesignerToolboxItemInfo>()).OrderBy(item => item.Category, StringComparer.Ordinal).ThenBy(item => item.DisplayName, StringComparer.Ordinal)
				.ToList()
		};
	}

	static IEnumerable<DesignerToolboxItemInfo> StandardActivities() => ActivityTypes(typeof(Sequence).Assembly)
		.Where(type => type.Namespace == typeof(Sequence).Namespace)
		.Select(type => ToolboxItem(type, "CoreWF"));

	static IEnumerable<DesignerToolboxItemInfo> ProjectActivities(DesignerDocumentSnapshot snapshot)
	{
		var paths = new[] { snapshot.ProjectAssemblyPath }.Concat(snapshot.ReferencedAssemblyPaths)
			.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
		foreach (var path in paths) {
			Assembly assembly;
			try { assembly = Assembly.LoadFrom(path); } catch { continue; }
			var category = string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase) ? "Project" : "Referenced activity";
			foreach (var type in ActivityTypes(assembly)) yield return ToolboxItem(type, category);
		}
	}

	static IEnumerable<Type> ActivityTypes(Assembly assembly)
	{
		IEnumerable<Type> types;
		try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException exception) { types = exception.Types.Where(type => type != null)!; }
		return types.Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(Activity).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null);
	}

	static DesignerToolboxItemInfo ToolboxItem(Type type, string category) => new() { Name = type.Name, DisplayName = type.Name, TypeName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name, Category = category };

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
		var session = Get(documentId);
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
		var session = Get(documentId);
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
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		foreach (var id in elementIds)
			if (!session.Document.Remove(id))
				throw new InvalidOperationException("Workflow element deletion was rejected: " + id);
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(documentId), baseVersion, undo: true); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(documentId), baseVersion, undo: false); }

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
		var session = Get(documentId);
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
		documents.Remove(documentId, _ => { });
		return new();
	}

	[JsonRpcMethod("workflow/get-arguments")]
	public List<WorkflowArgumentInfo> GetArguments(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(documentId).Document.GetArguments().Select(argument => new WorkflowArgumentInfo {
			Name = argument.Name, TypeName = argument.TypeName, Direction = argument.Direction, DefaultValue = argument.DefaultValue
		}).ToList();
	}

	[JsonRpcMethod("workflow/add-argument")]
	public DesignerSessionState AddArgument(string sessionId, string documentId, long baseVersion, string name, string typeName, string defaultValue, string direction = "In")
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
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
		var session = Get(documentId);
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
		var session = Get(documentId);
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
		return Get(documentId).Document.GetVariables().Select(variable => new WorkflowVariableInfo { Name = variable.Name, TypeName = variable.TypeName, Scope = variable.Scope, ScopeId = variable.ScopeId, DefaultValue = variable.DefaultValue }).ToList();
	}

	[JsonRpcMethod("workflow/get-variable-scopes")]
	public List<WorkflowVariableInfo> GetVariableScopes(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		return Get(documentId).Document.GetVariableScopes().Select(scope => new WorkflowVariableInfo { Scope = scope.Scope, ScopeId = scope.ScopeId }).ToList();
	}

	[JsonRpcMethod("workflow/add-variable")]
	public DesignerSessionState AddVariable(string sessionId, string documentId, long baseVersion, string name, string typeName, string defaultValue, string scopeId = "")
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
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
		var session = Get(documentId);
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
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.RemoveVariable(name, scopeId)) throw new InvalidOperationException("Workflow variable removal was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/validate-expression")]
	public WorkflowExpressionValidation ValidateExpression(string sessionId, string documentId, string expression, string? elementId = null)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		var isVisualBasic = expression.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
		var text = expression.StartsWith("=", StringComparison.Ordinal) ? expression[(isVisualBasic ? 4 : 1)..] : expression;
		if (string.IsNullOrWhiteSpace(text)) return new WorkflowExpressionValidation { Diagnostics = { "Expression cannot be empty." } };
		var variables = session.Document.GetExpressionSymbolsVisibleAt(elementId).Where(variable => System.Text.RegularExpressions.Regex.IsMatch(variable.Name, "^[A-Za-z_][A-Za-z0-9_]*$")).GroupBy(variable => variable.Name, StringComparer.Ordinal).Select(group => group.First()).ToArray();
		var source = isVisualBasic ? "Option Strict Off\nModule WorkflowExpression\nFunction Value() As Object\n" + string.Join("\n", variables.Select(variable => "Dim " + variable.Name + " As " + ExpressionVariableTypeName(session, variable.TypeName) + " = Nothing")) + "\nReturn (" + text + ")\nEnd Function\nEnd Module" : "class WorkflowExpression { object Value() { " + string.Join(" ", variables.Select(variable => ExpressionVariableTypeName(session, variable.TypeName) + " " + variable.Name + " = default;")) + " return (" + text + "); } }";
		Compilation compilation = isVisualBasic ? VisualBasicCompilation.Create("WorkflowExpression", new[] { VisualBasicSyntaxTree.ParseText(source) }, MetadataReferences(session), new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary)) : CSharpCompilation.Create("WorkflowExpression", new[] { CSharpSyntaxTree.ParseText(source) }, MetadataReferences(session), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		var diagnostics = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.GetMessage()).Distinct(StringComparer.Ordinal).ToList();
		return new WorkflowExpressionValidation { IsValid = diagnostics.Count == 0, Diagnostics = diagnostics };
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
		var session = Get(documentId);
		var visualBasic = expression.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
		var terms = visualBasic ? new[] { "True", "False", "Nothing", "If", "AndAlso", "OrElse", "Not", "String", "Math" } : new[] { "true", "false", "null", "this", "new", "nameof", "string", "Math" };
		var owner = System.Text.RegularExpressions.Regex.Match(expression[..Math.Max(0, expression.Length - prefix.Length)], @"([A-Za-z_][A-Za-z0-9_.]*)\.$").Groups[1].Value;
		if (ResolveCompletionType(owner, visualBasic) is { } staticType) return new WorkflowExpressionCompletion { Items = staticType.GetMembers(BindingFlags.Public | BindingFlags.Static).Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Field).Select(member => member.Name).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(64).ToList() };
		var variables = session.Document.GetExpressionSymbolsVisibleAt(elementId).ToArray();
		var typeName = variables.FirstOrDefault(variable => string.Equals(variable.Name, owner, StringComparison.OrdinalIgnoreCase)).TypeName;
		var instanceType = session.Document.GetVariableRuntimeTypeVisibleAt(owner, elementId) ?? ResolveVariableCompletionType(typeName);
		if (instanceType != null) return new WorkflowExpressionCompletion { Items = instanceType.GetMembers(BindingFlags.Public | BindingFlags.Instance).Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property).Select(member => member.Name).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(64).ToList() };
		var members = CompletionInstanceMembers(session, typeName, prefix); if (members.Count != 0) return new WorkflowExpressionCompletion { Items = members };
		members = CompletionStaticMembers(session, owner, prefix); if (members.Count != 0) return new WorkflowExpressionCompletion { Items = members };
		return new WorkflowExpressionCompletion { Items = terms.Concat(variables.Select(variable => variable.Name)).Concat(StandardActivities().Select(item => item.DisplayName)).Concat(CompletionTypeNames(session)).Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).Take(64).ToList() };
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

	[JsonRpcMethod("workflow/run")]
	public WorkflowRunResult Run(string sessionId, string documentId, long baseVersion)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		if (session.Document.Root == null) return new WorkflowRunResult { Message = "No workflow is loaded." };
		var expressionLanguages = UnavailableExpressionLanguages(session.Document.Root);
		if (expressionLanguages.Count != 0)
			return new WorkflowRunResult { Message = "This workflow contains " + string.Join(" and ", expressionLanguages) + " expressions. Editing and validation are supported, but expression execution is unavailable in the current CoreWF runtime." };
		var waitingStateMachines = StateMachinesWaitingForExternalEvent(session.Document.Root);
		if (waitingStateMachines.Count != 0)
			return new WorkflowRunResult { Message = "StateMachine " + string.Join(", ", waitingStateMachines) + " starts with transitions that have no Trigger. This designer run has no external state-machine event source; add a Trigger such as Delay before running." };
		try {
			var application = new WorkflowApplication(session.Document.Root);
			WorkflowDebugRun debug;
			lock (session.RunGate) {
				if (session.RunningApplication != null) return new WorkflowRunResult { Message = "A workflow run is already active." };
				debug = new WorkflowDebugRun(BuildRuntimeIds(session), session.Breakpoints);
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
			application.Run();
			if (!completed.Wait(TimeSpan.FromSeconds(30))) { application.Abort("Workflow Designer run timed out."); return new WorkflowRunResult { Message = "Workflow timed out after 30 seconds.", Trace = trace.Records }; }
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
			if (activity is ITextExpression { RequiresCompilation: true } expression)
				languages.Add(string.Equals(expression.Language, "VB", StringComparison.OrdinalIgnoreCase) || expression.Language.Contains("VisualBasic", StringComparison.OrdinalIgnoreCase) ? "VB" : expression.Language);
			foreach (var child in InspectChildren(activity)) pending.Push(child);
		}
		return languages.OrderBy(language => language, StringComparer.Ordinal).ToList();
	}

	static Dictionary<string, string> BuildRuntimeIds(DocumentSession session)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (session.Document.Root != null) AddRuntimeIds(session.Document.Root, "", result);
		return result;
	}

	static void AddRuntimeIds(Activity activity, string path, Dictionary<string, string> result)
	{
		result[activity.Id] = path;
		var children = InspectChildren(activity);
		if (activity is Switch<int> @switchForChildren)
			children = children.Where(child => !@switchForChildren.Cases.Values.Any(branch => ReferenceEquals(branch, child)) && !ReferenceEquals(@switchForChildren.Default, child)).ToArray();
		if (activity is TryCatch tryCatchForChildren)
			children = children.Where(child => !tryCatchForChildren.Catches.Any(@catch => ReferenceEquals((@catch.GetType().GetProperty("Action")?.GetValue(@catch) as ActivityDelegate)?.Handler, child))).ToArray();
		if (activity is Pick pickForChildren)
			children = children.Where(child => !pickForChildren.Branches.Any(branch => ReferenceEquals(branch.Trigger, child) || ReferenceEquals(branch.Action, child))).ToArray();
		if (activity is ForEach<int> forEachForChildren)
			children = children.Where(child => !ReferenceEquals(forEachForChildren.Body?.Handler, child) && !ReferenceEquals(forEachForChildren.Values?.Expression, child)).ToArray();
		if (activity is ParallelForEach<int> parallelForEachForChildren)
			children = children.Where(child => !ReferenceEquals(parallelForEachForChildren.Body?.Handler, child) && !ReferenceEquals(parallelForEachForChildren.Values?.Expression, child)).ToArray();
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
		if (activity is Switch<int> switchForRuntimeIds) {
			foreach (var entry in switchForRuntimeIds.Cases)
				if (entry.Value != null) AddRuntimeIds(entry.Value, (path.Length == 0 ? "" : path + ".") + "$case-" + entry.Key + ".0", result);
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
		if (activity is ForEach<int> forEachForRuntimeIds && forEachForRuntimeIds.Body?.Handler != null)
			AddRuntimeIds(forEachForRuntimeIds.Body.Handler, (path.Length == 0 ? "" : path + ".") + "$foreach-body.0", result);
		if (activity is ParallelForEach<int> parallelForEachForRuntimeIds && parallelForEachForRuntimeIds.Body?.Handler != null)
			AddRuntimeIds(parallelForEachForRuntimeIds.Body.Handler, (path.Length == 0 ? "" : path + ".") + "$parallelforeach-body.0", result);
	}

	[JsonRpcMethod("workflow/cancel-run")]
	public bool CancelRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
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
		var session = Get(documentId);
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
		var session = Get(documentId);
		lock (session.RunGate) return DebugState(session);
	}

	[JsonRpcMethod("workflow/continue")]
	public bool ContinueRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.None) == true;
	}

	[JsonRpcMethod("workflow/step")]
	public bool StepRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Next) == true;
	}

	[JsonRpcMethod("workflow/step-over")]
	public bool StepOverRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Over) == true;
	}

	[JsonRpcMethod("workflow/step-out")]
	public bool StepOutRun(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		lock (session.RunGate) return session.DebugRun?.Resume(WorkflowDebugRun.StepMode.Out) == true;
	}

	[JsonRpcMethod("workflow/connect-flow")]
	public DesignerSessionState ConnectFlow(string sessionId, string documentId, long baseVersion, string sourceId, string targetId)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
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
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.ConnectFlowDecision(sourceId, targetId, trueBranch)) throw new InvalidOperationException("Select a Flow Decision and a Flowchart step in the same Flowchart.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("workflow/disconnect-flow")]
	public DesignerSessionState DisconnectFlow(string sessionId, string documentId, long baseVersion, string sourceId, string branch)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.DisconnectFlow(sourceId, branch)) throw new InvalidOperationException("The selected Flowchart edge cannot be disconnected.");
		RecordMutation(session, before);
		return State(session);
	}

	static WorkflowDebugState DebugState(DocumentSession session) => new() {
		IsRunning = session.RunningApplication != null,
		IsPaused = session.DebugRun?.IsPaused == true,
		CurrentElementId = session.DebugRun?.CurrentElementId ?? "",
		BreakpointElementIds = session.Breakpoints.OrderBy(id => id, StringComparer.Ordinal).ToList()
	};

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
				Records.Add(state.Activity.Name + ": " + state.State);
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
		readonly ManualResetEventSlim resume = new(false);
		readonly object gate = new();
		StepMode stepMode;
		string stepOrigin = "";
		bool cancelled;
		public bool IsPaused { get; private set; }
		public string CurrentElementId { get; private set; } = "";
		public enum StepMode { None, Next, Over, Out }

		public WorkflowDebugRun(Dictionary<string, string> activityIds, IEnumerable<string> breakpoints)
		{
			this.activityIds = activityIds;
			this.breakpoints = new HashSet<string>(breakpoints, StringComparer.Ordinal);
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
		if (activity is ForEach<int> forEachForChildren)
			children = children.Where(child => !ReferenceEquals(forEachForChildren.Body?.Handler, child) && !ReferenceEquals(forEachForChildren.Values?.Expression, child)).ToArray();
		if (activity is ParallelForEach<int> parallelForEachForChildren)
			children = children.Where(child => !ReferenceEquals(parallelForEachForChildren.Body?.Handler, child) && !ReferenceEquals(parallelForEachForChildren.Values?.Expression, child)).ToArray();
		if (activity is StateMachine) children = Array.Empty<Activity>();
		var properties = document.GetProperties(activity)
			.Select(p => new DesignerPropertyInfo { Name = p.Name, DisplayName = p.Name, Value = p.Value, TypeName = p.TypeName, IsReadOnly = p.IsReadOnly, Category = "Workflow" })
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
		if (activity is ForEach<int> forEach) {
			var id = (path.Length == 0 ? "" : path + ".") + "$foreach-body";
			childNodes.Add(new DesignerElementNode { Id = id, Path = path, Name = "Body", Type = "ForEachBody", Properties = new List<DesignerPropertyInfo> { new() { Name = "$itemName", DisplayName = "Item name", Value = forEach.Body?.Argument?.Name ?? "item", TypeName = "String", IsReadOnly = false, Category = "For Each" } }, Children = forEach.Body?.Handler == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, forEach.Body.Handler, id + ".0") } });
		}
		if (activity is ParallelForEach<int> parallelForEach) {
			var id = (path.Length == 0 ? "" : path + ".") + "$parallelforeach-body";
			childNodes.Add(new DesignerElementNode { Id = id, Path = path, Name = "Body", Type = "ParallelForEachBody", Properties = new List<DesignerPropertyInfo> { new() { Name = "$itemName", DisplayName = "Item name", Value = parallelForEach.Body?.Argument?.Name ?? "item", TypeName = "String", IsReadOnly = false, Category = "Parallel For Each" } }, Children = parallelForEach.Body?.Handler == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, parallelForEach.Body.Handler, id + ".0") } });
		}
		if (activity is Switch<int> @switch) {
			foreach (var entry in @switch.Cases.OrderBy(entry => entry.Key)) {
				var id = (path.Length == 0 ? "" : path + ".") + "$case-" + entry.Key;
				childNodes.Add(new DesignerElementNode {
					Id = id, Path = path, Name = "Case " + entry.Key, Type = "SwitchCase",
					Properties = new List<DesignerPropertyInfo> { new() { Name = "$caseValue", DisplayName = "Case value", Value = entry.Key.ToString(), TypeName = "Int32", IsReadOnly = false, Category = "Switch" } },
					Children = entry.Value == null ? new List<DesignerElementNode>() : new List<DesignerElementNode> { Node(document, entry.Value, id + ".0") }
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

	static Activity[] InspectChildren(Activity activity)
	{
		try { return WorkflowInspectionServices.GetActivities(activity).ToArray(); }
		catch (InvalidWorkflowException) { return Array.Empty<Activity>(); }
	}

	static int Count(Activity activity) => 1 + InspectChildren(activity).Sum(Count);

	DocumentSession Get(string documentId) => documents.Get(documentId);
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
