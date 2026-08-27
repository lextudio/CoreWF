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
	public void Expression_validation_reports_csharp_and_visual_basic_syntax_errors()
	{
		var service = new WorkflowDesignerHostService("aa");
		service.Initialize("aa", DesignerProtocol.Version, "session");
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = Load(new Sequence()).ToXaml() } }
		});
		Assert.True(service.ValidateExpression("session", "document", "=1 + 2").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=1 +").IsValid);
		Assert.True(service.ValidateExpression("session", "document", "=vb:1 + 2").IsValid);
		Assert.False(service.ValidateExpression("session", "document", "=vb:1 +").IsValid);
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
	public void A_loaded_workflow_can_execute_with_the_corewf_runtime()
	{
		var document = Load(new Sequence());
		Assert.Empty(WorkflowInvoker.Invoke(document.Root!));
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
		service.Open(new DesignerDocumentSnapshot {
			SessionId = "session", DocumentId = "document", Version = 1,
			Files = { new DesignerSourceFileSnapshot { Kind = "Designer", FileName = "workflow.xaml", Text = workflow.ToXaml() } }
		});
		var breakpoint = service.ToggleBreakpoint("session", "document", "0");
		Assert.Contains("0", breakpoint.BreakpointElementIds);

		var running = Task.Run(() => service.Run("session", "document", 1));
		Assert.True(SpinWait.SpinUntil(() => service.GetDebugState("session", "document").IsPaused, TimeSpan.FromSeconds(3)));
		var paused = service.GetDebugState("session", "document");
		Assert.Equal("0", paused.CurrentElementId);
		Assert.True(service.ContinueRun("session", "document"));
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
		var document = Load(new Switch<int> { Expression = new InArgument<int>(0) });
		Assert.Equal("$case-0", document.AddChild("", "SwitchCase"));
		Assert.Equal("$case-0.0.0", document.AddChild("$case-0.0", "If"));
		Assert.Equal("$case-1", document.AddChild("", "SwitchCase"));
		Assert.True(document.SetProperty("$case-0", "$caseValue", "7"));
		Assert.Null(document.Find("$case-0.0"));
		Assert.IsType<If>(document.Find("$case-7.0.0"));
		Assert.False(document.SetProperty("$case-7", "$caseValue", "1"));

		var reloaded = new WorkflowDocument();
		reloaded.Reset(document.ToXaml());
		Assert.True(reloaded.LastParseSucceeded, reloaded.Error);
		Assert.IsType<If>(reloaded.Find("$case-7.0.0"));
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
