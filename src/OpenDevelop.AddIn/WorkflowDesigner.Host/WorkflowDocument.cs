using System.Activities;
using System.Activities.Statements;
using System.Activities.XamlIntegration;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Xaml;
using System.Xml;

namespace ICSharpCode.WorkflowDesigner.Host;

/// <summary>Owns one loaded .xaml activity tree and its edit/round-trip operations. Load and
/// Save go through <c>ActivityXamlServices.CreateBuilderReader/Writer</c> (the same mechanism
/// the classic WorkflowDesigner control uses internally) so the object graph is the real,
/// mutable <see cref="ActivityBuilder"/>/<see cref="Activity"/> tree, not a re-parsed copy - and
/// the round-trip preserves everything CoreWF itself understands about the file, the same
/// "run the real runtime object" rule the other four OOP backends already follow
/// (designer-common.md).</summary>
sealed class WorkflowDocument
{
	static readonly BindingFlags DeclaredPublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

	public Activity? Root { get; private set; }
	public ActivityBuilder? Builder { get; private set; }
	public bool LastParseSucceeded { get; private set; }
	public string Error { get; private set; } = "";

	public void Reset(string xamlText)
	{
		try {
			using var stringReader = new StringReader(xamlText);
			using var xmlReader = XmlReader.Create(stringReader);
			var builderReader = ActivityXamlServices.CreateBuilderReader(new XamlXmlReader(xmlReader));
			Builder = (ActivityBuilder)XamlServices.Load(builderReader);
			Root = Builder.Implementation;
			LastParseSucceeded = true;
			Error = "";
		} catch (Exception ex) {
			Builder = null;
			Root = null;
			LastParseSucceeded = false;
			Error = ex.Message;
		}
	}

	public string ToXaml()
	{
		var builder = Builder ?? new ActivityBuilder { Implementation = Root };
		var stringWriter = new StringWriter();
		using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true })) {
			var builderWriter = ActivityXamlServices.CreateBuilderWriter(new XamlXmlWriter(xmlWriter, new XamlSchemaContext()));
			XamlServices.Save(builderWriter, builder);
		}
		return stringWriter.ToString();
	}

	/// <summary>Projects workflow-level ActivityBuilder arguments without exposing CoreWF types
	/// outside the host process. The editor protocol can build its tabular Arguments surface from
	/// this stable shape.</summary>
	public IEnumerable<(string Name, string TypeName, string Direction, string DefaultValue)> GetArguments()
	{
		if (Builder == null) yield break;
		foreach (var property in Builder.Properties) {
			var (type, direction) = GetArgumentTypeAndDirection(property.Type);
			yield return (property.Name, type.Name, direction, GetLiteralValue(property.Value));
		}
	}

	public bool AddArgument(string name, string typeName, string defaultValue, string direction = "In")
	{
		if (Builder == null || string.IsNullOrWhiteSpace(name) || Builder.Properties.Any(property => property.Name == name)) return false;
		var type = ResolveArgumentType(typeName);
		var argumentType = MakeArgumentType(type, direction);
		if (argumentType == null || (!string.Equals(direction, "In", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(defaultValue))) return false;
		var propertyType = Builder.Properties.GetType().BaseType?.GetGenericArguments().LastOrDefault();
		if (propertyType == null) return false;
		var property = Activator.CreateInstance(propertyType);
		if (property == null) return false;
		propertyType.GetProperty("Name")?.SetValue(property, name);
		propertyType.GetProperty("Type")?.SetValue(property, argumentType);
		if (!TrySetArgumentDefault(property, type, defaultValue, direction)) return false;
		var add = Builder.Properties.GetType().GetMethod("Add", new[] { propertyType });
		if (add == null) return false;
		add.Invoke(Builder.Properties, new[] { property });
		return true;
	}

	public bool RemoveArgument(string name)
	{
		if (Builder == null || string.IsNullOrWhiteSpace(name)) return false;
		var property = Builder.Properties.FirstOrDefault(item => item.Name == name);
		if (property == null) return false;
		Builder.Properties.Remove(property);
		return true;
	}

	public bool UpdateArgument(string oldName, string newName, string typeName, string defaultValue, string direction = "In")
	{
		if (Builder == null || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
		var property = Builder.Properties.FirstOrDefault(item => item.Name == oldName);
		if (property == null || (oldName != newName && Builder.Properties.Any(item => item.Name == newName))) return false;
		var runtimeType = property.GetType();
		var type = ResolveArgumentType(typeName);
		var argumentType = MakeArgumentType(type, direction);
		if (argumentType == null || !TrySetArgumentDefault(property, type, defaultValue, direction)) return false;
		// DynamicActivityProperty is mutable. Updating it in place means a rejected literal or
		// type conversion cannot temporarily remove the existing argument from Builder.Properties.
		runtimeType.GetProperty("Name")?.SetValue(property, newName);
		runtimeType.GetProperty("Type")?.SetValue(property, argumentType);
		return true;
	}

	static (Type Type, string Direction) GetArgumentTypeAndDirection(Type? type)
	{
		if (type?.IsGenericType == true) {
			var definition = type.GetGenericTypeDefinition();
			if (definition == typeof(InArgument<>)) return (type.GetGenericArguments()[0], "In");
			if (definition == typeof(OutArgument<>)) return (type.GetGenericArguments()[0], "Out");
			if (definition == typeof(InOutArgument<>)) return (type.GetGenericArguments()[0], "InOut");
		}
		return (type ?? typeof(object), "In");
	}

	static Type? MakeArgumentType(Type type, string direction) => direction.Trim().ToLowerInvariant() switch {
		"in" => typeof(InArgument<>).MakeGenericType(type),
		"out" => typeof(OutArgument<>).MakeGenericType(type),
		"inout" => typeof(InOutArgument<>).MakeGenericType(type),
		_ => null
	};

	static bool TrySetArgumentDefault(object property, Type type, string value, string direction)
	{
		var valueProperty = property.GetType().GetProperty("Value");
		if (valueProperty == null) return false;
		if (!string.Equals(direction, "In", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value)) return false;
		if (string.IsNullOrWhiteSpace(value)) { valueProperty.SetValue(property, null); return true; }
		try {
			var argumentType = typeof(InArgument<>).MakeGenericType(type);
			if (TryCreateCodeExpression(type, value, out var expression)) {
				var argument = Activator.CreateInstance(argumentType);
				FindDeclaredProperty(argumentType, "Expression")?.SetValue(argument, expression);
				valueProperty.SetValue(property, argument);
			} else if (value.StartsWith("=", StringComparison.Ordinal)) {
				return false;
			} else {
				var literal = ConvertLiteral(value, type);
				valueProperty.SetValue(property, Activator.CreateInstance(argumentType, literal));
			}
			return true;
		} catch (Exception) { return false; }
	}

	static string GetLiteralValue(object? value)
	{
		var expression = GetDeclaredPropertyValue(value, "Expression");
		if (TryReadCodeExpression(expression, out var text)) return text;
		return GetDeclaredPropertyValue(expression, "Value")?.ToString() ?? "";
	}

	static object? GetDeclaredPropertyValue(object? instance, string name)
	{
		return FindDeclaredProperty(instance?.GetType(), name)?.GetValue(instance);
	}

	static PropertyInfo? FindDeclaredProperty(Type? type, string name)
	{
		for (; type != null; type = type.BaseType) {
			var property = type.GetProperty(name, DeclaredPublicInstance);
			if (property != null) return property;
		}
		return null;
	}

	static object ConvertLiteral(string value, Type type)
	{
		if (type == typeof(string)) return value;
		if (type == typeof(bool)) return bool.Parse(value);
		if (type == typeof(TimeSpan)) return TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type == typeof(DateTime)) return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
		if (type == typeof(Guid)) return Guid.Parse(value);
		if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);
		return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture)!;
	}

	public IEnumerable<(string Name, string TypeName, string Scope, string ScopeId, string DefaultValue)> GetVariables()
	{
		if (Root == null) yield break;
		foreach (var (activity, path) in ActivitiesWithPaths(Root, "")) {
			var variables = activity.GetType().GetProperty("Variables")?.GetValue(activity) as System.Collections.IEnumerable;
			if (variables == null) continue;
			foreach (var variable in variables.Cast<object>())
				yield return ((string?)variable.GetType().GetProperty("Name")?.GetValue(variable) ?? "", (variable.GetType().GetProperty("Type")?.GetValue(variable) as Type)?.Name ?? "Object", path.Length == 0 ? "Root activity" : activity.GetType().Name + " (" + path + ")", path, GetVariableDefault(variable));
			if (activity is StateMachine stateMachine) {
				for (var index = 0; index < stateMachine.States.Count; index++) {
					var state = stateMachine.States[index];
					var statePath = StateId(path, index);
					foreach (var variable in state.Variables)
						yield return (variable.Name, variable.Type.Name, "State " + state.DisplayName + " (" + statePath + ")", statePath, GetVariableDefault(variable));
				}
			}
		}
	}

	/// <summary>Lists only scopes that actually own a CoreWF Variables collection. This is used
	/// by the parent Variables panel instead of guessing from every projected tree node.</summary>
	public IEnumerable<(string Scope, string ScopeId)> GetVariableScopes()
	{
		if (Root == null) yield break;
		foreach (var (activity, path) in ActivitiesWithPaths(Root, "")) {
			if (activity.GetType().GetProperty("Variables")?.GetValue(activity) is System.Collections.IEnumerable)
				yield return (path.Length == 0 ? "Root activity" : activity.DisplayName + " (" + path + ")", path);
			if (activity is StateMachine stateMachine)
				for (var index = 0; index < stateMachine.States.Count; index++)
					yield return ("State " + stateMachine.States[index].DisplayName + " (" + StateId(path, index) + ")", StateId(path, index));
		}
	}

	/// <summary>Returns only variables whose lexical activity scope contains <paramref name="elementId"/>.
	/// The root scope is always visible; a sibling container's variables are not.</summary>
	public IEnumerable<(string Name, string TypeName, string Scope, string ScopeId, string DefaultValue)> GetVariablesVisibleAt(string? elementId)
	{
		var variables = string.IsNullOrEmpty(elementId)
			? GetVariables().Where(variable => variable.ScopeId.Length == 0).ToList()
			: GetVariables().Where(variable => variable.ScopeId.Length == 0 || elementId == variable.ScopeId || elementId.StartsWith(variable.ScopeId + ".", StringComparison.Ordinal)).ToList();
		if (elementId != null && TryFindForEachBody(elementId, out var forEach, out _) && !string.IsNullOrWhiteSpace(forEach.Body?.Argument?.Name))
			variables.Add((forEach.Body.Argument.Name, "Int32", "For Each item", elementId, ""));
		if (elementId != null && TryFindParallelForEachBody(elementId, out var parallelForEach, out _) && !string.IsNullOrWhiteSpace(parallelForEach.Body?.Argument?.Name))
			variables.Add((parallelForEach.Body.Argument.Name, "Int32", "Parallel For Each item", elementId, ""));
		if (elementId != null && TryFindCatch(elementId, out _, out var @catch, out _, out _) && @catch.GetType().GetProperty("Action")?.GetValue(@catch) is ActivityDelegate action && action.GetType().GetProperty("Argument")?.GetValue(action) is DelegateArgument { Name.Length: > 0 } argument)
			variables.Add((argument.Name, @catch.ExceptionType.Name, "Catch exception", elementId, ""));
		return variables;
	}

	/// <summary>Returns the names valid in an expression at a design location. Dynamic activity
	/// inputs are part of the workflow lexical environment; <c>Out</c> arguments are omitted
	/// because they cannot be read as inputs. Variables are yielded first so a local variable
	/// shadows an equally named workflow argument when a caller de-duplicates names.</summary>
	public IEnumerable<(string Name, string TypeName, string Scope, string ScopeId, string DefaultValue)> GetExpressionSymbolsVisibleAt(string? elementId)
	{
		foreach (var variable in GetVariablesVisibleAt(elementId)) yield return variable;
		if (Builder == null) yield break;
		foreach (var property in Builder.Properties) {
			var (type, direction) = GetArgumentTypeAndDirection(property.Type);
			if (string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase)) continue;
			yield return (property.Name, type.Name, "Workflow " + direction + " argument", "", GetLiteralValue(property.Value));
		}
	}

	public Type? GetVariableRuntimeTypeVisibleAt(string name, string? elementId)
	{
		if (Root == null || string.IsNullOrWhiteSpace(name)) return null;
		if (elementId != null && TryFindState(elementId, out _, out var scopedState, out _, out _) && scopedState.Variables.FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal)) is { } stateVariable) return stateVariable.Type;
		if (elementId != null && TryFindCatch(elementId, out _, out var @catch, out _, out _) && @catch.GetType().GetProperty("Action")?.GetValue(@catch) is ActivityDelegate action && string.Equals((action.GetType().GetProperty("Argument")?.GetValue(action) as DelegateArgument)?.Name, name, StringComparison.Ordinal)) return @catch.ExceptionType;
		var variable = ActivitiesWithPaths(Root, "")
			.Where(item => string.IsNullOrEmpty(item.Path) || !string.IsNullOrEmpty(elementId) && (elementId == item.Path || elementId.StartsWith(item.Path + ".", StringComparison.Ordinal)))
			.OrderByDescending(item => item.Path.Length)
			.SelectMany(item => (item.Activity.GetType().GetProperty("Variables")?.GetValue(item.Activity) as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object>())
			.FirstOrDefault(item => string.Equals(item.GetType().GetProperty("Name")?.GetValue(item) as string, name, StringComparison.Ordinal));
		var variableType = variable?.GetType().GetProperty("Type")?.GetValue(variable) as Type;
		if (variableType != null) return variableType;
		if (Builder == null) return null;
		var argument = Builder.Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));
		if (argument == null) return null;
		var (argumentType, direction) = GetArgumentTypeAndDirection(argument.Type);
		return string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase) ? null : argumentType;
	}

	/// <summary>Converts the transport-only Run dialog values into declared workflow inputs.
	/// Values are deliberately parsed in the isolated host, never by the addin.</summary>
	public bool TryCreateRunInputs(IReadOnlyDictionary<string, string>? values, out Dictionary<string, object> inputs, out string error)
	{
		inputs = new Dictionary<string, object>(StringComparer.Ordinal);
		error = "";
		if (values == null || values.Count == 0) return true;
		if (Builder == null) { error = "No workflow is loaded."; return false; }
		var declarations = Builder.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
		foreach (var pair in values) {
			if (!declarations.TryGetValue(pair.Key, out var property)) { error = $"'{pair.Key}' is not a declared workflow input."; return false; }
			var (type, direction) = GetArgumentTypeAndDirection(property.Type);
			if (string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase)) { error = $"'{pair.Key}' is an output argument and cannot be supplied when running."; return false; }
			var value = ConvertSimple(type, pair.Value);
			if (value == null && type != typeof(string)) { error = $"'{pair.Value}' is not a valid {type.Name} value for '{pair.Key}'."; return false; }
			inputs.Add(pair.Key, value!);
		}
		return true;
	}

	/// <summary>Materializes the editable ActivityBuilder as the DynamicActivity expected by
	/// WorkflowApplication. Running <see cref="Root"/> directly would discard the builder's
	/// declared workflow arguments.</summary>
	public Activity? CreateRuntimeActivity()
	{
		if (Builder?.Implementation == null) return null;
		var runtime = new DynamicActivity { Name = Builder.Name, Implementation = () => Builder.Implementation };
		foreach (var property in Builder.Properties) {
			var copy = new DynamicActivityProperty { Name = property.Name, Type = property.Type, Value = property.Value };
			foreach (var attribute in property.Attributes) copy.Attributes.Add(attribute);
			runtime.Properties.Add(copy);
		}
		foreach (var constraint in Builder.Constraints) runtime.Constraints.Add(constraint);
		return runtime;
	}

	static IEnumerable<(Activity Activity, string Path)> ActivitiesWithPaths(Activity root, string path)
	{
		yield return (root, path);
		Activity[] children;
		try { children = WorkflowInspectionServices.GetActivities(root).ToArray(); }
		catch (InvalidWorkflowException) { children = Array.Empty<Activity>(); }
		for (var index = 0; index < children.Length; index++)
			foreach (var item in ActivitiesWithPaths(children[index], path.Length == 0 ? index.ToString() : path + "." + index)) yield return item;
	}

	public bool AddVariable(string name, string typeName, string defaultValue, string scopeId = "")
	{
		var scope = FindVariableScope(scopeId);
		if (scope == null || string.IsNullOrWhiteSpace(name)) return false;
		var variables = scope.GetType().GetProperty("Variables")?.GetValue(scope);
		if (variables is not System.Collections.IEnumerable existing || existing.Cast<object>().Any(variable => (string?)variable.GetType().GetProperty("Name")?.GetValue(variable) == name)) return false;
		var variable = CreateVariable(name, ResolveArgumentType(typeName), defaultValue);
		if (variable == null) return false;
		var add = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Add" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variable.GetType()));
		if (add == null) return false;
		add.Invoke(variables, new[] { variable });
		return true;
	}

	public bool UpdateVariable(string oldName, string newName, string typeName, string defaultValue, string scopeId = "")
	{
		var scope = FindVariableScope(scopeId);
		if (scope == null || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
		var variables = scope.GetType().GetProperty("Variables")?.GetValue(scope);
		if (variables is not System.Collections.IEnumerable existing) return false;
		var items = existing.Cast<object>().ToList();
		var old = items.FirstOrDefault(item => (string?)item.GetType().GetProperty("Name")?.GetValue(item) == oldName);
		if (old == null || (oldName != newName && items.Any(item => (string?)item.GetType().GetProperty("Name")?.GetValue(item) == newName))) return false;
		var replacement = CreateVariable(newName, ResolveArgumentType(typeName), defaultValue);
		if (replacement == null) return false;
		var remove = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Remove" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(old.GetType()));
		var add = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Add" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(replacement.GetType()));
		if (remove == null || add == null) return false;
		try {
			if (remove.Invoke(variables, new[] { old }) is false) return false;
			add.Invoke(variables, new[] { replacement });
			return true;
		} catch {
			// The replacement was validated before removing the original. If a collection-level
			// constraint nevertheless rejects it, restore the old variable before reporting failure.
			try { add.Invoke(variables, new[] { old }); } catch { }
			return false;
		}
	}

	static Variable? CreateVariable(string name, Type type, string defaultValue)
	{
		try {
			if (string.IsNullOrWhiteSpace(defaultValue))
				return Variable.Create(name, type, VariableModifiers.None);
			if (TryCreateCodeExpression(type, defaultValue, out var expression)) {
				var variable = Variable.Create(name, type, VariableModifiers.None);
				var defaultProperty = FindDeclaredProperty(variable.GetType(), "Default");
				if (defaultProperty == null) return null;
				defaultProperty.SetValue(variable, expression);
				return variable;
			}
			if (defaultValue.StartsWith("=", StringComparison.Ordinal)) return null;
			// Use Variable<T>(name, value), rather than setting the non-generic Variable.Default
			// after construction. The latter is legal at runtime but UiPath/CoreWF's XAML writer
			// serializes it as a reference to the variable itself instead of a literal default.
			var variableType = typeof(Variable<>).MakeGenericType(type);
			return Activator.CreateInstance(variableType, name, ConvertLiteral(defaultValue, type)) as Variable;
		} catch (Exception) { return null; }
	}

	static string GetVariableDefault(object variable)
	{
		var expression = GetDeclaredPropertyValue(variable, "Default");
		if (TryReadCodeExpression(expression, out var text)) return text;
		return expression?.GetType().GetProperty("Value")?.GetValue(expression)?.ToString() ?? "";
	}

	public bool RemoveVariable(string name, string scopeId = "")
	{
		var scope = FindVariableScope(scopeId);
		if (scope == null || string.IsNullOrWhiteSpace(name)) return false;
		var variables = scope.GetType().GetProperty("Variables")?.GetValue(scope);
		if (variables is not System.Collections.IEnumerable existing) return false;
		var variable = existing.Cast<object>().FirstOrDefault(item => (string?)item.GetType().GetProperty("Name")?.GetValue(item) == name);
		if (variable == null) return false;
		var remove = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Remove" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variable.GetType()));
		if (remove == null) return false;
		remove.Invoke(variables, new[] { variable });
		return true;
	}

	object? FindVariableScope(string scopeId) => TryFindState(scopeId, out _, out var state, out _, out var suffix) && suffix.Length == 0 ? state : Find(scopeId);

	static Type ResolveArgumentType(string name) => name.Trim().ToLowerInvariant() switch {
		"string" => typeof(string), "int" or "int32" => typeof(int), "bool" or "boolean" => typeof(bool),
		"double" => typeof(double), "decimal" => typeof(decimal), "timespan" => typeof(TimeSpan),
		"datetime" => typeof(DateTime), "guid" => typeof(Guid), _ => Type.GetType(name) ?? typeof(object)
	};

	/// <summary>Finds an activity by its structural path id ("" for the root, "0.2.1" for the
	/// second child of the third child of the root - dot-joined <see cref="WorkflowInspectionServices"/>
	/// child indices). Activities have no required design-time name (unlike WinForms/WPF/MewUI
	/// elements, which always get one), so the path is the only stable-enough identity a fresh
	/// tree walk can reproduce.</summary>
	public Activity? Find(string id)
	{
		if (Root == null) return null;
		if (id.Length == 0) return Root;
		if (TryFindState(id, out _, out var state, out _, out var stateSuffix)) {
			if (TryFindTransition(id, out _, out var transition, out _, out var transitionSuffix)) {
				if (transitionSuffix == ".0") return transition.Trigger;
				if (transitionSuffix == ".1") return transition.Action;
				return null;
			}
			if (stateSuffix is not ".0" and not ".1") return null;
			return stateSuffix == ".0" ? state.Entry : state.Exit;
		}
		if (TryFindForEachBody(id, out var forEach, out var forEachSuffix)) {
			if (!forEachSuffix.StartsWith(".0", StringComparison.Ordinal) || forEach.Body?.Handler == null) return null;
			var currentForEachActivity = forEach.Body.Handler;
			var remainingForEachPath = forEachSuffix.Length == 2 ? "" : forEachSuffix[3..];
			foreach (var part in remainingForEachPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				if (!int.TryParse(part, out var index)) return null;
				var children = WorkflowInspectionServices.GetActivities(currentForEachActivity).ToArray();
				if (index < 0 || index >= children.Length) return null;
				currentForEachActivity = children[index];
			}
			return currentForEachActivity;
		}
		if (TryFindParallelForEachBody(id, out var parallelForEach, out var parallelForEachSuffix)) {
			if (!parallelForEachSuffix.StartsWith(".0", StringComparison.Ordinal) || parallelForEach.Body?.Handler == null) return null;
			var currentParallelActivity = parallelForEach.Body.Handler;
			var remainingParallelPath = parallelForEachSuffix.Length == 2 ? "" : parallelForEachSuffix[3..];
			foreach (var part in remainingParallelPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				if (!int.TryParse(part, out var index)) return null;
				var children = WorkflowInspectionServices.GetActivities(currentParallelActivity).ToArray();
				if (index < 0 || index >= children.Length) return null;
				currentParallelActivity = children[index];
			}
			return currentParallelActivity;
		}
		if (TryFindPickBranch(id, out _, out var pickBranch, out _, out var pickSuffix)) {
			if (!pickSuffix.StartsWith(".0", StringComparison.Ordinal) && !pickSuffix.StartsWith(".1", StringComparison.Ordinal)) return null;
			var currentPickActivity = pickSuffix.StartsWith(".0", StringComparison.Ordinal) ? pickBranch.Trigger : pickBranch.Action;
			if (currentPickActivity == null) return null;
			var remainingPickPath = pickSuffix.Length == 2 ? "" : pickSuffix[3..];
			foreach (var part in remainingPickPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				if (!int.TryParse(part, out var index)) return null;
				var children = WorkflowInspectionServices.GetActivities(currentPickActivity).ToArray();
				if (index < 0 || index >= children.Length) return null;
				currentPickActivity = children[index];
			}
			return currentPickActivity;
		}
		if (TryFindSwitchBranch(id, out _, out var branch, out _, out var branchSuffix)) {
			if (branch == null || branchSuffix is not ".0" && !branchSuffix.StartsWith(".0.", StringComparison.Ordinal)) return null;
			var currentBranch = branch;
			var remaining = branchSuffix.Length == 2 ? "" : branchSuffix[3..];
			foreach (var part in remaining.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				if (!int.TryParse(part, out var index)) return null;
				var children = WorkflowInspectionServices.GetActivities(currentBranch).ToArray();
				if (index < 0 || index >= children.Length) return null;
				currentBranch = children[index];
			}
			return currentBranch;
		}
		var catchMarker = id.LastIndexOf("$catch-", StringComparison.Ordinal);
		if (catchMarker >= 0) {
			var ownerPath = catchMarker == 0 ? "" : id[..catchMarker].TrimEnd('.');
			if (Find(ownerPath) is not TryCatch owner) return null;
			var suffix = id[(catchMarker + "$catch-".Length)..];
			var separator = suffix.IndexOf('.');
			if (!int.TryParse(separator < 0 ? suffix : suffix[..separator], out var catchIndex) || catchIndex < 0 || catchIndex >= owner.Catches.Count) return null;
			if (separator < 0) return null;
			var handlerPath = suffix[(separator + 1)..];
			if (!handlerPath.StartsWith("0", StringComparison.Ordinal)) return null;
			if (owner.Catches[catchIndex].GetType().GetProperty("Action")?.GetValue(owner.Catches[catchIndex]) is not ActivityDelegate action || action.Handler == null) return null;
			var currentHandler = action.Handler;
			var remaining = handlerPath.Length == 1 ? "" : handlerPath[1..].TrimStart('.');
			foreach (var part in remaining.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
				if (!int.TryParse(part, out var index)) return null;
				var children = WorkflowInspectionServices.GetActivities(currentHandler).ToArray();
				if (index < 0 || index >= children.Length) return null;
				currentHandler = children[index];
			}
			return currentHandler;
		}
		var current = Root;
		foreach (var part in id.Split('.')) {
			if (!int.TryParse(part, out var index)) return null;
			var children = WorkflowInspectionServices.GetActivities(current).ToArray();
			if (index < 0 || index >= children.Length) return null;
			current = children[index];
		}
		return current;
	}

	public bool SetProperty(string id, string propertyName, string value)
	{
		if (TryFindTransition(id, out var transitionOwner, out var transition, out _, out var transitionSuffix) && transitionSuffix.Length == 0) {
			if (string.Equals(propertyName, "$displayName", StringComparison.Ordinal)) { transition.DisplayName = value; return true; }
			if (string.Equals(propertyName, "$target", StringComparison.Ordinal)) {
				if (string.IsNullOrWhiteSpace(value)) { transition.To = null; return true; }
				if (!TryFindState(value, out var targetOwner, out var target, out _, out var targetSuffix) || targetSuffix.Length != 0 || !ReferenceEquals(targetOwner, transitionOwner)) return false;
				transition.To = target; return true;
			}
			if (string.Equals(propertyName, nameof(Transition.Condition), StringComparison.Ordinal)) {
				var condition = ConvertToPropertyType(typeof(Activity<bool>), value, id);
				if (condition is not Activity<bool> typedCondition) return false;
				transition.Condition = typedCondition; return true;
			}
		}
		if (TryFindState(id, out var stateMachine, out var state, out _, out var stateSuffix) && stateSuffix.Length == 0) {
			if (string.Equals(propertyName, "$displayName", StringComparison.Ordinal)) { state.DisplayName = value; return true; }
			if (string.Equals(propertyName, "IsFinal", StringComparison.Ordinal) && bool.TryParse(value, out var isFinal)) {
				if (isFinal && (state.Exit != null || state.Transitions.Count != 0)) return false;
				state.IsFinal = isFinal; return true;
			}
			if (string.Equals(propertyName, "$initial", StringComparison.Ordinal) && bool.TryParse(value, out var isInitial)) {
				if (!isInitial) return !ReferenceEquals(stateMachine.InitialState, state);
				stateMachine.InitialState = state; return true;
			}
		}
		if (string.Equals(propertyName, "$itemName", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$")) {
			if (TryFindForEachBody(id, out var forEach, out var suffix) && suffix.Length == 0 && forEach.Body != null) { forEach.Body.Argument ??= new DelegateInArgument<int>(); forEach.Body.Argument.Name = value; return true; }
			if (TryFindParallelForEachBody(id, out var parallelForEach, out suffix) && suffix.Length == 0 && parallelForEach.Body != null) { parallelForEach.Body.Argument ??= new DelegateInArgument<int>(); parallelForEach.Body.Argument.Name = value; return true; }
		}
		if (string.Equals(propertyName, "$exceptionName", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$") && TryFindCatch(id, out _, out var namedCatch, out var namedCatchIndex, out _) && id.EndsWith("$catch-" + namedCatchIndex, StringComparison.Ordinal) && namedCatch.GetType().GetProperty("Action")?.GetValue(namedCatch) is ActivityDelegate namedAction) { var argument = namedAction.GetType().GetProperty("Argument"); if (argument == null) return false; var delegateArgument = argument.GetValue(namedAction) as DelegateArgument ?? (DelegateArgument)Activator.CreateInstance(typeof(DelegateInArgument<>).MakeGenericType(namedCatch.ExceptionType))!; delegateArgument.Name = value; argument.SetValue(namedAction, delegateArgument); return true; }
		if (TryFindCatch(id, out var catchOwner, out var catchNode, out var catchIndex, out _)
			&& id.EndsWith("$catch-" + catchIndex, StringComparison.Ordinal) && string.Equals(propertyName, "$exceptionType", StringComparison.Ordinal)) {
			var exceptionType = Type.GetType(value) ?? typeof(Exception).Assembly.GetType(value);
			if (exceptionType == null || !typeof(Exception).IsAssignableFrom(exceptionType)) return false;
			if (exceptionType == catchNode.ExceptionType) return true;
			if (catchOwner.Catches.Any(other => !ReferenceEquals(other, catchNode) && other.ExceptionType == exceptionType)) return false;
			var replacementType = typeof(Catch<>).MakeGenericType(exceptionType);
			if (Activator.CreateInstance(replacementType) is not Catch replacement) return false;
			var actionType = typeof(ActivityAction<>).MakeGenericType(exceptionType);
			var action = Activator.CreateInstance(actionType) as ActivityDelegate;
			if (action == null) return false;
			var exceptionName = "exception";
			if (catchNode.GetType().GetProperty("Action")?.GetValue(catchNode) is ActivityDelegate oldAction) {
				action.Handler = oldAction.Handler;
				exceptionName = (oldAction.GetType().GetProperty("Argument")?.GetValue(oldAction) as DelegateArgument)?.Name ?? exceptionName;
			}
			var argument = (DelegateArgument)Activator.CreateInstance(typeof(DelegateInArgument<>).MakeGenericType(exceptionType))!;
			argument.Name = exceptionName;
			action.GetType().GetProperty("Argument")?.SetValue(action, argument);
			replacement.GetType().GetProperty("Action")?.SetValue(replacement, action);
			catchOwner.Catches[catchIndex] = replacement;
			return true;
		}
		if (TryFindSwitchBranch(id, out var switchOwner, out var switchBranch, out var switchKey, out var switchSuffix)
			&& switchSuffix.Length == 0 && switchKey is int existingKey && string.Equals(propertyName, "$caseValue", StringComparison.Ordinal)) {
			if (!int.TryParse(value, out var replacementKey) || switchBranch == null) return false;
			if (replacementKey == existingKey) return true;
			if (switchOwner.Cases.ContainsKey(replacementKey)) return false;
			switchOwner.Cases.Remove(existingKey);
			switchOwner.Cases.Add(replacementKey, switchBranch);
			return true;
		}
		if (TryFindFlowDecision(id, out var decision, out var flowchartPath)) {
			var decisionProperty = decision.GetType().GetProperty(propertyName);
			if (decisionProperty == null || !decisionProperty.CanWrite) return false;
			var decisionValue = ConvertToPropertyType(decisionProperty.PropertyType, value, flowchartPath);
			if (decisionValue == null) return false;
			decisionProperty.SetValue(decision, decisionValue);
			return true;
		}
		var activity = Find(id);
		if (activity == null) return false;
		if (propertyName == "$displayName") { activity.DisplayName = value; return true; }
		var property = activity.GetType().GetProperty(propertyName);
		if (property == null || !property.CanWrite) return false;
		var converted = ConvertToPropertyType(property.PropertyType, value, id);
		if (converted == null) return false;
		property.SetValue(activity, converted);
		return true;
	}

	/// <summary>Adds a new activity of <paramref name="typeName"/> (resolved against
	/// <c>System.Activities.Statements</c> when unqualified, matching the toolbox catalog the
	/// host reports) into a collection/slot parent (e.g. <see cref="Sequence.Activities"/>) or a
	/// <see cref="Flowchart"/> step. Returns the new child's path id, or null when the target has
	/// no supported child surface or the type can't be resolved/constructed.</summary>
	public string? AddChild(string parentId, string typeName)
	{
		if (TryFindTransition(parentId, out _, out var transitionOwner, out _, out var transitionSuffix) && transitionSuffix.Length == 0) {
			var transitionActivityType = ResolveActivityType(typeName);
			if (transitionActivityType == null || Activator.CreateInstance(transitionActivityType) is not Activity transitionActivity) return null;
			InitializeRequiredArguments(transitionActivity);
			if (transitionOwner.Trigger == null) { transitionOwner.Trigger = transitionActivity; return parentId + ".0"; }
			if (transitionOwner.Action == null) { transitionOwner.Action = transitionActivity; return parentId + ".1"; }
			return null;
		}
		if (TryFindState(parentId, out _, out var stateOwner, out _, out var stateSuffix) && stateSuffix.Length == 0) {
			if (string.Equals(typeName, "Transition", StringComparison.Ordinal)) {
				if (stateOwner.IsFinal) return null;
				stateOwner.Transitions.Add(new Transition { DisplayName = "Transition " + (stateOwner.Transitions.Count + 1) });
				return parentId + ".$transition-" + (stateOwner.Transitions.Count - 1);
			}
			var stateActivityType = ResolveActivityType(typeName);
			if (stateActivityType == null || Activator.CreateInstance(stateActivityType) is not Activity stateActivity) return null;
			InitializeRequiredArguments(stateActivity);
			if (stateOwner.Entry == null) { stateOwner.Entry = stateActivity; return parentId + ".0"; }
			if (stateOwner.IsFinal) return null;
			if (stateOwner.Exit == null) { stateOwner.Exit = stateActivity; return parentId + ".1"; }
			return null;
		}
		if (TryFindForEachBody(parentId, out var forEach, out var forEachSuffix) && forEachSuffix.Length == 0) {
			if (forEach.Body?.Handler != null) return null;
			var bodyActivityType = ResolveActivityType(typeName);
			if (bodyActivityType == null || Activator.CreateInstance(bodyActivityType) is not Activity bodyActivity) return null;
			InitializeRequiredArguments(bodyActivity);
			forEach.Body ??= new ActivityAction<int> { Argument = new DelegateInArgument<int> { Name = "item" } };
			forEach.Body.Handler = bodyActivity;
			return parentId + ".0";
		}
		if (TryFindParallelForEachBody(parentId, out var parallelForEach, out var parallelForEachSuffix) && parallelForEachSuffix.Length == 0) {
			if (parallelForEach.Body?.Handler != null) return null;
			var bodyActivityType = ResolveActivityType(typeName);
			if (bodyActivityType == null || Activator.CreateInstance(bodyActivityType) is not Activity bodyActivity) return null;
			InitializeRequiredArguments(bodyActivity);
			parallelForEach.Body ??= new ActivityAction<int> { Argument = new DelegateInArgument<int> { Name = "item" } };
			parallelForEach.Body.Handler = bodyActivity;
			return parentId + ".0";
		}
		if (TryFindPickBranch(parentId, out _, out var pickBranch, out _, out var pickSuffix) && pickSuffix.Length == 0) {
			var branchActivityType = ResolveActivityType(typeName);
			if (branchActivityType == null || Activator.CreateInstance(branchActivityType) is not Activity branchActivity) return null;
			InitializeRequiredArguments(branchActivity);
			if (pickBranch.Trigger == null) { pickBranch.Trigger = branchActivity; return parentId + ".0"; }
			if (pickBranch.Action == null) { pickBranch.Action = branchActivity; return parentId + ".1"; }
			return null;
		}
		if (TryFindSwitchBranch(parentId, out var switchOwner, out var switchBranch, out var switchKey, out var switchSuffix) && switchSuffix.Length == 0) {
			if (switchBranch != null) return null;
			var handlerType = ResolveActivityType(typeName);
			if (handlerType == null || Activator.CreateInstance(handlerType) is not Activity handler) return null;
			InitializeRequiredArguments(handler);
			if (switchKey is int caseKey) switchOwner.Cases[caseKey] = handler;
			else switchOwner.Default = handler;
			return parentId + ".0";
		}
		if (TryFindCatch(parentId, out _, out var parentCatch, out var parentCatchIndex, out _) && parentId.EndsWith("$catch-" + parentCatchIndex, StringComparison.Ordinal)) {
			if (parentCatch.GetType().GetProperty("Action")?.GetValue(parentCatch) is not ActivityDelegate action || action.Handler != null) return null;
			var handlerType = ResolveActivityType(typeName);
			if (handlerType == null || Activator.CreateInstance(handlerType) is not Activity handler) return null;
			InitializeRequiredArguments(handler);
			action.Handler = handler;
			return parentId + ".0";
		}
		var parent = Find(parentId);
		if (parent == null) return null;
		if (parent is StateMachine stateMachine && string.Equals(typeName, "State", StringComparison.Ordinal)) {
			var state = new State { DisplayName = "State " + (stateMachine.States.Count + 1) };
			stateMachine.States.Add(state);
			stateMachine.InitialState ??= state;
			return StateId(parentId, stateMachine.States.Count - 1);
		}
		if (parent is Pick pick && string.Equals(typeName, "PickBranch", StringComparison.Ordinal)) {
			pick.Branches.Add(new PickBranch());
			return PickBranchId(parentId, pick.Branches.Count - 1);
		}
		if (parent is Switch<int> intSwitch && string.Equals(typeName, "SwitchCase", StringComparison.Ordinal)) {
			var key = 0;
			while (intSwitch.Cases.ContainsKey(key)) key++;
			intSwitch.Cases.Add(key, new Sequence());
			return SwitchCaseId(parentId, key);
		}
		if (parent is Switch<int> defaultSwitch && string.Equals(typeName, "SwitchDefault", StringComparison.Ordinal)) {
			if (defaultSwitch.Default != null) return null;
			defaultSwitch.Default = new Sequence();
			return SwitchDefaultId(parentId);
		}
		if (parent is TryCatch tryCatch && string.Equals(typeName, "Catch", StringComparison.Ordinal)) {
			var catchType = typeof(Catch<>).MakeGenericType(typeof(Exception));
			if (Activator.CreateInstance(catchType) is not Catch newCatch) return null;
			var actionType = typeof(ActivityAction<>).MakeGenericType(typeof(Exception));
			var action = Activator.CreateInstance(actionType) as ActivityDelegate;
			if (action == null) return null;
			var argument = (DelegateArgument)Activator.CreateInstance(typeof(DelegateInArgument<>).MakeGenericType(typeof(Exception)))!;
			argument.Name = "exception";
			action.GetType().GetProperty("Argument")?.SetValue(action, argument);
			newCatch.GetType().GetProperty("Action")?.SetValue(newCatch, action);
			tryCatch.Catches.Add(newCatch);
			return CatchId(parentId, tryCatch.Catches.Count - 1);
		}
		if (parent is Flowchart decisionFlowchart && string.Equals(typeName, "FlowDecision", StringComparison.Ordinal)) {
			var decision = new FlowDecision { Condition = new System.Activities.Expressions.Literal<bool>(false) };
			// Keep the default insertion order executable: a decision dropped after a linear
			// run of steps is the next node of that run. Its outgoing branches remain explicit
			// (there is no safe default for choosing True versus False).
			var predecessor = decisionFlowchart.Nodes.OfType<FlowStep>().LastOrDefault(step => step.Next == null);
			if (predecessor != null) predecessor.Next = decision;
			else if (decisionFlowchart.StartNode == null) decisionFlowchart.StartNode = decision;
			decisionFlowchart.Nodes.Add(decision);
			return FlowDecisionId(parentId, decisionFlowchart.Nodes.Count - 1);
		}
		var childType = ResolveActivityType(typeName);
		if (childType == null) return null;
		if (Activator.CreateInstance(childType) is not Activity child) return null;
		InitializeRequiredArguments(child);
		if (parent is Flowchart flowchart) {
			// A Flowchart owns FlowNode objects rather than an Activity collection. Wrap the
			// inserted activity in a FlowStep and connect it after the prior step so the
			// generated workflow is executable immediately, not merely a disconnected graph.
			var step = new FlowStep { Action = child };
			var previous = flowchart.Nodes.OfType<FlowStep>().LastOrDefault();
			if (previous != null) previous.Next = step;
			else flowchart.StartNode = step;
			flowchart.Nodes.Add(step);
			return parentId.Length == 0 ? (flowchart.Nodes.OfType<FlowStep>().Count() - 1).ToString() : parentId + "." + (flowchart.Nodes.OfType<FlowStep>().Count() - 1);
		} else {
			var collectionProperty = FindActivityCollectionProperty(parent.GetType());
			if (collectionProperty != null) {
			var collection = collectionProperty.GetValue(parent);
			var addMethod = collectionProperty.PropertyType.GetMethod("Add", new[] { collectionProperty.PropertyType.GetGenericArguments()[0] });
			addMethod?.Invoke(collection, new object[] { child });
			} else {
			// Structured activities use writable Activity slots instead of a collection: If.Then/
			// Else, While.Body and similar CoreWF shapes. Fill the first empty slot in declaration
			// order, which mirrors the designer's natural "Then before Else" insertion behavior.
			var slot = parent.GetType().GetProperties()
				.FirstOrDefault(property => property.CanWrite && typeof(Activity).IsAssignableFrom(property.PropertyType)
					&& property.GetValue(parent) == null);
			if (slot == null) return null;
			slot.SetValue(parent, child);
			}
		}
		var index = WorkflowInspectionServices.GetActivities(parent).ToList().IndexOf(child);
		if (index < 0) return null;
		return parentId.Length == 0 ? index.ToString() : parentId + "." + index;
	}

	/// <summary>Creates a detached, XAML-round-tripped copy of the selected activities. Keeping
	/// the clipboard as XAML rather than live objects is important: a pasted subtree must never
	/// share arguments, variables, or implementation objects with its source subtree.</summary>
	public bool Copy(IEnumerable<string> ids)
	{
		var requested = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
		var selected = requested
			.Where(id => !requested.Any(parent => parent != id && id.StartsWith(parent + ".", StringComparison.Ordinal)))
			.Select(id => Find(id)).Where(activity => activity != null).Cast<Activity>().ToList();
		if (selected.Count == 0) return false;
		var serialized = new List<string>();
		foreach (var activity in selected) {
			var xaml = SerializeActivity(activity);
			if (xaml == null) return false;
			serialized.Add(xaml);
		}
		clipboard = serialized;
		return true;
	}

	public IReadOnlyList<string> Clipboard => clipboard;

	/// <summary>Pastes the most recently copied activity subtrees as fresh CLR objects. The
	/// destination follows the same slot/collection rules as toolbox insertion, so a paste cannot
	/// turn a valid WF shape into an invalid object graph.</summary>
	public IReadOnlyList<string>? Paste(string parentId, IReadOnlyList<string>? clipboardContents = null)
	{
		var source = clipboardContents ?? clipboard;
		if (source.Count == 0) return null;
		var before = ToXaml();
		var pasted = new List<string>();
		foreach (var xaml in source) {
			var child = DeserializeActivity(xaml);
			if (child == null) { Reset(before); return null; }
			var id = AddExistingChild(parentId, child);
			if (id == null) { Reset(before); return null; }
			pasted.Add(id);
		}
		return pasted;
	}

	List<string> clipboard = new();

	static string? SerializeActivity(Activity activity)
	{
		try {
			var builder = new ActivityBuilder { Name = "WorkflowDesignerClipboard", Implementation = activity };
			using var writer = new StringWriter();
			using var xml = XmlWriter.Create(writer);
			XamlServices.Save(ActivityXamlServices.CreateBuilderWriter(new XamlXmlWriter(xml, new XamlSchemaContext())), builder);
			return writer.ToString();
		} catch { return null; }
	}

	static Activity? DeserializeActivity(string xaml)
	{
		var document = new WorkflowDocument();
		document.Reset(xaml);
		return document.LastParseSucceeded ? document.Root : null;
	}

	string? AddExistingChild(string parentId, Activity child)
	{
		if (TryFindTransition(parentId, out _, out var transition, out _, out var transitionSuffix) && transitionSuffix.Length == 0) {
			if (transition.Trigger == null) { transition.Trigger = child; return parentId + ".0"; }
			if (transition.Action == null) { transition.Action = child; return parentId + ".1"; }
			return null;
		}
		if (TryFindState(parentId, out _, out var state, out _, out var stateSuffix) && stateSuffix.Length == 0) {
			if (state.Entry == null) { state.Entry = child; return parentId + ".0"; }
			if (!state.IsFinal && state.Exit == null) { state.Exit = child; return parentId + ".1"; }
			return null;
		}
		if (TryFindForEachBody(parentId, out var forEach, out var forEachSuffix) && forEachSuffix.Length == 0 && forEach.Body?.Handler == null) {
			forEach.Body ??= new ActivityAction<int> { Argument = new DelegateInArgument<int> { Name = "item" } };
			forEach.Body.Handler = child;
			return parentId + ".0";
		}
		if (TryFindParallelForEachBody(parentId, out var parallelForEach, out var parallelForEachSuffix) && parallelForEachSuffix.Length == 0 && parallelForEach.Body?.Handler == null) {
			parallelForEach.Body ??= new ActivityAction<int> { Argument = new DelegateInArgument<int> { Name = "item" } };
			parallelForEach.Body.Handler = child;
			return parentId + ".0";
		}
		if (TryFindPickBranch(parentId, out _, out var branch, out _, out var pickSuffix) && pickSuffix.Length == 0) {
			if (branch.Trigger == null) { branch.Trigger = child; return parentId + ".0"; }
			if (branch.Action == null) { branch.Action = child; return parentId + ".1"; }
			return null;
		}
		if (TryFindSwitchBranch(parentId, out var @switch, out var existing, out var key, out var switchSuffix) && switchSuffix.Length == 0 && existing == null) {
			if (key is int caseKey) @switch.Cases[caseKey] = child;
			else @switch.Default = child;
			return parentId + ".0";
		}
		if (TryFindCatch(parentId, out _, out var @catch, out var catchIndex, out _) && parentId.EndsWith("$catch-" + catchIndex, StringComparison.Ordinal)
			&& @catch.GetType().GetProperty("Action")?.GetValue(@catch) is ActivityDelegate action && action.Handler == null) {
			action.Handler = child;
			return parentId + ".0";
		}
		var parent = Find(parentId);
		if (parent == null) return null;
		if (parent is Flowchart flowchart) {
			var step = new FlowStep { Action = child };
			var previous = flowchart.Nodes.OfType<FlowStep>().LastOrDefault();
			if (previous != null) previous.Next = step; else flowchart.StartNode = step;
			flowchart.Nodes.Add(step);
			return parentId.Length == 0 ? (flowchart.Nodes.OfType<FlowStep>().Count() - 1).ToString() : parentId + "." + (flowchart.Nodes.OfType<FlowStep>().Count() - 1);
		}
		var collectionProperty = FindActivityCollectionProperty(parent.GetType());
		if (collectionProperty != null) {
			var collection = collectionProperty.GetValue(parent);
			collectionProperty.PropertyType.GetMethod("Add", new[] { collectionProperty.PropertyType.GetGenericArguments()[0] })?.Invoke(collection, new object[] { child });
		} else {
			var slot = parent.GetType().GetProperties().FirstOrDefault(property => property.CanWrite && typeof(Activity).IsAssignableFrom(property.PropertyType) && property.GetValue(parent) == null);
			if (slot == null) return null;
			slot.SetValue(parent, child);
		}
		var index = WorkflowInspectionServices.GetActivities(parent).ToList().IndexOf(child);
		return index < 0 ? null : parentId.Length == 0 ? index.ToString() : parentId + "." + index;
	}

	static string FlowDecisionId(string flowchartPath, int index) => (flowchartPath.Length == 0 ? "" : flowchartPath + ".") + "$decision-" + index;
	static string CatchId(string tryCatchPath, int index) => (tryCatchPath.Length == 0 ? "" : tryCatchPath + ".") + "$catch-" + index;
	static string SwitchCaseId(string switchPath, int key) => (switchPath.Length == 0 ? "" : switchPath + ".") + "$case-" + key;
	static string SwitchDefaultId(string switchPath) => (switchPath.Length == 0 ? "" : switchPath + ".") + "$default";
	static string PickBranchId(string pickPath, int index) => (pickPath.Length == 0 ? "" : pickPath + ".") + "$pick-" + index;
	static string ForEachBodyId(string forEachPath) => (forEachPath.Length == 0 ? "" : forEachPath + ".") + "$foreach-body";
	static string ParallelForEachBodyId(string forEachPath) => (forEachPath.Length == 0 ? "" : forEachPath + ".") + "$parallelforeach-body";
	static string StateId(string stateMachinePath, int index) => (stateMachinePath.Length == 0 ? "" : stateMachinePath + ".") + "$state-" + index;

	bool TryFindTransition(string id, out StateMachine stateMachine, out Transition transition, out int index, out string suffix)
	{
		stateMachine = null!; transition = null!; index = -1; suffix = "";
		var marker = id.LastIndexOf(".$transition-", StringComparison.Ordinal);
		if (marker < 0) return false;
		var stateId = id[..marker];
		if (!TryFindState(stateId, out stateMachine, out var state, out _, out var stateSuffix) || stateSuffix.Length != 0) return false;
		var rest = id[(marker + ".$transition-".Length)..];
		var separator = rest.IndexOf('.');
		if (!int.TryParse(separator < 0 ? rest : rest[..separator], out index) || index < 0 || index >= state.Transitions.Count) return false;
		transition = state.Transitions[index]; suffix = separator < 0 ? "" : rest[separator..]; return true;
	}

	bool TryFindState(string id, out StateMachine owner, out State state, out int index, out string suffix)
	{
		owner = null!; state = null!; index = -1; suffix = "";
		var marker = id.LastIndexOf("$state-", StringComparison.Ordinal);
		if (marker < 0) return false;
		var rest = id[(marker + "$state-".Length)..];
		var separator = rest.IndexOf('.');
		if (!int.TryParse(separator < 0 ? rest : rest[..separator], out index)) return false;
		var ownerPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(ownerPath) is not StateMachine found || index < 0 || index >= found.States.Count) return false;
		owner = found; state = found.States[index]; suffix = separator < 0 ? "" : rest[separator..]; return true;
	}

	bool TryFindForEachBody(string id, out ForEach<int> owner, out string suffix)
	{
		owner = null!;
		suffix = "";
		var marker = id.LastIndexOf("$foreach-body", StringComparison.Ordinal);
		if (marker < 0) return false;
		var ownerPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(ownerPath) is not ForEach<int> found) return false;
		owner = found;
		suffix = id[(marker + "$foreach-body".Length)..];
		return true;
	}

	bool TryFindParallelForEachBody(string id, out ParallelForEach<int> owner, out string suffix)
	{
		owner = null!;
		suffix = "";
		var marker = id.LastIndexOf("$parallelforeach-body", StringComparison.Ordinal);
		if (marker < 0) return false;
		var ownerPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(ownerPath) is not ParallelForEach<int> found) return false;
		owner = found;
		suffix = id[(marker + "$parallelforeach-body".Length)..];
		return true;
	}

	bool TryFindPickBranch(string id, out Pick owner, out PickBranch branch, out int index, out string suffix)
	{
		owner = null!;
		branch = null!;
		index = -1;
		suffix = "";
		var marker = id.LastIndexOf("$pick-", StringComparison.Ordinal);
		if (marker < 0) return false;
		var rest = id[(marker + "$pick-".Length)..];
		var separator = rest.IndexOf('.');
		if (!int.TryParse(separator < 0 ? rest : rest[..separator], out index)) return false;
		var ownerPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(ownerPath) is not Pick found || index < 0 || index >= found.Branches.Count) return false;
		owner = found;
		branch = found.Branches[index];
		suffix = separator < 0 ? "" : rest[separator..];
		return true;
	}

	bool TryFindSwitchBranch(string id, out Switch<int> owner, out Activity? branch, out int? caseKey, out string suffix)
	{
		owner = null!;
		branch = null;
		caseKey = null;
		suffix = "";
		var caseMarker = id.LastIndexOf("$case-", StringComparison.Ordinal);
		var defaultMarker = id.LastIndexOf("$default", StringComparison.Ordinal);
		var marker = Math.Max(caseMarker, defaultMarker);
		if (marker < 0) return false;
		var ownerPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(ownerPath) is not Switch<int> found) return false;
		owner = found;
		if (marker == defaultMarker) {
			suffix = id[(marker + "$default".Length)..];
			branch = owner.Default;
			return true;
		}
		var rest = id[(marker + "$case-".Length)..];
		var separator = rest.IndexOf('.');
		if (!int.TryParse(separator < 0 ? rest : rest[..separator], out var key)) return false;
		caseKey = key;
		suffix = separator < 0 ? "" : rest[separator..];
		owner.Cases.TryGetValue(key, out branch);
		return true;
	}

	bool TryFindCatch(string id, out TryCatch tryCatch, out Catch @catch, out int index, out string tryCatchPath)
	{
		tryCatch = null!;
		@catch = null!;
		index = -1;
		tryCatchPath = "";
		var marker = id.LastIndexOf("$catch-", StringComparison.Ordinal);
		if (marker < 0) return false;
		var suffix = id[(marker + "$catch-".Length)..];
		var separator = suffix.IndexOf('.');
		if (!int.TryParse(separator < 0 ? suffix : suffix[..separator], out index)) return false;
		tryCatchPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(tryCatchPath) is not TryCatch found || index < 0 || index >= found.Catches.Count) return false;
		tryCatch = found;
		@catch = found.Catches[index];
		return true;
	}

	bool TryFindFlowDecision(string id, out FlowDecision decision, out string flowchartPath)
	{
		decision = null!;
		flowchartPath = "";
		var marker = id.LastIndexOf("$decision-", StringComparison.Ordinal);
		if (marker < 0 || !int.TryParse(id[(marker + "$decision-".Length)..], out var index)) return false;
		flowchartPath = marker == 0 ? "" : id[..marker].TrimEnd('.');
		if (Find(flowchartPath) is not Flowchart flowchart || index < 0 || index >= flowchart.Nodes.Count || flowchart.Nodes[index] is not FlowDecision found) return false;
		decision = found;
		return true;
	}

	// WorkflowInspectionServices validates the whole tree while calculating structural paths.
	// The standard conditional activities have a required Condition, so leaving it null makes a
	// freshly dropped toolbox item impossible to render or edit. Seed a conservative false value;
	// the user can replace it with the activity's normal condition expression afterwards.
	static void InitializeRequiredArguments(Activity activity)
	{
		switch (activity) {
			case If conditional:
				conditional.Condition = new InArgument<bool>(false);
				break;
			case While loop:
				loop.Condition = new System.Activities.Expressions.Literal<bool>(false);
				break;
			case DoWhile loop:
				loop.Condition = new System.Activities.Expressions.Literal<bool>(false);
				break;
			case Delay delay:
				delay.Duration = new InArgument<TimeSpan>(TimeSpan.Zero);
				break;
			case Switch<int> @switch:
				@switch.Expression = new InArgument<int>(0);
				break;
			case ForEach<int> forEach:
				forEach.Values = new InArgument<IEnumerable<int>>(new Microsoft.CSharp.Activities.CSharpValue<IEnumerable<int>>("new int[0]"));
				break;
			case ParallelForEach<int> parallelForEach:
				parallelForEach.Values = new InArgument<IEnumerable<int>>(new Microsoft.CSharp.Activities.CSharpValue<IEnumerable<int>>("new int[0]"));
				break;
		}
	}

	public bool Remove(string id)
	{
		if (id.Length == 0) return false;
		if (TryFindTransition(id, out _, out var transition, out var transitionIndex, out var transitionSuffix)) {
			var transitionStateId = id[..id.LastIndexOf(".$transition-", StringComparison.Ordinal)];
			if (!TryFindState(transitionStateId, out _, out var transitionState, out _, out _)) return false;
			if (transitionSuffix.Length == 0) { transitionState.Transitions.RemoveAt(transitionIndex); return true; }
			if (transitionSuffix == ".0" && transition.Trigger != null) { transition.Trigger = null; return true; }
			if (transitionSuffix == ".1" && transition.Action != null) { transition.Action = null; return true; }
		}
		if (TryFindState(id, out var stateMachine, out var state, out var stateIndex, out var stateSuffix)) {
			if (stateSuffix.Length == 0) {
				foreach (var candidate in stateMachine.States)
					foreach (var inboundTransition in candidate.Transitions.Where(candidateTransition => ReferenceEquals(candidateTransition.To, state))) inboundTransition.To = null;
				if (ReferenceEquals(stateMachine.InitialState, state)) stateMachine.InitialState = stateMachine.States.FirstOrDefault(candidate => !ReferenceEquals(candidate, state) && !candidate.IsFinal);
				return stateMachine.States.Remove(state);
			}
			if (stateSuffix == ".0" && state.Entry != null) { state.Entry = null; return true; }
			if (stateSuffix == ".1" && state.Exit != null) { state.Exit = null; return true; }
		}
		if (TryFindForEachBody(id, out var forEach, out var forEachSuffix) && forEachSuffix == ".0" && forEach.Body?.Handler != null) {
			forEach.Body.Handler = null;
			return true;
		}
		if (TryFindParallelForEachBody(id, out var parallelForEach, out var parallelForEachSuffix) && parallelForEachSuffix == ".0" && parallelForEach.Body?.Handler != null) {
			parallelForEach.Body.Handler = null;
			return true;
		}
		if (TryFindPickBranch(id, out var pickOwner, out var pickBranch, out var pickIndex, out var pickSuffix)) {
			if (pickSuffix.Length == 0) { pickOwner.Branches.RemoveAt(pickIndex); return true; }
			if (pickSuffix == ".0" && pickBranch.Trigger != null) { pickBranch.Trigger = null; return true; }
			if (pickSuffix == ".1" && pickBranch.Action != null) { pickBranch.Action = null; return true; }
		}
		if (TryFindSwitchBranch(id, out var switchOwner, out _, out var switchKey, out var switchSuffix)) {
			if (switchSuffix.Length == 0 || switchSuffix == ".0") {
				if (switchKey is int caseKey) return switchOwner.Cases.Remove(caseKey);
				if (switchOwner.Default == null) return false;
				switchOwner.Default = null;
				return true;
			}
		}
		if (TryFindCatch(id, out var catchOwner, out var catchNode, out var catchIndex, out _)) {
			var handlerSuffix = id[(id.LastIndexOf("$catch-", StringComparison.Ordinal) + "$catch-".Length)..];
			if (handlerSuffix == catchIndex.ToString()) {
				catchOwner.Catches.RemoveAt(catchIndex);
				return true;
			}
			if (handlerSuffix == catchIndex + ".0") {
				if (catchNode.GetType().GetProperty("Action")?.GetValue(catchNode) is not ActivityDelegate action || action.Handler == null) return false;
				action.Handler = null;
				return true;
			}
		}
		if (TryFindFlowDecision(id, out var decision, out var flowchartPath)) {
			if (Find(flowchartPath) is not Flowchart decisionFlowchart) return false;
			// A removed FlowNode must not remain as an incoming target. Unlike deleting a
			// FlowStep there is no unambiguous successor for a decision (it can branch), so
			// deliberately disconnect callers instead of silently selecting one branch.
			foreach (var step in decisionFlowchart.Nodes.OfType<FlowStep>().Where(step => ReferenceEquals(step.Next, decision))) step.Next = null;
			foreach (var caller in decisionFlowchart.Nodes.OfType<FlowDecision>()) {
				if (ReferenceEquals(caller.True, decision)) caller.True = null;
				if (ReferenceEquals(caller.False, decision)) caller.False = null;
			}
			if (ReferenceEquals(decisionFlowchart.StartNode, decision)) decisionFlowchart.StartNode = null;
			return decisionFlowchart.Nodes.Remove(decision);
		}
		var lastDot = id.LastIndexOf('.');
		var parentId = lastDot < 0 ? "" : id[..lastDot];
		var parent = Find(parentId);
		var target = Find(id);
		if (parent == null || target == null) return false;
		if (parent is Flowchart flowchart) {
			var step = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(node => ReferenceEquals(node.Action, target));
			if (step == null) return false;
			foreach (var node in flowchart.Nodes.OfType<FlowStep>().Where(node => ReferenceEquals(node.Next, step))) node.Next = step.Next;
			foreach (var branchOwner in flowchart.Nodes.OfType<FlowDecision>()) {
				if (ReferenceEquals(branchOwner.True, step)) branchOwner.True = step.Next;
				if (ReferenceEquals(branchOwner.False, step)) branchOwner.False = step.Next;
			}
			if (ReferenceEquals(flowchart.StartNode, step)) flowchart.StartNode = step.Next;
			return flowchart.Nodes.Remove(step);
		}
		var collectionProperty = FindActivityCollectionProperty(parent.GetType());
		if (collectionProperty != null) {
			var collection = collectionProperty.GetValue(parent);
			var removeMethod = collectionProperty.PropertyType.GetMethod("Remove", new[] { collectionProperty.PropertyType.GetGenericArguments()[0] });
			return removeMethod?.Invoke(collection, new object[] { target }) is true;
		}
		var slot = parent.GetType().GetProperties().FirstOrDefault(property => property.CanWrite && typeof(Activity).IsAssignableFrom(property.PropertyType) && ReferenceEquals(property.GetValue(parent), target));
		if (slot == null) return false;
		slot.SetValue(parent, null);
		return true;
	}

	/// <summary>Deletes a designer multi-selection as one tree operation. Descendants of an
	/// already selected ancestor are intentionally ignored, and a rejected later deletion restores
	/// the complete original XAML so a command can never leave a half-deleted workflow.</summary>
	public bool RemoveMany(IEnumerable<string> ids)
	{
		var requested = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
		var roots = requested.Where(id => !requested.Any(parent => parent != id && id.StartsWith(parent + ".", StringComparison.Ordinal))).ToArray();
		if (roots.Length == 0) return false;
		var before = ToXaml();
		foreach (var id in roots) {
			if (Remove(id)) continue;
			Reset(before);
			return false;
		}
		return true;
	}

	/// <summary>Reconnects two FlowStep actions selected in the tree. The host deliberately
	/// rejects a back edge that would make the simple FlowStep chain cyclic; decision nodes will
	/// own explicit loop/branch semantics when the graph protocol grows beyond activity paths.</summary>
	public bool ConnectFlowSteps(string sourceId, string targetId)
	{
		if (sourceId == targetId) return false;
		var source = Find(sourceId);
		var target = Find(targetId);
		if (source == null || target == null) return false;
		foreach (var flowchart in Flowcharts(Root)) {
			var from = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(step => ReferenceEquals(step.Action, source));
			var to = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(step => ReferenceEquals(step.Action, target));
			if (from == null || to == null) continue;
			for (var cursor = to; cursor != null; cursor = cursor.Next as FlowStep)
				if (ReferenceEquals(cursor, from)) return false;
			from.Next = to;
			return true;
		}
		return false;
	}

	public bool ConnectFlowDecision(string sourceId, string targetId, bool trueBranch)
	{
		if (!TryFindFlowDecision(sourceId, out var decision, out var flowchartPath)) return false;
		if (Find(flowchartPath) is not Flowchart flowchart || Find(targetId) is not { } target) return false;
		var step = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(candidate => ReferenceEquals(candidate.Action, target));
		if (step == null) return false;
		if (trueBranch) decision.True = step;
		else decision.False = step;
		return true;
	}

	/// <summary>Marks a selected FlowStep or FlowDecision as the Flowchart entry node.
	/// This is intentionally explicit: insertion order is only a convenience default and
	/// must not prevent editing a graph whose execution starts in the middle.</summary>
	public bool SetFlowStart(string elementId)
	{
		if (TryFindFlowDecision(elementId, out var decision, out var flowchartPath) && Find(flowchartPath) is Flowchart decisionFlowchart) {
			decisionFlowchart.StartNode = decision;
			return true;
		}
		var activity = Find(elementId);
		if (activity == null) return false;
		foreach (var flowchart in Flowcharts(Root)) {
			var step = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(candidate => ReferenceEquals(candidate.Action, activity));
			if (step == null) continue;
			flowchart.StartNode = step;
			return true;
		}
		return false;
	}

	/// <summary>Clears an explicitly selected Flowchart edge without deleting either endpoint.</summary>
	public bool DisconnectFlow(string sourceId, string branch)
	{
		if (TryFindFlowDecision(sourceId, out var decision, out _)) {
			if (string.Equals(branch, "True", StringComparison.OrdinalIgnoreCase)) { decision.True = null; return true; }
			if (string.Equals(branch, "False", StringComparison.OrdinalIgnoreCase)) { decision.False = null; return true; }
			return false;
		}
		var source = Find(sourceId);
		if (source == null || !string.Equals(branch, "Next", StringComparison.OrdinalIgnoreCase)) return false;
		foreach (var flowchart in Flowcharts(Root)) {
			var step = flowchart.Nodes.OfType<FlowStep>().FirstOrDefault(candidate => ReferenceEquals(candidate.Action, source));
			if (step == null) continue;
			step.Next = null;
			return true;
		}
		return false;
	}

	static IEnumerable<Flowchart> Flowcharts(Activity? activity)
	{
		if (activity == null) yield break;
		if (activity is Flowchart flowchart) yield return flowchart;
		foreach (var child in WorkflowInspectionServices.GetActivities(activity))
			foreach (var nested in Flowcharts(child)) yield return nested;
	}

	/// <summary>Moves an Activity one place within its owning collection. This intentionally
	/// excludes synthetic State/Transition/Flowchart nodes: their ordering or graph edges carry
	/// additional semantics and must use their dedicated editors instead.</summary>
	public string? MoveSibling(string id, int offset)
	{
		if (offset is not (-1 or 1)) return null;
		var separator = id.LastIndexOf('.');
		var parentId = separator < 0 ? "" : id[..separator];
		var parent = Find(parentId);
		var target = Find(id);
		if (parent == null || target == null) return null;
		var collectionProperty = FindActivityCollectionProperty(parent.GetType());
		if (collectionProperty?.GetValue(parent) is not System.Collections.IList collection) return null;
		var index = collection.IndexOf(target);
		var destination = index + offset;
		if (index < 0 || destination < 0 || destination >= collection.Count) return null;
		collection.RemoveAt(index);
		collection.Insert(destination, target);
		return parentId.Length == 0 ? destination.ToString() : parentId + "." + destination;
	}

	static PropertyInfo? FindActivityCollectionProperty(Type activityType) =>
		activityType.GetProperties()
			.FirstOrDefault(p => p.PropertyType.IsGenericType
				&& p.PropertyType.GetGenericTypeDefinition() == typeof(Collection<>)
				&& typeof(Activity).IsAssignableFrom(p.PropertyType.GetGenericArguments()[0]));

	static Type? ResolveActivityType(string typeName)
	{
		if (string.Equals(typeName, "SwitchInt32", StringComparison.Ordinal)) return typeof(Switch<int>);
		if (string.Equals(typeName, "ForEachInt32", StringComparison.Ordinal)) return typeof(ForEach<int>);
		if (string.Equals(typeName, "ParallelForEachInt32", StringComparison.Ordinal)) return typeof(ParallelForEach<int>);
		if (typeName.Contains('.')) return Type.GetType(typeName) ?? typeof(Sequence).Assembly.GetType(typeName);
		return typeof(Sequence).Assembly.GetType("System.Activities.Statements." + typeName);
	}

	/// <summary>Reads/writes the "designable" properties of an activity for the Properties pad:
	/// simple CLR values directly, and <c>InArgument{T}</c>/<c>OutArgument{T}</c>/
	/// <c>InOutArgument{T}</c> whose current expression is a plain <c>Literal{T}</c> by their
	/// literal value. A root-scope variable reference is represented as <c>@variableName</c> and
	/// a CoreWF C# value expression as <c>=expression</c>; both remain editable. Other expression
	/// forms stay read-only rather than being silently discarded on save.</summary>
	public IEnumerable<(string Name, string Value, string TypeName, bool IsReadOnly)> GetProperties(Activity activity)
	{
		foreach (var property in activity.GetType().GetProperties()) {
			if (property.DeclaringType == typeof(Activity) || property.DeclaringType == typeof(object)) continue;
			if (!property.CanRead) continue;
			var activityResultType = GetActivityResultType(property.PropertyType);
			if (activityResultType != null) {
				var (value, isEditable) = ReadActivityValue(property.GetValue(activity), activityResultType);
				yield return (property.Name, value, activityResultType.Name, !isEditable || !property.CanWrite);
				continue;
			}
			if (typeof(Activity).IsAssignableFrom(property.PropertyType)) continue;
			if (IsActivityCollection(property.PropertyType)) continue;
			var argumentElementType = GetArgumentElementType(property.PropertyType);
			if (argumentElementType != null) {
				var (value, isEditable) = ReadArgumentValue(property.GetValue(activity), argumentElementType);
				yield return (property.Name, value, argumentElementType.Name, !isEditable);
				continue;
			}
			if (!IsSimpleType(property.PropertyType)) continue;
			var raw = property.GetValue(activity);
			yield return (property.Name, raw?.ToString() ?? "", property.PropertyType.Name, !property.CanWrite);
		}
	}

	public IEnumerable<(string Name, string Value, string TypeName, bool IsReadOnly)> GetProperties(FlowDecision decision)
	{
		var type = GetActivityResultType(decision.GetType().GetProperty(nameof(FlowDecision.Condition))!.PropertyType)!;
		var (value, editable) = ReadActivityValue(decision.Condition, type);
		yield return ("Condition", value, type.Name, !editable);
	}

	public (string Value, bool IsEditable) GetBooleanExpressionValue(Activity<bool>? expression) => ReadActivityValue(expression, typeof(bool));

	static bool IsActivityCollection(Type type) =>
		type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Collection<>)
			&& typeof(Activity).IsAssignableFrom(type.GetGenericArguments()[0]);

	static bool IsSimpleType(Type type) =>
		type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
			|| type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid);

	static Type? GetArgumentElementType(Type type)
	{
		if (!type.IsGenericType) return null;
		var definition = type.GetGenericTypeDefinition();
		if (definition == typeof(InArgument<>) || definition == typeof(OutArgument<>) || definition == typeof(InOutArgument<>))
			return type.GetGenericArguments()[0];
		return null;
	}

	static Type? GetActivityResultType(Type type) =>
		type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Activity<>) ? type.GetGenericArguments()[0] : null;

	static (string Value, bool IsEditable) ReadActivityValue(object? expression, Type resultType)
	{
		if (expression == null) return ("", true);
		var literalType = typeof(System.Activities.Expressions.Literal<>).MakeGenericType(resultType);
		if (literalType.IsInstanceOfType(expression))
			return (GetDeclaredPropertyValue(expression, "Value")?.ToString() ?? "", true);
		if (TryReadCodeExpression(expression, out var text)) return (text, true);
		return ("<expression>", false);
	}

	static (string Value, bool IsEditable) ReadArgumentValue(object? argument, Type elementType)
	{
		if (argument == null) return ("", true);
		var expressionProperty = argument.GetType().GetProperty("Expression", DeclaredPublicInstance);
		var expression = expressionProperty?.GetValue(argument);
		if (expression == null) return ("", true);
		var literalType = typeof(System.Activities.Expressions.Literal<>).MakeGenericType(elementType);
		if (literalType.IsInstanceOfType(expression)) {
			var valueProperty = literalType.GetProperty("Value", DeclaredPublicInstance);
			return (valueProperty?.GetValue(expression)?.ToString() ?? "", true);
		}
		var variable = expression.GetType().GetProperty("Variable", DeclaredPublicInstance)?.GetValue(expression) as Variable;
		if (variable != null) return ("@" + variable.Name, true);
		if (TryReadCodeExpression(expression, out var text)) return (text, true);
		return ("<expression>", false);
	}

	static bool TryCreateCodeExpression(Type resultType, string value, out object? expression)
	{
		expression = null;
		var isVisualBasic = value.StartsWith("=vb:", StringComparison.OrdinalIgnoreCase);
		if (!isVisualBasic && !value.StartsWith("=", StringComparison.Ordinal)) return false;
		var text = value[(isVisualBasic ? 4 : 1)..];
		if (string.IsNullOrWhiteSpace(text)) return false;
		try {
			var generic = (isVisualBasic ? typeof(Microsoft.VisualBasic.Activities.VisualBasicValue<>) : typeof(Microsoft.CSharp.Activities.CSharpValue<>)).MakeGenericType(resultType);
			expression = Activator.CreateInstance(generic, text);
			return expression != null;
		} catch { return false; }
	}

	static bool TryReadCodeExpression(object? expression, out string text)
	{
		text = "";
		if (expression?.GetType().IsGenericType != true) return false;
		var generic = expression.GetType().GetGenericTypeDefinition();
		if (generic != typeof(Microsoft.CSharp.Activities.CSharpValue<>) && generic != typeof(Microsoft.VisualBasic.Activities.VisualBasicValue<>)) return false;
		text = (generic == typeof(Microsoft.VisualBasic.Activities.VisualBasicValue<>) ? "=vb:" : "=") + (GetDeclaredPropertyValue(expression, "ExpressionText") as string ?? "");
		return true;
	}

	object? ConvertToPropertyType(Type propertyType, string value, string scopeId)
	{
		var activityResultType = GetActivityResultType(propertyType);
		if (activityResultType != null) {
			if (TryCreateCodeExpression(activityResultType, value, out var expression)) return expression;
			if (value.StartsWith("=", StringComparison.Ordinal)) return null;
			if (value.StartsWith("@", StringComparison.Ordinal)) {
				var variable = FindVariable(scopeId, value[1..], activityResultType);
				if (variable == null) return null;
				var referenceType = typeof(System.Activities.Expressions.VariableReference<>).MakeGenericType(activityResultType);
				var constructor = referenceType.GetConstructors().FirstOrDefault(candidate => candidate.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variable.GetType()));
				if (constructor != null) return constructor.Invoke(new object[] { variable });
				var variableProperty = referenceType.GetProperty("Variable", BindingFlags.Public | BindingFlags.Instance);
				if (variableProperty?.CanWrite == true) { var reference = Activator.CreateInstance(referenceType); variableProperty.SetValue(reference, variable); return reference; }
				return null;
			}
			var literal = Activator.CreateInstance(typeof(System.Activities.Expressions.Literal<>).MakeGenericType(activityResultType));
			if (literal == null) return null;
			var valueProperty = literal.GetType().GetProperty("Value", DeclaredPublicInstance);
			if (valueProperty == null) return null;
			valueProperty.SetValue(literal, ConvertSimple(activityResultType, value));
			return literal;
		}
		var argumentElementType = GetArgumentElementType(propertyType);
		if (argumentElementType != null) {
			if (TryCreateCodeExpression(argumentElementType, value, out var expression)) {
				if (propertyType.GetGenericTypeDefinition() != typeof(InArgument<>)) return null;
				var argument = Activator.CreateInstance(propertyType);
				var expressionProperty = FindDeclaredProperty(propertyType, "Expression");
				if (argument == null || expression == null || expressionProperty == null) return null;
				expressionProperty.SetValue(argument, expression);
				return argument;
			}
			if (value.StartsWith("=", StringComparison.Ordinal)) return null;
			if (value.StartsWith("@", StringComparison.Ordinal)) {
				var variable = FindVariable(scopeId, value[1..], argumentElementType);
				if (variable != null) {
					try { return Activator.CreateInstance(propertyType, new object[] { variable }); } catch { }
				}
				var fromVariable = propertyType.GetMethod("FromVariable", BindingFlags.Public | BindingFlags.Static);
				if (variable != null && fromVariable != null) return fromVariable.Invoke(null, new object[] { variable });
				var variableConstructor = variable == null ? null : propertyType.GetConstructors().FirstOrDefault(constructor => constructor.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variable.GetType()));
				if (variable != null && variableConstructor != null) return variableConstructor.Invoke(new object[] { variable });
				return null;
			}
			var converted = ConvertSimple(argumentElementType, value);
			var constructor = propertyType.GetConstructor(new[] { argumentElementType });
			return constructor?.Invoke(new[] { converted });
		}
		return ConvertSimple(propertyType, value);
	}

	Variable? FindVariable(string scopeId, string name, Type type)
	{
		if (Root == null || string.IsNullOrWhiteSpace(name)) return null;
		for (var current = scopeId; ; current = ParentPath(current)) {
			if (TryFindState(current, out _, out var state, out _, out _)) {
				var stateVariable = state.Variables.FirstOrDefault(candidate => candidate.Name == name && candidate.Type == type);
				if (stateVariable != null) return stateVariable;
			}
			var activity = Find(current);
			var variables = activity?.GetType().GetProperty("Variables")?.GetValue(activity) as System.Collections.IEnumerable;
			var variable = variables?.Cast<Variable>().FirstOrDefault(candidate => candidate.Name == name && candidate.Type == type);
			if (variable != null) return variable;
			if (current.Length == 0) return null;
		}
	}

	static string ParentPath(string path)
	{
		var lastDot = path.LastIndexOf('.');
		return lastDot < 0 ? "" : path[..lastDot];
	}

	static object? ConvertSimple(Type type, string value)
	{
		if (type == typeof(string)) return value;
		if (type == typeof(TimeSpan)) {
			try { return TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
			catch { return null; }
		}
		if (type == typeof(DateTime)) {
			try { return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
			catch { return null; }
		}
		if (type == typeof(Guid)) {
			try { return Guid.Parse(value); }
			catch { return null; }
		}
		if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);
		try { return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture); }
		catch { return null; }
	}
}
