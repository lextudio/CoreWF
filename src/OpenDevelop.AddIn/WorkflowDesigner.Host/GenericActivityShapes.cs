using System.Activities;
using System.Activities.Statements;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace ICSharpCode.WorkflowDesigner.Host;

/// <summary>Reflection views over the generic CoreWF statements (<c>ForEach{T}</c>,
/// <c>ParallelForEach{T}</c>, <c>Switch{T}</c>). The designer must edit these for any closed
/// <c>T</c> a document happens to contain, not just the <c>int</c> specialization the toolbox
/// used to offer, so every access to their delegate/case surface goes through these shapes
/// instead of a <c>Switch&lt;int&gt;</c>-style pattern match.</summary>
static class GenericActivityShapes
{
	/// <summary>Returns the closed type argument when <paramref name="value"/> is an instance of
	/// <paramref name="openDefinition"/> (e.g. <c>typeof(ForEach&lt;&gt;)</c>), otherwise null.</summary>
	public static Type? TypeArgument(object? value, Type openDefinition)
	{
		for (var type = value?.GetType(); type != null; type = type.BaseType)
			if (type.IsGenericType && type.GetGenericTypeDefinition() == openDefinition)
				return type.GetGenericArguments()[0];
		return null;
	}

	public static bool IsClosedGenericOf(object? value, Type openDefinition) => TypeArgument(value, openDefinition) != null;

	/// <summary>Parses a case key or literal written by the user into <paramref name="keyType"/>.
	/// Switch keys are part of the document, so a value that cannot be converted must fail the
	/// edit rather than silently landing in the workflow as a default.</summary>
	public static bool TryParseValue(Type keyType, string text, out object? value)
	{
		value = null;
		var target = Nullable.GetUnderlyingType(keyType) ?? keyType;
		try {
			if (target.IsEnum) { value = Enum.Parse(target, text, ignoreCase: true); return true; }
			if (target == typeof(string)) { value = text; return true; }
			var converter = TypeDescriptor.GetConverter(target);
			if (converter.CanConvertFrom(typeof(string))) { value = converter.ConvertFromInvariantString(text); return value != null; }
			value = Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
			return value != null;
		} catch { return false; }
	}

	public static string FormatValue(object? value) =>
		value switch {
			null => "",
			string text => text,
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString() ?? ""
		};

	/// <summary>Editable view of a <c>ForEach{T}</c>/<c>ParallelForEach{T}</c> body delegate.</summary>
	public sealed class ForEachShape
	{
		readonly PropertyInfo bodyProperty;

		ForEachShape(Activity owner, Type itemType, bool isParallel)
		{
			Owner = owner;
			ItemType = itemType;
			IsParallel = isParallel;
			bodyProperty = owner.GetType().GetProperty("Body")!;
		}

		public Activity Owner { get; }
		public Type ItemType { get; }
		public bool IsParallel { get; }

		public static ForEachShape? For(object? activity)
		{
			if (activity is not Activity owner) return null;
			if (TypeArgument(owner, typeof(ForEach<>)) is { } itemType) return new ForEachShape(owner, itemType, isParallel: false);
			if (TypeArgument(owner, typeof(ParallelForEach<>)) is { } parallelItemType) return new ForEachShape(owner, parallelItemType, isParallel: true);
			return null;
		}

		public static ForEachShape? For(object? activity, bool parallel)
		{
			var shape = For(activity);
			return shape != null && shape.IsParallel == parallel ? shape : null;
		}

		public ActivityDelegate? Body => bodyProperty.GetValue(Owner) as ActivityDelegate;

		public Activity? Handler
		{
			get => Body?.Handler;
			set { if (value != null) EnsureBody(); if (Body is { } body) body.Handler = value; }
		}

		/// <summary>The activity backing the loop's <c>Values</c> argument, which the tree
		/// projection must not also render as an ordinary child.</summary>
		public Activity? ValuesExpression => (Owner.GetType().GetProperty("Values")?.GetValue(Owner) as Argument)?.Expression;

		public DelegateArgument? Argument => Body?.GetType().GetProperty("Argument")?.GetValue(Body) as DelegateArgument;

		/// <summary>Creates the <c>ActivityAction{T}</c> body (with its iteration argument) when the
		/// activity does not have one yet, so an activity dropped into the body slot binds to a
		/// usable <c>item</c> variable of the loop's own element type.</summary>
		public ActivityDelegate EnsureBody(string argumentName = "item")
		{
			if (Body is { } existing) {
				if (existing.GetType().GetProperty("Argument") is { } slot && slot.GetValue(existing) == null)
					slot.SetValue(existing, NewDelegateArgument(argumentName));
				return existing;
			}
			var body = (ActivityDelegate)Activator.CreateInstance(typeof(ActivityAction<>).MakeGenericType(ItemType))!;
			body.GetType().GetProperty("Argument")!.SetValue(body, NewDelegateArgument(argumentName));
			bodyProperty.SetValue(Owner, body);
			return body;
		}

		public void SetArgumentName(string name)
		{
			var body = EnsureBody(name);
			if (body.GetType().GetProperty("Argument")?.GetValue(body) is DelegateArgument argument) argument.Name = name;
		}

		DelegateArgument NewDelegateArgument(string name)
		{
			var argument = (DelegateArgument)Activator.CreateInstance(typeof(DelegateInArgument<>).MakeGenericType(ItemType))!;
			argument.Name = name;
			return argument;
		}
	}

	/// <summary>Editable view of a <c>Switch{T}</c>'s cases and default branch. Cases are addressed
	/// by ordinal position rather than by key text: a key of an arbitrary <c>T</c> cannot be
	/// embedded in the designer's dotted element-path ids safely.</summary>
	public sealed class SwitchShape
	{
		readonly PropertyInfo defaultProperty;
		readonly object cases;
		readonly MethodInfo containsKey, remove, clear, getItem, setItem;
		readonly PropertyInfo keysProperty;

		SwitchShape(Activity owner, Type keyType)
		{
			Owner = owner;
			KeyType = keyType;
			cases = owner.GetType().GetProperty("Cases")!.GetValue(owner)!;
			// Switch<T>.Cases is a CoreWF NullableKeyDictionary, which implements only the generic
			// IDictionary<T, Activity>, so the case surface has to be reached through that
			// interface's members rather than the non-generic IDictionary.
			var dictionary = typeof(IDictionary<,>).MakeGenericType(keyType, typeof(Activity));
			containsKey = dictionary.GetMethod("ContainsKey")!;
			remove = dictionary.GetMethod("Remove", new[] { keyType })!;
			getItem = dictionary.GetMethod("get_Item")!;
			setItem = dictionary.GetMethod("set_Item")!;
			keysProperty = dictionary.GetProperty("Keys")!;
			clear = typeof(ICollection<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(keyType, typeof(Activity))).GetMethod("Clear")!;
			defaultProperty = owner.GetType().GetProperty("Default")!;
		}

		public Activity Owner { get; }
		public Type KeyType { get; }

		public static SwitchShape? For(object? activity) =>
			activity is Activity owner && TypeArgument(owner, typeof(Switch<>)) is { } keyType ? new SwitchShape(owner, keyType) : null;

		public Activity? Default
		{
			get => defaultProperty.GetValue(Owner) as Activity;
			set => defaultProperty.SetValue(Owner, value);
		}

		/// <summary>Case keys in a stable enumeration order, which is what the designer's
		/// <c>$case-{index}</c> element ids refer to.</summary>
		public List<object?> Keys => ((IEnumerable)keysProperty.GetValue(cases)!).Cast<object?>().ToList();

		public int Count => Keys.Count;

		public IEnumerable<Activity?> Branches => Keys.Select(Get);

		public bool ContainsKey(object? key) => (bool)containsKey.Invoke(cases, new[] { key })!;

		Activity? Get(object? key) => getItem.Invoke(cases, new[] { key }) as Activity;

		void Set(object? key, Activity? activity) => setItem.Invoke(cases, new[] { key, activity });

		public Activity? ActivityAt(int index)
		{
			var keys = Keys;
			return index >= 0 && index < keys.Count ? Get(keys[index]) : null;
		}

		public bool TryGetKey(int index, out object? key)
		{
			var keys = Keys;
			if (index < 0 || index >= keys.Count) { key = null; return false; }
			key = keys[index];
			return true;
		}

		public bool SetActivityAt(int index, Activity? activity)
		{
			if (!TryGetKey(index, out var key)) return false;
			Set(key, activity);
			return true;
		}

		public bool RemoveAt(int index)
		{
			if (!TryGetKey(index, out var key)) return false;
			return (bool)remove.Invoke(cases, new[] { key })!;
		}

		/// <summary>Rekeys an existing case, refusing a duplicate key so the document never reaches
		/// a state <c>Switch{T}</c> itself would reject at load time.</summary>
		public bool TryRekey(int index, string keyText, out string error)
		{
			error = "";
			if (!TryGetKey(index, out var oldKey)) { error = "The switch case no longer exists."; return false; }
			if (!TryParseValue(KeyType, keyText, out var newKey) || newKey == null) { error = "'" + keyText + "' is not a valid " + KeyType.Name + " value."; return false; }
			if (Equals(oldKey, newKey)) return true;
			if (ContainsKey(newKey)) { error = "The switch already has a case for '" + keyText + "'."; return false; }
			// Preserve enumeration order so the designer's ordinal case ids stay stable across a
			// rename: rebuild the dictionary with the replacement key in the original position.
			var entries = Keys.Select(key => (Key: key, Value: Get(key))).ToList();
			clear.Invoke(cases, Array.Empty<object>());
			foreach (var entry in entries) Set(Equals(entry.Key, oldKey) ? newKey : entry.Key, entry.Value);
			return true;
		}

		/// <summary>Adds a case with the first unused key that can be generated for the key type,
		/// mirroring how a new case appears in the designer before the user names it.</summary>
		public int AddCase(Activity? activity)
		{
			var key = NextFreeKey();
			if (key == null) return -1;
			Set(key, activity);
			return Keys.FindIndex(existing => Equals(existing, key));
		}

		object? NextFreeKey()
		{
			var target = Nullable.GetUnderlyingType(KeyType) ?? KeyType;
			if (target == typeof(string)) {
				for (var index = 1; index < 1000; index++) {
					var candidate = "Case " + index;
					if (!ContainsKey(candidate)) return candidate;
				}
				return null;
			}
			if (target.IsEnum) return Enum.GetValues(target).Cast<object>().FirstOrDefault(value => !ContainsKey(value));
			for (var index = 0; index < 1000; index++) {
				if (!TryParseValue(target, index.ToString(CultureInfo.InvariantCulture), out var candidate) || candidate == null) break;
				if (!ContainsKey(candidate)) return candidate;
			}
			// A key type with no generatable sequence (e.g. a custom struct) still gets one case,
			// which the user can then rekey through the properties pad.
			return Count == 0 ? Activator.CreateInstance(target) : null;
		}
	}
}
