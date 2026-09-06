using System.Activities;
using System.Activities.Statements;
using System.Activities.XamlIntegration;
using System.Threading.Tasks;
using System.Xaml;
using System.Xml;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.WorkflowDesigner.Host;

public sealed class WorkflowDocumentTests
{
	[Fact]
	public void Arguments_and_variables_round_trip()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddArgument("message", "String", "hello"));
		Assert.True(document.AddVariable("condition", "Boolean", ""));

		var reloaded = new WorkflowDocument();
		var xaml = document.ToXaml();
		reloaded.Reset(xaml);
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error + Environment.NewLine + xaml);
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "message" && argument.DefaultValue == "hello");
		Assert.Contains(reloaded.GetVariables(), variable => variable.Name == "condition" && variable.DefaultValue == "");
	}

	[Fact]
	public void Time_span_arguments_and_variables_preserve_their_type_and_literal_defaults()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddArgument("timeout", "TimeSpan", "00:00:01"));
		Assert.True(document.AddVariable("interval", "TimeSpan", "00:00:00.250"));
		Assert.True(document.AddArgument("id", "Guid", "01234567-89ab-cdef-0123-456789abcdef"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "timeout" && argument.TypeName == "TimeSpan" && argument.DefaultValue == "00:00:01");
		Assert.Contains(reloaded.GetVariables(), variable => variable.Name == "interval" && variable.TypeName == "TimeSpan" && variable.DefaultValue == "00:00:00.2500000");
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "id" && argument.TypeName == "Guid" && argument.DefaultValue == "01234567-89ab-cdef-0123-456789abcdef");
	}

	[Fact]
	public void Arguments_preserve_direction_and_reject_defaults_for_output_directions()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddArgument("input", "String", "hello", "In"));
		Assert.True(document.AddArgument("output", "Int32", "", "Out"));
		Assert.True(document.AddArgument("state", "Boolean", "", "InOut"));
		Assert.False(document.AddArgument("invalid", "String", "value", "Out"));
		Assert.Contains(document.GetArguments(), argument => argument.Name == "input" && argument.Direction == "In" && argument.DefaultValue == "hello");
		Assert.Contains(document.GetArguments(), argument => argument.Name == "output" && argument.Direction == "Out" && argument.TypeName == "Int32");
		Assert.Contains(document.GetArguments(), argument => argument.Name == "state" && argument.Direction == "InOut" && argument.TypeName == "Boolean");

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "output" && argument.Direction == "Out");
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "state" && argument.Direction == "InOut");
	}

	[Fact]
	public void Input_argument_csharp_default_round_trips()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddArgument("count", "Int32", "=1 + 2", "In"));
		Assert.Contains(document.GetArguments(), argument => argument.Name == "count" && argument.DefaultValue == "=1 + 2");

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "count" && argument.DefaultValue == "=1 + 2");
	}

	[Fact]
	public void Visual_basic_expressions_round_trip_for_arguments_and_activity_values()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddArgument("count", "Int32", "=vb:1 + 2", "In"));
		Assert.Equal("0", document.AddChild("", "If"));
		Assert.True(document.SetProperty("0", "Condition", "=vb:1 < 2"));
		Assert.Contains(document.GetArguments(), argument => argument.Name == "count" && argument.DefaultValue == "=vb:1 + 2");
		Assert.Contains(document.GetProperties(document.Find("0")!), property => property.Name == "Condition" && property.Value == "=vb:1 < 2");

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains(reloaded.GetArguments(), argument => argument.Name == "count" && argument.DefaultValue == "=vb:1 + 2");
		Assert.Contains(reloaded.GetProperties(reloaded.Find("0")!), property => property.Name == "Condition" && property.Value == "=vb:1 < 2");
	}

	[Theory]
	[InlineData("=1 + 2")]
	[InlineData("=vb:1 + 2")]
	public void Variable_code_expression_defaults_round_trip(string expression)
	{
		var document = Load(new Sequence());
		Assert.True(document.AddVariable("count", "Int32", expression));
		Assert.Contains(document.GetVariables(), variable => variable.Name == "count" && variable.DefaultValue == expression);

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains(reloaded.GetVariables(), variable => variable.Name == "count" && variable.DefaultValue == expression);
	}

	[Fact]
	public void Property_kinds_describe_the_value_type_behind_an_activity_argument()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence { Activities = { new Delay { Duration = new InArgument<TimeSpan>(TimeSpan.Zero) }, new While { Condition = new System.Activities.Expressions.Literal<bool>(true) } } }).ToXaml() } }
		});
		var properties = FlattenNodes(state.Tree!).SelectMany(node => node.Properties).ToList();
		// Duration is an InArgument<TimeSpan> and Condition an InArgument<Boolean>: the pad has to
		// see the value type behind the wrapper, or every argument looks like a plain string.
		Assert.Equal("TimeSpan", Assert.Single(properties.Where(property => property.Name == "Duration")).TypeName);
		var condition = Assert.Single(properties.Where(property => property.Name == "Condition"));
		Assert.Equal("Boolean", condition.Kind);
		Assert.Equal("True", condition.Value);
	}

	[Fact]
	public void An_enum_argument_offers_its_values_to_the_properties_pad()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence { Activities = { new TryCatch() } }).ToXaml() } }
		});
		var properties = FlattenNodes(state.Tree!).SelectMany(node => node.Properties).ToList();
		Assert.All(properties.Where(property => property.Kind == "Enum"), property => Assert.NotEmpty(property.AllowedValues));
		Assert.All(properties.Where(property => property.Kind == "Number"), property => Assert.NotEqual("Enum", property.Kind));
	}

	[Fact]
	public void The_toolbox_offers_open_generic_activities_with_their_arity()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});
		var toolbox = service.GetCapabilities("session", "document").Toolbox;
		var forEach = Assert.Single(toolbox.Where(item => item.Name == "ForEach"));
		Assert.EndsWith("<T>", forEach.DisplayName, StringComparison.Ordinal);
		Assert.Contains('`', forEach.TypeName);
		Assert.DoesNotContain("ForEachInt32", toolbox.Select(item => item.Name));
		// The structural child shapes a container owns are still offered even though they are not
		// activity types of their own.
		Assert.Single(toolbox.Where(item => item.Name == "SwitchCase"));
	}

	[Fact]
	public void Expressions_are_validated_against_the_workflows_own_imports()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});
		// System is one of CoreWF's default imports, so an unqualified DateTime resolves the way
		// it will at run time.
		Assert.True(service.ValidateExpression("session", "document", "=DateTime.Now").IsValid);
		Assert.Contains("System", service.GetImports("session", "document"));

		// A namespace that is not imported must not resolve, or validation would accept
		// expressions the runtime rejects.
		Assert.False(service.ValidateExpression("session", "document", "=StringBuilder.Equals(1, 1)").IsValid);
		state = service.AddImport("session", "document", state.Version, "System.Text");
		Assert.Contains("System.Text", service.GetImports("session", "document"));
		Assert.True(service.ValidateExpression("session", "document", "=StringBuilder.Equals(1, 1)").IsValid);

		service.RemoveImport("session", "document", state.Version, "System.Text");
		Assert.DoesNotContain("System.Text", service.GetImports("session", "document"));
	}

	[Fact]
	public void An_added_import_survives_the_xaml_round_trip()
	{
		var document = Load(new Sequence());
		Assert.True(document.AddImport("System.Text"));
		Assert.False(document.AddImport("System.Text"));
		Assert.False(document.AddImport("not a namespace"));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Contains("System.Text", reloaded.GetImports());
		// The defaults are materialized on first edit, so removing one actually removes it.
		Assert.Contains("System", reloaded.GetImports());
		Assert.True(reloaded.RemoveImport("System"));
		Assert.DoesNotContain("System", reloaded.GetImports());
	}

	[Fact]
	public void An_unreadable_project_assembly_does_not_take_the_toolbox_down_with_it()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var notAnAssembly = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dll");
		File.WriteAllText(notAnAssembly, "this is not an assembly");
		try {
			service.Open(new DesignerDocumentSnapshot {
				SessionId = "session", DocumentId = "document", Version = 1, ProjectCodeMode = "Enabled",
				ProjectAssemblyPath = notAnAssembly,
				Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
			});
			var toolbox = service.GetCapabilities("session", "document").Toolbox;
			Assert.Single(toolbox.Where(item => item.Name == "Sequence"));
			Assert.Empty(toolbox.Where(item => item.Category == "Project"));
		} finally { File.Delete(notAnAssembly); }
	}

	static IEnumerable<DesignerElementNode> FlattenNodes(DesignerElementNode node) =>
		new[] { node }.Concat(node.Children.SelectMany(FlattenNodes));

	[Fact]
	public void Expression_validation_reports_csharp_and_visual_basic_syntax_errors()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});
		Assert.True(service.ValidateExpression("session", "document", "=1 + 2").IsValid);
		var csharpError = service.ValidateExpression("session", "document", "=1 +");
		Assert.False(csharpError.IsValid);
		Assert.Contains(csharpError.Details, detail => detail.Code.StartsWith("CS", StringComparison.Ordinal) && detail.Line == 1 && detail.Column >= 1);
		Assert.True(service.ValidateExpression("session", "document", "=vb:1 + 2").IsValid);
		var visualBasicError = service.ValidateExpression("session", "document", "=vb:1 +");
		Assert.False(visualBasicError.IsValid);
		Assert.Contains(visualBasicError.Details, detail => detail.Code.StartsWith("BC", StringComparison.Ordinal) && detail.Line == 1 && detail.Column >= 1);
	}

	[Fact]
	public void Expression_validation_resolves_workflow_variables_and_reports_unknown_identifiers()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddVariable("count", "Int32", ""));
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			ProjectAssemblyPath = typeof(WorkflowDocumentTests).Assembly.Location,
			ReferencedAssemblyPaths = { typeof(DesignerProtocol).Assembly.Location },
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		Assert.True(service.ValidateExpression("session", "document", "=count + 1").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=missing + 1").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=count.DoesNotExist").IsValid);
		Assert.True(service.ValidateExpression("session", "document", "=vb:count + 1").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=vb:missing + 1").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=vb:count.DoesNotExist").IsValid);
	}

	[Fact]
	public void Expression_completion_includes_workflow_variables_and_language_terms()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddVariable("count", "Int32", ""));
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			ProjectAssemblyPath = typeof(WorkflowDocumentTests).Assembly.Location,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		Assert.Contains("count", service.CompleteExpression("session", "document", "=co", "co").Items);
		Assert.Contains("ToString", service.CompleteExpression("session", "document", "=count.To", "To").Items);
		Assert.Contains("True", service.CompleteExpression("session", "document", "=vb:Tr", "Tr").Items);
		Assert.Contains("Abs", service.CompleteExpression("session", "document", "=Math.A", "A").Items);
		Assert.Contains("Max", service.CompleteExpression("session", "document", "=Math.M", "M").Items);
		Assert.Contains("ICSharpCode.WorkflowDesigner.Host.WorkflowDocumentTests", service.CompleteExpression("session", "document", "=ICSharpCode.WorkflowDesigner.Host.Work", "ICSharpCode.WorkflowDesigner.Host.Work").Items);
		Assert.Contains("Version", service.CompleteExpression("session", "document", "=ICSharpCode.SharpDevelop.Designer.Remote.DesignerProtocol.V", "V").Items);
		Assert.True(workflow.AddVariable("model", typeof(ExpressionModel).AssemblyQualifiedName!, ""));
		Assert.True(workflow.AddVariable("values", typeof(List<int>).AssemblyQualifiedName!, ""));
		Assert.Contains(workflow.GetVariables(), variable => variable.Name == "model" && variable.TypeName == nameof(ExpressionModel));
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 2,
			ProjectAssemblyPath = typeof(WorkflowDocumentTests).Assembly.Location,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		Assert.Contains("Title", service.CompleteExpression("session", "document", "=model.Ti", "Ti").Items);
		Assert.Contains("Add", service.CompleteExpression("session", "document", "=values.Ad", "Ad").Items);
		Assert.Contains("Where", service.CompleteExpression("session", "document", "=values.Wh", "Wh").Items);
		Assert.True(service.ValidateExpression("session", "document", "=model.Title").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=model.DoesNotExist").IsValid);
		Assert.True(service.ValidateExpression("session", "document", "=new ICSharpCode.WorkflowDesigner.Host.WorkflowDocumentTests()").IsValid);
	}

	[Fact]
	public void Expression_validation_and_completion_honor_the_selected_lexical_scope()
	{
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddVariable("rootValue", "Int32", ""));
		Assert.Equal("0", workflow.AddChild("", "Sequence"));
		Assert.True(workflow.AddVariable("innerValue", "Int32", "", "0"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.Contains("rootValue", service.CompleteExpression("session", "document", "=root", "root", "0").Items);
		Assert.Contains("innerValue", service.CompleteExpression("session", "document", "=inner", "inner", "0").Items);
		Assert.DoesNotContain("innerValue", service.CompleteExpression("session", "document", "=inner", "inner", "").Items);
		Assert.True(service.ValidateExpression("session", "document", "=innerValue + rootValue", "0").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=innerValue", "").IsValid);
	}

	[Fact]
	public void Flowchart_steps_are_linked_and_remain_after_round_trip()
	{
		var document = Load(new Flowchart());
		Assert.NotNull(document.AddChild("", "WriteLine"));
		Assert.NotNull(document.AddChild("", "WriteLine"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Equal(2, WorkflowInspectionServices.GetActivities(reloaded.Root!).Count());
	}

	[Fact]
	public void Catch_handler_exposes_its_exception_parameter_to_expression_completion()
	{
		var workflow = Load(new TryCatch());
		Assert.Equal("$catch-0", workflow.AddChild("", "Catch"));
		Assert.Equal("$catch-0.0", workflow.AddChild("$catch-0", "Sequence"));
		Assert.True(workflow.SetProperty("$catch-0", "$exceptionName", "error"));
		Assert.True(workflow.SetProperty("$catch-0", "$exceptionType", typeof(InvalidOperationException).AssemblyQualifiedName!));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.Contains("error", service.CompleteExpression("session", "document", "=er", "er", "$catch-0.0").Items);
		Assert.True(service.ValidateExpression("session", "document", "=error.Message", "$catch-0.0").IsValid);
	}

	[Fact]
	public void Pick_branches_keep_their_trigger_and_action_through_round_trip()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Pick"));
		Assert.Equal("0.$pick-0", document.AddChild("0", "PickBranch"));
		Assert.Equal("0.$pick-0.0", document.AddChild("0.$pick-0", "WriteLine"));
		Assert.Equal("0.$pick-0.1", document.AddChild("0.$pick-0", "Sequence"));
		Assert.IsType<WriteLine>(document.Find("0.$pick-0.0"));
		Assert.IsType<Sequence>(document.Find("0.$pick-0.1"));
		Assert.Equal("0.$pick-0.1.0", document.AddChild("0.$pick-0.1", "WriteLine"));
		Assert.IsType<WriteLine>(document.Find("0.$pick-0.1.0"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<WriteLine>(reloaded.Find("0.$pick-0.0"));
		Assert.True(reloaded.Remove("0.$pick-0.1"));
		Assert.Null(reloaded.Find("0.$pick-0.1"));
		Assert.True(reloaded.Remove("0.$pick-0"));
		Assert.Null(reloaded.Find("0.$pick-0.0"));
	}

	[Fact]
	public void Flowchart_steps_can_be_reconnected_without_creating_a_cycle()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("2", document.AddChild("", "WriteLine"));
		Assert.True(document.ConnectFlowSteps("0", "2"));
		Assert.False(document.ConnectFlowSteps("2", "0"));
		var steps = ((Flowchart)document.Root!).Nodes.OfType<FlowStep>().ToArray();
		Assert.Same(steps[2], steps[0].Next);
	}

	[Fact]
	public void Flowchart_can_add_edit_and_remove_a_flow_decision()
	{
		var document = Load(new Flowchart());
		Assert.Equal("$decision-0", document.AddChild("", "FlowDecision"));
		Assert.Contains(document.GetProperties((FlowDecision)((Flowchart)document.Root!).Nodes[0]), property => property.Name == "Condition" && property.Value == "False");
		Assert.True(document.SetProperty("$decision-0", "Condition", "True"));
		Assert.Contains(document.GetProperties((FlowDecision)((Flowchart)document.Root!).Nodes[0]), property => property.Name == "Condition" && property.Value == "True");
		Assert.True(document.Remove("$decision-0"));
		Assert.Empty(((Flowchart)document.Root!).Nodes);
		Assert.Null(((Flowchart)document.Root!).StartNode);
	}

	[Fact]
	public void Flow_decision_true_and_false_branches_can_target_flow_steps()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-2", document.AddChild("", "FlowDecision"));
		var nodes = ((Flowchart)document.Root!).Nodes;
		Assert.Same(nodes[2], Assert.IsType<FlowStep>(nodes[1]).Next);
		Assert.True(document.ConnectFlowDecision("$decision-2", "0", true));
		Assert.True(document.ConnectFlowDecision("$decision-2", "1", false));
		var decision = Assert.IsType<FlowDecision>(nodes[2]);
		Assert.Same(nodes[0], decision.True);
		Assert.Same(nodes[1], decision.False);
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		var reloadedDecision = Assert.IsType<FlowDecision>(((Flowchart)reloaded.Root!).Nodes[2]);
		Assert.NotNull(reloadedDecision.True);
		Assert.NotNull(reloadedDecision.False);
	}

	[Fact]
	public void Flowchart_start_node_can_be_explicitly_changed_to_a_step_or_decision()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-2", document.AddChild("", "FlowDecision"));
		var flowchart = (Flowchart)document.Root!;
		Assert.True(document.SetFlowStart("1"));
		Assert.Same(Assert.IsType<FlowStep>(flowchart.Nodes[1]), flowchart.StartNode);
		Assert.True(document.SetFlowStart("$decision-2"));
		Assert.Same(Assert.IsType<FlowDecision>(flowchart.Nodes[2]), flowchart.StartNode);
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<FlowDecision>(((Flowchart)reloaded.Root!).StartNode);
	}

	[Fact]
	public void Flowchart_edges_can_be_disconnected_without_deleting_nodes()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-2", document.AddChild("", "FlowDecision"));
		Assert.True(document.ConnectFlowDecision("$decision-2", "0", true));
		Assert.True(document.DisconnectFlow("0", "Next"));
		Assert.True(document.DisconnectFlow("$decision-2", "True"));
		var flowchart = (Flowchart)document.Root!;
		Assert.Null(Assert.IsType<FlowStep>(flowchart.Nodes[0]).Next);
		Assert.Null(Assert.IsType<FlowDecision>(flowchart.Nodes[2]).True);
		Assert.Equal(3, flowchart.Nodes.Count);
	}

	[Fact]
	public void Removing_a_flow_decision_disconnects_all_of_its_incoming_edges()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-1", document.AddChild("", "FlowDecision"));
		var flowchart = (Flowchart)document.Root!;
		var step = Assert.IsType<FlowStep>(flowchart.Nodes[0]);
		Assert.NotNull(step.Next);

		Assert.True(document.Remove("$decision-1"));
		Assert.Null(step.Next);
		Assert.True(document.ToXaml().Length > 0);
	}

	[Fact]
	public void Removing_a_flow_step_retargets_decision_branches_to_its_successor()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-2", document.AddChild("", "FlowDecision"));
		Assert.True(document.ConnectFlowDecision("$decision-2", "0", true));
		var flowchart = (Flowchart)document.Root!;
		var successor = Assert.IsType<FlowStep>(flowchart.Nodes[1]);

		Assert.True(document.Remove("0"));
		Assert.Same(successor, Assert.IsType<FlowDecision>(flowchart.Nodes.Single(node => node is FlowDecision)).True);
	}

	[Fact]
	public void Host_tree_projects_flow_decisions_as_selectable_nodes()
	{
		var document = Load(new Flowchart());
		Assert.Equal("$decision-0", document.AddChild("", "FlowDecision"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } }
		});
		Assert.NotNull(state.Tree);
		Assert.Contains(state.Tree!.Children, node => node.Type == "FlowDecision" && node.Id == "$decision-0" && node.Properties.Any(property => property.Name == "Condition"));
	}

	[Fact]
	public void Host_tree_projects_actual_flowchart_edges_for_the_canvas()
	{
		var document = Load(new Flowchart());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("$decision-2", document.AddChild("", "FlowDecision"));
		Assert.True(document.ConnectFlowDecision("$decision-2", "0", true));
		Assert.True(document.ConnectFlowDecision("$decision-2", "1", false));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } }
		});
		Assert.NotNull(state.Tree);
		Assert.Contains(state.Tree!.Children.Single(node => node.Id == "0").Properties, property => property.Name == "$flowNext" && property.Value == "1");
		var decision = state.Tree.Children.Single(node => node.Id == "$decision-2");
		Assert.Contains(decision.Properties, property => property.Name == "$flowTrue" && property.Value == "0");
		Assert.Contains(decision.Properties, property => property.Name == "$flowFalse" && property.Value == "1");
	}

	[Fact]
	public void Host_tree_keeps_an_empty_flowchart_editable()
	{
		var document = Load(new Flowchart());
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } }
		});
		Assert.NotNull(state.Tree);
		Assert.Empty(state.Tree!.Children);
	}

	[Fact]
	public void Variables_can_be_created_updated_and_removed_in_a_nested_activity_scope()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Sequence"));
		Assert.True(document.AddVariable("inner", "String", "", "0"));
		Assert.Contains(document.GetVariables(), variable => variable.Name == "inner" && variable.ScopeId == "0" && variable.Scope.StartsWith("Sequence", StringComparison.Ordinal));
		Assert.True(document.UpdateVariable("inner", "renamed", "Int32", "", "0"));
		Assert.Contains(document.GetVariables(), variable => variable.Name == "renamed" && variable.TypeName == "Int32" && variable.ScopeId == "0");
		Assert.True(document.RemoveVariable("renamed", "0"));
		Assert.DoesNotContain(document.GetVariables(), variable => variable.ScopeId == "0");
	}

	[Fact]
	public void Project_activity_toolbox_entries_honor_standard_component_model_metadata()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			ProjectAssemblyPath = typeof(MetadataActivity).Assembly.Location,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});

		var item = Assert.Single(service.GetCapabilities("session", "document").Toolbox, item => item.TypeName.StartsWith(typeof(MetadataActivity).FullName!, StringComparison.Ordinal));
		Assert.Equal("Send customer notification", item.DisplayName);
		Assert.Equal("Business activities", item.Category);
		Assert.Equal("Sends a notification using the current workflow context.", item.Template);
		Assert.Equal(typeof(MetadataActivity).Namespace, item.XamlNamespace);
	}

	[Fact]
	public void Refreshing_project_capabilities_preserves_the_open_workflow_document()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});
		state = service.AddElement("session", "document", state.Version, "", new DesignerToolboxItemInfo { TypeName = "Delay" }, "", 0, 0);

		var capabilities = service.RefreshCapabilities("session", "document", new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = state.Version,
			ProjectAssemblyPath = typeof(MetadataActivity).Assembly.Location
		});

		Assert.Contains(capabilities.Toolbox, item => item.TypeName.StartsWith(typeof(MetadataActivity).FullName!, StringComparison.Ordinal));
		var current = service.GetDebugState("session", "document");
		Assert.False(current.IsRunning);
		Assert.Equal(state.Version, service.Flush("session", "document", state.Version).BaseVersion);
		Assert.Contains("Delay", service.Flush("session", "document", state.Version).Files[0].Text, StringComparison.Ordinal);
	}

	[Fact]
	public void Enum_properties_are_projected_as_finite_choices_and_round_trip()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new EnumActivity()).ToXaml() } }
		});
		var mode = Assert.Single(state.Tree!.Properties, property => property.Name == nameof(EnumActivity.Mode));
		Assert.Equal("Enum", mode.Kind);
		Assert.Equal(new[] { nameof(EnumMode.Normal), nameof(EnumMode.Urgent) }, mode.AllowedValues);
		Assert.Equal("Delivery priority", mode.DisplayName);
		Assert.Equal("Delivery", mode.Category);
		Assert.Equal("Controls how the activity is dispatched.", mode.Description);
		Assert.DoesNotContain(state.Tree.Properties, property => property.Name == nameof(EnumActivity.HiddenImplementationDetail));
		state = service.SetProperty("session", "document", state.Version, "", nameof(EnumActivity.Mode), nameof(EnumMode.Urgent));
		Assert.Equal(nameof(EnumMode.Urgent), Assert.Single(state.Tree!.Properties, property => property.Name == nameof(EnumActivity.Mode)).Value);
		Assert.Throws<ArgumentException>(() => service.SetProperty("session", "document", state.Version, "", nameof(EnumActivity.Mode), "NotARealMode"));
	}

	[Fact]
	public void A_loaded_workflow_can_execute_with_the_corewf_runtime()
	{
		var document = Load(new Sequence());
		Assert.Empty(WorkflowInvoker.Invoke(document.Root!));
	}

	[Fact]
	public void Workflow_validation_reports_model_errors_without_executing()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence { Activities = { new If() } }).ToXaml() } } });
		var result = service.ValidateWorkflow("session", "document", 1);
		Assert.False(result.IsValid);
		Assert.NotEmpty(result.Diagnostics);
		Assert.Contains(result.Diagnostics, diagnostic => !diagnostic.IsWarning && !string.IsNullOrWhiteSpace(diagnostic.Message));
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.ElementId == "0");
	}

	[Fact]
	public void Run_accepts_declared_workflow_inputs_and_rejects_invalid_values()
	{
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddArgument("count", "Int32", ""));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var result = service.Run("session", "document", 1, new Dictionary<string, string> { ["count"] = "42" });
		Assert.True(result.Succeeded, result.Message);
		var invalid = service.Run("session", "document", 1, new Dictionary<string, string> { ["count"] = "not-a-number" });
		Assert.False(invalid.Succeeded);
		Assert.Contains("valid Int32", invalid.Message);
	}

	[Fact]
	public void Variable_expressions_resolve_the_nearest_nested_scope()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Sequence"));
		Assert.True(document.AddVariable("flag", "Boolean", "", ""));
		Assert.True(document.AddVariable("flag", "Boolean", "", "0"));
		Assert.Equal("0.0", document.AddChild("0", "If"));
		Assert.True(document.SetProperty("0.0", "Condition", "@flag"));
		Assert.Contains(document.GetProperties(document.Find("0.0")!), property => property.Name == "Condition" && property.Value == "@flag");
	}

	[Fact]
	public async Task A_running_workflow_can_be_cancelled_through_the_host_service()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Delay { Duration = TimeSpan.FromSeconds(20) }).ToXaml() } }
		});
		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.CancelRun("session", "document"), TimeSpan.FromSeconds(3)));
		var result = await running;
		Assert.False(result.Succeeded);
		Assert.Contains("Cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task A_breakpoint_pauses_a_running_activity_until_the_host_continues_it()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence { Activities = { new Delay { Duration = TimeSpan.FromMilliseconds(200) } } });
		Assert.True(workflow.AddArgument("count", "Int32", ""));
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		var breakpoint = service.ToggleBreakpoint("session", "document", "0");
		Assert.Contains("0", breakpoint.BreakpointElementIds);

		var running = Task.Run(() => service.Run("session", "document", 1, new Dictionary<string, string> { ["count"] = "42" }));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document").IsPaused, TimeSpan.FromSeconds(3)));
		var paused = service.GetDebugState("session", "document");
		Assert.Equal("0", paused.CurrentElementId);
		Assert.Equal("42", paused.Inputs["count"]);
		Assert.Collection(paused.CallStack,
			frame => { Assert.Equal("", frame.ElementId); Assert.Equal("Sequence", frame.TypeName); },
			frame => { Assert.Equal("0", frame.ElementId); Assert.Equal("Delay", frame.TypeName); });
		Assert.Contains(paused.Trace, entry => entry.Contains("Executing", StringComparison.Ordinal));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task An_idle_workflow_exposes_and_resumes_external_bookmarks()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new BookmarkActivity()).ToXaml() } }
		});

		var running = Task.Run(() => service.Run("session", "document", 1));
		var observed = SpinWait.SpinUntil(() => service.GetDebugState("session", "document").Bookmarks.Any(bookmark => bookmark.Name == "continue"), TimeSpan.FromSeconds(3));
		Assert.True(observed, running.IsCompleted ? "Run completed before exposing a bookmark: " + (await running).Message : "Workflow did not become idle with its bookmark.");
		var waiting = service.GetDebugState("session", "document");
		Assert.True(waiting.IsRunning);
		Assert.True(waiting.IsIdle);
		Assert.False(running.IsCompleted, "An idle workflow must remain active until its bookmark is resumed or it is stopped.");
		Assert.Contains(waiting.Bookmarks, bookmark => bookmark.Name == "continue" && bookmark.OwnerDisplayName == "Wait for host");
		Assert.True(service.ResumeBookmark("session", "document", "continue", "payload"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task A_breakpoint_in_a_switch_case_uses_the_case_structural_path()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Switch<int> { Expression = new InArgument<int>(0) });
		Assert.Equal("$case-0", workflow.AddChild("", "SwitchCase"));
		Assert.Equal("$case-0.0.0", workflow.AddChild("$case-0.0", "WriteLine"));
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		service.ToggleBreakpoint("session", "document", "$case-0.0.0");
		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "$case-0.0.0" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task A_breakpoint_on_a_state_transition_trigger_uses_its_virtual_path()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-1", workflow.AddChild("", "State"));
		Assert.True(workflow.SetProperty("$state-1", "IsFinal", "True"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		Assert.True(workflow.SetProperty("$state-0.$transition-0", "$target", "$state-1"));
		Assert.Equal("$state-0.$transition-0.0", workflow.AddChild("$state-0.$transition-0", "Delay"));
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		service.ToggleBreakpoint("session", "document", "$state-0.$transition-0.0");
		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "$state-0.$transition-0.0" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task Step_pauses_again_at_the_next_activity()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence { Activities = {
			new Delay { Duration = TimeSpan.FromMilliseconds(100) },
			new Delay { Duration = TimeSpan.FromMilliseconds(100) }
		} });
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		service.ToggleBreakpoint("session", "document", "0");

		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document").IsPaused, TimeSpan.FromSeconds(3)));
		Assert.True(service.StepRun("session", "document"));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "1" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task Step_over_pauses_at_the_next_activity_in_the_same_scope()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence { Activities = {
			new Delay { Duration = TimeSpan.FromMilliseconds(100) },
			new Delay { Duration = TimeSpan.FromMilliseconds(100) }
		} });
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		service.ToggleBreakpoint("session", "document", "0");

		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "0" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.StepOverRun("session", "document"));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "1" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public async Task Step_out_pauses_at_the_next_activity_outside_the_current_scope()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Sequence { Activities = {
			new Sequence { Activities = { new Delay { Duration = TimeSpan.FromMilliseconds(60) }, new Delay { Duration = TimeSpan.FromMilliseconds(60) } } },
			new Delay { Duration = TimeSpan.FromMilliseconds(60) }
		} });
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		service.ToggleBreakpoint("session", "document", "0.0");

		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "0.0" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.StepOutRun("session", "document"));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document") is { IsPaused: true, CurrentElementId: "1" }, TimeSpan.FromSeconds(3)));
		Assert.True(service.ContinueRun("session", "document"));
		Assert.True((await running).Succeeded);
	}

	[Fact]
	public void Try_catch_accepts_a_catch_handler_and_round_trips_it()
	{
		var document = Load(new TryCatch());
		var catchId = document.AddChild("", "Catch");
		Assert.Equal("$catch-0", catchId);
		Assert.Equal("$catch-0.0", document.AddChild(catchId!, "Delay"));

		var tryCatch = Assert.IsType<TryCatch>(document.Root);
		var @catch = Assert.Single(tryCatch.Catches);
		Assert.Equal(typeof(Exception), @catch.ExceptionType);
		Assert.IsType<Delay>(@catch.GetType().GetProperty("Action")!.GetValue(@catch)!.GetType().GetProperty("Handler")!.GetValue(@catch.GetType().GetProperty("Action")!.GetValue(@catch)));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		var reloadedTryCatch = Assert.IsType<TryCatch>(reloaded.Root);
		Assert.Single(reloadedTryCatch.Catches);
		Assert.True(reloaded.Remove("$catch-0.0"));
		Assert.True(reloaded.Remove("$catch-0"));
		Assert.Empty(reloadedTryCatch.Catches);
	}

	[Fact]
	public void Catch_handler_children_have_stable_paths_and_can_be_removed_independently()
	{
		var document = Load(new TryCatch());
		Assert.Equal("$catch-0", document.AddChild("", "Catch"));
		Assert.Equal("$catch-0.0", document.AddChild("$catch-0", "Sequence"));
		Assert.Equal("$catch-0.0.0", document.AddChild("$catch-0.0", "If"));
		Assert.IsType<If>(document.Find("$catch-0.0.0"));
		Assert.True(document.Remove("$catch-0.0.0"));
		Assert.IsType<Sequence>(document.Find("$catch-0.0"));
		Assert.True(document.Remove("$catch-0.0"));
		Assert.Null(document.Find("$catch-0.0"));
	}

	[Fact]
	public void Catch_exception_type_can_be_changed_without_losing_its_handler()
	{
		var document = Load(new TryCatch());
		Assert.Equal("$catch-0", document.AddChild("", "Catch"));
		Assert.Equal("$catch-0.0", document.AddChild("$catch-0", "Sequence"));
		Assert.Equal("$catch-0.0.0", document.AddChild("$catch-0.0", "If"));
		Assert.True(document.SetProperty("$catch-0", "$exceptionType", typeof(InvalidOperationException).AssemblyQualifiedName!));
		var tryCatch = Assert.IsType<TryCatch>(document.Root);
		Assert.Equal(typeof(InvalidOperationException), Assert.Single(tryCatch.Catches).ExceptionType);
		Assert.IsType<If>(document.Find("$catch-0.0.0"));
		Assert.Equal("$catch-1", document.AddChild("", "Catch"));
		Assert.False(document.SetProperty("$catch-1", "$exceptionType", typeof(InvalidOperationException).AssemblyQualifiedName!));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Equal(typeof(InvalidOperationException), Assert.IsType<TryCatch>(reloaded.Root).Catches[0].ExceptionType);
		Assert.IsType<If>(reloaded.Find("$catch-0.0.0"));
	}

	[Fact]
	public void Int32_switch_cases_and_default_have_editable_handler_paths_and_round_trip()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "SwitchInt32"));
		Assert.Contains(document.GetProperties(document.Find("0")!), property => property.Name == "Expression" && property.Value == "0");
		Assert.Equal("0.$case-0", document.AddChild("0", "SwitchCase"));
		Assert.Equal("0.$case-0.0.0", document.AddChild("0.$case-0.0", "If"));
		Assert.Equal("0.$default", document.AddChild("0", "SwitchDefault"));
		Assert.Equal("0.$default.0.0", document.AddChild("0.$default.0", "If"));
		Assert.IsType<If>(document.Find("0.$case-0.0.0"));
		Assert.IsType<If>(document.Find("0.$default.0.0"));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<If>(reloaded.Find("0.$case-0.0.0"));
		Assert.IsType<If>(reloaded.Find("0.$default.0.0"));
		Assert.True(reloaded.Remove("0.$case-0"));
		Assert.Null(reloaded.Find("0.$case-0.0"));
	}

	[Fact]
	public void Int32_switch_case_value_can_be_changed_without_losing_its_handler()
	{
		// Case element ids are ordinal, so rekeying a case keeps its id and its handler; only the
		// case value the properties pad shows changes.
		var document = Load(new Switch<int> { Expression = new InArgument<int>(0) });
		Assert.Equal("$case-0", document.AddChild("", "SwitchCase"));
		Assert.Equal("$case-0.0.0", document.AddChild("$case-0.0", "If"));
		Assert.Equal("$case-1", document.AddChild("", "SwitchCase"));
		Assert.True(document.SetProperty("$case-0", "$caseValue", "7"));
		Assert.IsType<If>(document.Find("$case-0.0.0"));
		Assert.False(document.SetProperty("$case-0", "$caseValue", "1"));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<If>(reloaded.Find("$case-0.0.0"));
	}

	[Fact]
	public void A_generic_toolbox_item_is_closed_with_the_requested_type_argument()
	{
		var document = Load(new Sequence());
		var forEach = typeof(ForEach<>).AssemblyQualifiedName + "|System.String";
		Assert.Equal("0", document.AddChild("", forEach));
		var added = Assert.IsType<ForEach<string>>(document.Find("0"));
		// A freshly dropped loop must be renderable and runnable, so its required Values argument
		// is seeded with an empty sequence of its own element type.
		Assert.NotNull(added.Values?.Expression);

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<ForEach<string>>(reloaded.Find("0"));
	}

	[Fact]
	public void A_generic_toolbox_item_dropped_without_a_type_argument_falls_back_to_Object()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", typeof(Switch<>).AssemblyQualifiedName!));
		Assert.IsType<Switch<object>>(document.Find("0"));
	}

	[Fact]
	public void A_string_keyed_switch_is_editable_through_the_same_case_ids()
	{
		var document = Load(new Switch<string> { Expression = new InArgument<string>("") });
		Assert.Equal("$case-0", document.AddChild("", "SwitchCase"));
		Assert.Equal("$case-0.0.0", document.AddChild("$case-0.0", "If"));
		Assert.True(document.SetProperty("$case-0", "$caseValue", "ready"));
		Assert.Equal("$case-1", document.AddChild("", "SwitchCase"));
		Assert.False(document.SetProperty("$case-1", "$caseValue", "ready"));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<If>(reloaded.Find("$case-0.0.0"));
		Assert.True(reloaded.Remove("$case-0"));
		Assert.Null(reloaded.Find("$case-0.0.0"));
	}

	[Fact]
	public void A_string_loop_body_declares_its_item_variable_with_the_loop_element_type()
	{
		var document = Load(new ForEach<string>());
		Assert.Equal("$foreach-body.0", document.AddChild("$foreach-body", "If"));
		Assert.True(document.SetProperty("$foreach-body", "$itemName", "line"));
		var itemVariable = Assert.Single(document.GetVariablesVisibleAt("$foreach-body.0").Where(variable => variable.Name == "line"));
		Assert.Equal("String", itemVariable.TypeName);

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<If>(reloaded.Find("$foreach-body.0"));
	}

	[Fact]
	public void Int32_switch_projects_case_and_default_nodes_into_designer_state()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Switch<int> { Expression = new InArgument<int>(0) });
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		state = service.AddElement("session", "document", state.Version, "", new DesignerToolboxItemInfo { Name = "SwitchCase", TypeName = "SwitchCase" }, "", 0, 0);
		Assert.Equal("$case-0", state.CreatedElementId);
		Assert.Contains(state.Tree!.Children, child => child.Id == "$case-0" && child.Type == "SwitchCase");
		state = service.AddElement("session", "document", state.Version, "", new DesignerToolboxItemInfo { Name = "SwitchDefault", TypeName = "SwitchDefault" }, "", 0, 0);
		Assert.Equal("$default", state.CreatedElementId);
		Assert.Contains(state.Tree!.Children, child => child.Id == "$default" && child.Type == "SwitchDefault");
	}

	[Fact]
	public void Try_catch_projects_the_catch_and_its_handler_into_designer_state()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new TryCatch());
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		state = service.AddElement("session", "document", state.Version,
			"", new DesignerToolboxItemInfo { Name = "Catch", TypeName = "Catch" }, "", 0, 0);
		Assert.Equal("$catch-0", state.CreatedElementId);
		var catchNode = Assert.Single(state.Tree!.Children);
		Assert.Equal("Catch", catchNode.Type);
		state = service.AddElement("session", "document", state.Version,
			"$catch-0", new DesignerToolboxItemInfo { Name = "Delay", TypeName = "Delay" }, "", 0, 0);
		Assert.Equal("$catch-0.0", state.CreatedElementId);
		Assert.Equal("Delay", Assert.Single(Assert.Single(state.Tree!.Children).Children).Type);
	}

	[Fact]
	public void Pick_projects_editable_branch_trigger_and_action_nodes()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new Pick());
		var state = service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		state = service.AddElement("session", "document", state.Version, "", new DesignerToolboxItemInfo { Name = "PickBranch", TypeName = "PickBranch" }, "", 0, 0);
		Assert.Equal("$pick-0", state.CreatedElementId);
		var branch = Assert.Single(state.Tree!.Children);
		Assert.Equal("PickBranch", branch.Type);
		state = service.AddElement("session", "document", state.Version, "$pick-0", new DesignerToolboxItemInfo { Name = "WriteLine", TypeName = "WriteLine" }, "", 0, 0);
		Assert.Equal("$pick-0.0", state.CreatedElementId);
		state = service.AddElement("session", "document", state.Version, "$pick-0", new DesignerToolboxItemInfo { Name = "Sequence", TypeName = "Sequence" }, "", 0, 0);
		Assert.Equal("$pick-0.1", state.CreatedElementId);
		branch = Assert.Single(state.Tree!.Children);
		Assert.Equal(new[] { "WriteLine", "Sequence" }, branch.Children.Select(child => child.Type));
	}

	[Fact]
	public void Run_reports_a_clear_message_for_unavailable_compiled_expressions()
	{
		var document = Load(new If());
		Assert.True(document.SetProperty("", "Condition", "=1 < 2"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } } });
		var result = service.Run("session", "document", 1);
		Assert.False(result.Succeeded);
		Assert.Contains("expression execution is unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("C#", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_identifies_visual_basic_expressions_in_the_preflight_message()
	{
		var document = Load(new If());
		Assert.True(document.SetProperty("", "Condition", "=vb:1 < 2"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } } });
		var result = service.Run("session", "document", 1);
		Assert.False(result.Succeeded);
		Assert.Contains("VB", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Int32_for_each_projects_and_round_trips_its_body_delegate()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "ForEachInt32"));
		Assert.Equal("0.$foreach-body.0", document.AddChild("0.$foreach-body", "Sequence"));
		Assert.Equal("0.$foreach-body.0.0", document.AddChild("0.$foreach-body.0", "WriteLine"));
		Assert.IsType<WriteLine>(document.Find("0.$foreach-body.0.0"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<WriteLine>(reloaded.Find("0.$foreach-body.0.0"));
		Assert.True(reloaded.Remove("0.$foreach-body.0"));
		Assert.Null(reloaded.Find("0.$foreach-body.0"));
	}

	[Fact]
	public void State_machine_projects_editable_states_and_entry_exit_slots()
	{
		var document = Load(new StateMachine());
		Assert.Equal("$state-0", document.AddChild("", "State"));
		Assert.Equal("$state-1", document.AddChild("", "State"));
		Assert.True(document.SetProperty("$state-1", "$initial", "True"));
		Assert.Equal("$state-0.0", document.AddChild("$state-0", "WriteLine"));
		Assert.Equal("$state-0.1", document.AddChild("$state-0", "Sequence"));
		Assert.True(document.SetProperty("$state-0", "$displayName", "Ready"));
		Assert.True(document.SetProperty("$state-1", "IsFinal", "True"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.False(reloaded.SetProperty("$state-1", "$initial", "False"));
		Assert.IsType<WriteLine>(reloaded.Find("$state-0.0"));
		Assert.IsType<Sequence>(reloaded.Find("$state-0.1"));
	}

	[Fact]
	public void Final_state_rejects_exit_and_transitions_and_cannot_be_set_final_with_them()
	{
		var document = Load(new StateMachine());
		Assert.Equal("$state-0", document.AddChild("", "State"));
		Assert.Equal("$state-0.0", document.AddChild("$state-0", "WriteLine"));
		Assert.Equal("$state-0.1", document.AddChild("$state-0", "Sequence"));
		Assert.False(document.SetProperty("$state-0", "IsFinal", "True"));
		Assert.True(document.Remove("$state-0.1"));
		Assert.True(document.SetProperty("$state-0", "IsFinal", "True"));
		Assert.Null(document.AddChild("$state-0", "Transition"));
		Assert.Null(document.AddChild("$state-0", "Sequence"));
	}

	[Fact]
	public void State_machine_transitions_target_states_and_round_trip_their_slots()
	{
		var document = Load(new StateMachine());
		Assert.Equal("$state-0", document.AddChild("", "State"));
		Assert.Equal("$state-1", document.AddChild("", "State"));
		Assert.Equal("$state-0.$transition-0", document.AddChild("$state-0", "Transition"));
		Assert.True(document.SetProperty("$state-0.$transition-0", "$target", "$state-1"));
		Assert.True(document.SetProperty("$state-0.$transition-0", "Condition", "True"));
		Assert.Equal("$state-0.$transition-0.0", document.AddChild("$state-0.$transition-0", "Delay"));
		Assert.Equal("$state-0.$transition-0.1", document.AddChild("$state-0.$transition-0", "WriteLine"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<Delay>(reloaded.Find("$state-0.$transition-0.0"));
		Assert.IsType<WriteLine>(reloaded.Find("$state-0.$transition-0.1"));
		Assert.Contains("true", reloaded.ToXaml(), StringComparison.OrdinalIgnoreCase);
		Assert.True(reloaded.SetProperty("$state-0.$transition-0", "$target", ""));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", Text = reloaded.ToXaml() } } });
		var transition = Assert.Single(Assert.Single(state.Tree!.Children, node => node.Id == "$state-0").Children, node => node.Type == "Transition");
		Assert.Equal("", Assert.Single(transition.Properties, property => property.Name == "$target").Value);
		Assert.True(reloaded.Remove("$state-0.$transition-0"));
	}

	[Fact]
	public void State_variables_are_editable_and_visible_to_transition_conditions()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.True(workflow.AddVariable("ready", "Boolean", "True", "$state-0"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.Contains("ready", service.CompleteExpression("session", "document", "=rea", "rea", "$state-0.$transition-0").Items);
		Assert.True(service.ValidateExpression("session", "document", "=ready", "$state-0.$transition-0").IsValid);
		Assert.Contains(workflow.GetVariables(), variable => variable.Name == "ready" && variable.ScopeId == "$state-0");
	}

	[Fact]
	public void Variable_scope_catalog_exposes_real_activity_and_state_scopes_only()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-0.0", workflow.AddChild("$state-0", "WriteLine"));
		var scopes = workflow.GetVariableScopes().ToArray();
		Assert.Contains(scopes, scope => scope.ScopeId == "");
		Assert.Contains(scopes, scope => scope.ScopeId == "$state-0");
		Assert.DoesNotContain(scopes, scope => scope.ScopeId == "$state-0.0");
	}

	[Fact]
	public void Variable_scope_rpc_projects_state_scopes_for_the_parent_panel()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var scopes = service.GetVariableScopes("session", "document");
		Assert.Contains(scopes, scope => scope.ScopeId == "");
		Assert.Contains(scopes, scope => scope.ScopeId == "$state-0");
	}

	[Fact]
	public void State_machine_tree_projects_internal_activities_only_under_their_virtual_state_slots()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-0.0", workflow.AddChild("$state-0", "WriteLine"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		Assert.Equal("$state-0.$transition-0.0", workflow.AddChild("$state-0.$transition-0", "Delay"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var stateNode = Assert.Single(state.Tree!.Children);
		Assert.Equal("$state-0", stateNode.Id);
		Assert.Equal("WriteLine", Assert.Single(stateNode.Properties, property => property.Name == "$entry").Value);
		Assert.Equal("(empty)", Assert.Single(stateNode.Properties, property => property.Name == "$exit").Value);
		Assert.Equal(new[] { "$state-0.0", "$state-0.$transition-0" }, stateNode.Children.Select(child => child.Id));
	}

	[Fact]
	public void State_machine_delay_trigger_transitions_to_a_final_state_at_runtime()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-1", workflow.AddChild("", "State"));
		Assert.True(workflow.SetProperty("$state-1", "IsFinal", "True"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		Assert.True(workflow.SetProperty("$state-0.$transition-0", "$target", "$state-1"));
		Assert.Equal("$state-0.$transition-0.0", workflow.AddChild("$state-0.$transition-0", "Delay"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var result = service.Run("session", "document", 1);
		Assert.True(result.Succeeded, result.Message);
	}

	[Fact]
	public void Run_reports_an_actionable_diagnostic_for_a_state_machine_waiting_for_an_external_event()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-1", workflow.AddChild("", "State"));
		Assert.True(workflow.SetProperty("$state-1", "IsFinal", "True"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		Assert.True(workflow.SetProperty("$state-0.$transition-0", "$target", "$state-1"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var result = service.Run("session", "document", 1);
		Assert.False(result.Succeeded);
		Assert.Contains("no Trigger", result.Message, StringComparison.Ordinal);
		Assert.Contains("Delay", result.Message, StringComparison.Ordinal);
	}


	[Fact]
	public void Removing_a_state_disconnects_inbound_transitions_and_undo_restores_them()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-1", workflow.AddChild("", "State"));
		Assert.Equal("$state-0.$transition-0", workflow.AddChild("$state-0", "Transition"));
		Assert.True(workflow.SetProperty("$state-0.$transition-0", "$target", "$state-1"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		state = service.DeleteElements("session", "document", state.Version, new[] { "$state-1" });
		var transition = Assert.Single(Assert.Single(state.Tree!.Children, node => node.Id == "$state-0").Children);
		Assert.Equal("", Assert.Single(transition.Properties, property => property.Name == "$target").Value);
		state = service.Undo("session", "document", state.Version);
		transition = Assert.Single(Assert.Single(state.Tree!.Children, node => node.Id == "$state-0").Children);
		Assert.Equal("$state-1", Assert.Single(transition.Properties, property => property.Name == "$target").Value);
	}

	[Fact]
	public void Removing_the_initial_state_never_promotes_a_final_state_to_initial()
	{
		var workflow = Load(new StateMachine());
		Assert.Equal("$state-0", workflow.AddChild("", "State"));
		Assert.Equal("$state-1", workflow.AddChild("", "State"));
		Assert.True(workflow.SetProperty("$state-1", "IsFinal", "True"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		state = service.DeleteElements("session", "document", state.Version, new[] { "$state-0" });
		var remaining = Assert.Single(state.Tree!.Children);
		Assert.Equal("False", Assert.Single(remaining.Properties, property => property.Name == "$initial").Value);
	}

	[Fact]
	public void For_each_body_exposes_its_typed_item_to_expression_validation_and_completion()
	{
		var workflow = Load(new Sequence());
		Assert.Equal("0", workflow.AddChild("", "ForEachInt32"));
		Assert.Equal("0.$foreach-body.0", workflow.AddChild("0.$foreach-body", "Sequence"));
		Assert.True(workflow.SetProperty("0.$foreach-body", "$itemName", "number"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.Contains("number", service.CompleteExpression("session", "document", "=nu", "nu", "0.$foreach-body.0").Items);
		Assert.True(service.ValidateExpression("session", "document", "=number + 1", "0.$foreach-body.0").IsValid);
	}

	[Fact]
	public void Workflow_input_arguments_are_available_to_expression_validation_and_completion()
	{
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddArgument("greeting", "String", "hello"));
		Assert.True(workflow.AddArgument("count", "Int32", "", "InOut"));
		Assert.True(workflow.AddArgument("result", "String", "", "Out"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.Contains("greeting", service.CompleteExpression("session", "document", "=gre", "gre").Items);
		Assert.Contains("count", service.CompleteExpression("session", "document", "=cou", "cou").Items);
		Assert.DoesNotContain("result", service.CompleteExpression("session", "document", "=res", "res").Items);
		Assert.True(service.ValidateExpression("session", "document", "=greeting.Length + count").IsValid);
	}

	[Fact]
	public void Local_variable_shadows_an_equally_named_workflow_argument_in_expression_scope()
	{
		var workflow = Load(new Sequence());
		Assert.True(workflow.AddArgument("value", "String", "text"));
		Assert.Equal("0", workflow.AddChild("", "Sequence"));
		Assert.True(workflow.AddVariable("value", "Int32", "1", "0"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		Assert.True(service.ValidateExpression("session", "document", "=value + 1", "0").IsValid);
	}

	[Fact]
	public void Int32_for_each_projects_its_body_into_designer_state()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var workflow = Load(new ForEach<int> { Values = new InArgument<IEnumerable<int>>(new Microsoft.CSharp.Activities.CSharpValue<IEnumerable<int>>("new int[0]")) });
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } } });
		var body = Assert.Single(state.Tree!.Children);
		Assert.Equal("$foreach-body", body.Id);
		Assert.Equal("ForEachBody", body.Type);
		state = service.AddElement("session", "document", state.Version, "$foreach-body", new DesignerToolboxItemInfo { Name = "WriteLine", TypeName = "WriteLine" }, "", 0, 0);
		Assert.Equal("$foreach-body.0", state.CreatedElementId);
		Assert.Equal("WriteLine", Assert.Single(Assert.Single(state.Tree!.Children).Children).Type);
	}

	[Fact]
	public void Int32_parallel_for_each_projects_and_round_trips_its_body_delegate()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "ParallelForEachInt32"));
		Assert.Equal("0.$parallelforeach-body.0", document.AddChild("0.$parallelforeach-body", "Sequence"));
		Assert.Equal("0.$parallelforeach-body.0.0", document.AddChild("0.$parallelforeach-body.0", "WriteLine"));
		Assert.IsType<WriteLine>(document.Find("0.$parallelforeach-body.0.0"));
		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<WriteLine>(reloaded.Find("0.$parallelforeach-body.0.0"));
	}


	[Theory]
	[InlineData("If")]
	[InlineData("While")]
	[InlineData("DoWhile")]
	public void Conditional_toolbox_activities_receive_a_valid_default_condition(string typeName)
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", typeName));
		var inserted = document.Find("0");
		Assert.NotNull(inserted);
		Assert.Contains(document.GetProperties(inserted!), property => property.Name == "Condition" && property.Value == "False" && !property.IsReadOnly);
		Assert.True(document.SetProperty("0", "Condition", "True"));
		Assert.Contains(document.GetProperties(document.Find("0")!), property => property.Name == "Condition" && property.Value == "True");

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.Single(WorkflowInspectionServices.GetActivities(reloaded.Root!));
		Assert.Contains(reloaded.GetProperties(reloaded.Find("0")!), property => property.Name == "Condition" && property.Value == "True");
	}

	[Fact]
	public void Delay_toolbox_activity_receives_an_executable_default_duration()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Delay"));
		Assert.Contains(document.GetProperties(document.Find("0")!), property => property.Name == "Duration" && property.Value == "00:00:00" && !property.IsReadOnly);
		Assert.True(document.SetProperty("0", "Duration", "00:00:00.001"));
		var delay = Assert.IsType<Delay>(document.Find("0"));
		Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.IsType<System.Activities.Expressions.Literal<TimeSpan>>(delay.Duration.Expression).Value);
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } } });
		var result = service.Run("session", "document", 1);
		Assert.True(result.Succeeded, result.Message);
	}

	[Fact]
	public void Copied_activity_subtrees_are_detached_and_paste_through_the_versioned_host_protocol()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Sequence"));
		Assert.Equal("0.0", document.AddChild("0", "WriteLine"));
		Assert.True(document.SetProperty("0", "$displayName", "Original branch"));
		Assert.True(document.SetProperty("0.0", "$displayName", "Nested action"));

		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = document.ToXaml() } } });
		Assert.True(service.CopyElements("session", "document", new[] { "0", "0.0" }));
		state = service.PasteElements("session", "document", state.Version, "");

		Assert.Equal("1", state.CreatedElementId);
		var pasted = Assert.Single(state.Tree!.Children, node => node.Id == "1");
		Assert.Equal("Original branch", pasted.Name);
		Assert.Equal("Nested action", Assert.Single(pasted.Children).Name);
		Assert.True(state.CanUndo);

		state = service.Undo("session", "document", state.Version);
		Assert.DoesNotContain(state.Tree!.Children, node => node.Id == "1");
	}

	[Fact]
	public void Multi_item_paste_is_atomic_when_a_structured_destination_runs_out_of_slots()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("2", document.AddChild("", "If"));
		Assert.True(document.Copy(new[] { "0", "1" }));

		Assert.Null(document.Paste("2"));
		var conditional = Assert.IsType<If>(document.Find("2"));
		Assert.Null(conditional.Then);
		Assert.Null(conditional.Else);
	}

	[Fact]
	public void Host_scoped_clipboard_pastes_between_open_workflow_documents()
	{
		var source = Load(new Sequence());
		Assert.Equal("0", source.AddChild("", "WriteLine"));
		Assert.True(source.SetProperty("0", "$displayName", "Copied from another document"));
		var destination = Load(new Sequence());
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "source", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", Text = source.ToXaml() } } });
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "destination", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", Text = destination.ToXaml() } } });

		Assert.True(service.CopyElements("session", "source", new[] { "0" }));
		state = service.PasteElements("session", "destination", state.Version, "");
		Assert.Equal("0", state.CreatedElementId);
		Assert.Equal("Copied from another document", Assert.Single(state.Tree!.Children).Name);
	}

	[Fact]
	public void Multi_selection_deletion_and_cut_are_ancestor_aware_atomic_history_operations()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "Sequence"));
		Assert.Equal("0.0", document.AddChild("0", "WriteLine"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", Text = document.ToXaml() } } });

		state = service.DeleteElements("session", "document", state.Version, new[] { "0", "0.0" });
		Assert.Empty(state.Tree!.Children);
		state = service.Undo("session", "document", state.Version);
		Assert.Equal("WriteLine", Assert.Single(Assert.Single(state.Tree!.Children).Children).Type);

		state = service.CutElements("session", "document", state.Version, new[] { "0", "0.0" });
		Assert.Empty(state.Tree!.Children);
		state = service.PasteElements("session", "document", state.Version, "");
		Assert.Equal("0", state.CreatedElementId);
		Assert.Equal("WriteLine", Assert.Single(Assert.Single(state.Tree!.Children).Children).Type);
	}

	[Fact]
	public void Collection_activity_reordering_updates_the_structural_path_and_undo_history()
	{
		var document = Load(new Sequence());
		Assert.Equal("0", document.AddChild("", "WriteLine"));
		Assert.Equal("1", document.AddChild("", "WriteLine"));
		Assert.Equal("2", document.AddChild("", "WriteLine"));
		Assert.True(document.SetProperty("0", "$displayName", "first"));
		Assert.True(document.SetProperty("1", "$displayName", "second"));
		Assert.True(document.SetProperty("2", "$displayName", "third"));
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		var state = service.Open(new DesignerDocumentSnapshot { SessionId = "session", DocumentId = "document", Version = 1, Files = { new DesignerSourceFileSnapshot { Kind = "Designer", Text = document.ToXaml() } } });

		state = service.MoveElement("session", "document", state.Version, "1", -1);
		Assert.Equal("0", state.CreatedElementId);
		Assert.Equal(new[] { "second", "first", "third" }, state.Tree!.Children.Select(node => node.Name));
		state = service.Undo("session", "document", state.Version);
		Assert.Equal(new[] { "first", "second", "third" }, state.Tree!.Children.Select(node => node.Name));
		Assert.Throws<InvalidOperationException>(() => service.MoveElement("session", "document", state.Version, "0", -1));
	}

	static WorkflowDocument Load(Activity activity)
	{
		var builder = new ActivityBuilder { Name = "TestWorkflow", Implementation = activity };
		using var writer = new StringWriter();
		using (var xml = XmlWriter.Create(writer))
			XamlServices.Save(ActivityXamlServices.CreateBuilderWriter(new XamlXmlWriter(xml, new XamlSchemaContext())), builder);
		var document = new WorkflowDocument();
		document.Reset(writer.ToString());
		Assert.True(document.LastParseSucceeded, document.Error);
		return document;
	}
}

public sealed class ExpressionModel { public string Title { get; set; } = ""; }

[System.ComponentModel.DisplayName("Send customer notification")]
[System.ComponentModel.Category("Business activities")]
[System.ComponentModel.Description("Sends a notification using the current workflow context.")]
public sealed class MetadataActivity : Activity { }

public enum EnumMode { Normal, Urgent }
public sealed class EnumActivity : Activity {
	[System.ComponentModel.DisplayName("Delivery priority")]
	[System.ComponentModel.Category("Delivery")]
	[System.ComponentModel.Description("Controls how the activity is dispatched.")]
	public EnumMode Mode { get; set; } = EnumMode.Normal;
	[System.ComponentModel.Browsable(false)]
	public bool HiddenImplementationDetail { get; set; }
}

public sealed class BookmarkActivity : NativeActivity
{
	public BookmarkActivity() => DisplayName = "Wait for host";
	protected override bool CanInduceIdle => true;
	protected override void Execute(NativeActivityContext context) => context.CreateBookmark("continue", OnResume);
	void OnResume(NativeActivityContext context, Bookmark bookmark, object value) { }
}
