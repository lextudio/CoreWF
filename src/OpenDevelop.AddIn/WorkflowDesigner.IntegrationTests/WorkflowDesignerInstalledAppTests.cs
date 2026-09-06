using OpenDevelop.IntegrationTests;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CoreWf.OpenDevelop.AddIn.IntegrationTests;

[Collection(OpenDevelopInstalledAppCollection.Name)]
public sealed class WorkflowDesignerInstalledAppTests
{
	readonly OpenDevelopAppFixture app;
	const int AddinDevFlowPort = 9301;

	public WorkflowDesignerInstalledAppTests(OpenDevelopAppFixture app) => this.app = app;

	async Task<AddinHostClient> StartAddinHostAsync()
	{
		var addinProject = Path.Combine(Environment.GetEnvironmentVariable("COREWF_CHECKOUT_ROOT")
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "uno-tools", "CoreWF"),
			"src", "OpenDevelop.AddIn", "WorkflowDesigner", "WorkflowDesigner.csproj");
		Assert.True(File.Exists(addinProject), "WorkflowDesigner project is missing: " + addinProject);
		var opened = await app.ReopenSolutionAsync(addinProject);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		var props = await app.InvokeAsync("od.project.properties", "WorkflowDesigner", "StartAction,StartArguments");
		Assert.True(props.GetProperty("success").GetBoolean(), props.ToString());
		var arguments = props.GetProperty("properties").GetProperty("StartArguments").GetString() ?? string.Empty;
		Assert.Contains("-addindir:", arguments);
		Assert.Contains("-devflow:" + AddinDevFlowPort, arguments);
		var started = await app.InvokeAsync("od.run-project");
		Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
		var child = new AddinHostClient(AddinDevFlowPort);
		var ready = await OpenDevelopAppFixture.PollUntilAsync(child.IsReadyAsync, TimeSpan.FromSeconds(120));
		Assert.True(ready, "The SDK-started WorkflowDesigner host never exposed its DevFlow endpoint.");
		return child;
	}

	[Fact]
	public async Task InstalledOpenDevelop_LoadsWorkflowDesignerAddin_AndOpensWorkflowXaml()
	{
		var child = await StartAddinHostAsync();
		try
		{
		var workflow = Path.Combine(Path.GetTempPath(), "opendevelop-corewf-" + Guid.NewGuid().ToString("N") + ".xaml");
		await File.WriteAllTextAsync(workflow, """
			<Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
			          xmlns:s="clr-namespace:System.Activities.Statements;assembly=System.Activities">
			  <s:Sequence DisplayName="Integration sequence">
			    <s:Delay Duration="00:00:00.2" />
			    <s:Delay Duration="00:00:00.2" />
			    <s:WriteLine Text="OpenDevelop CoreWF integration" />
			  </s:Sequence>
			</Activity>
			""", TestContext.Current.CancellationToken);
		try
		{
			var app = child;
			var opened = await app.InvokeAsync("od.open-file", workflow);
			Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
			var designer = await app.InvokeAsync("od.activate-secondary-view", workflow,
				"ICSharpCode.WorkflowDesigner.WorkflowDesignerViewContent");
			Assert.True(designer.GetProperty("success").GetBoolean(), designer.ToString());
			Assert.Equal("ICSharpCode.WorkflowDesigner.WorkflowDesignerViewContent", designer.GetProperty("typeName").GetString());
			var status = await app.InvokeAsync("od.workflow-designer.status");
			Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
			Assert.True(status.GetProperty("hostProcessId").GetInt32() > 0, status.ToString());
			Assert.True(status.GetProperty("elementCount").GetInt32() >= 2, status.ToString());

			var validation = await app.InvokeAsync("od.workflow-designer.validate");
			Assert.True(validation.GetProperty("success").GetBoolean(), validation.ToString());
			Assert.True(validation.GetProperty("isValid").GetBoolean(), validation.ToString());
			var run = await app.InvokeAsync("od.workflow-designer.run");
			Assert.True(run.GetProperty("success").GetBoolean(), run.ToString());
			Assert.True(run.GetProperty("trace").GetArrayLength() > 0, run.ToString());

			var leaf = await app.InvokeAsync("od.workflow-designer.select", "0");
			Assert.True(leaf.GetProperty("success").GetBoolean(), leaf.ToString());
			var breakpoint = await app.InvokeAsync("od.workflow-designer.breakpoint.toggle");
			Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
			Assert.Contains("0", breakpoint.GetProperty("breakpointElementIds").EnumerateArray().Select(id => id.GetString()));
			var started = await app.InvokeAsync("od.workflow-designer.debug.start");
			Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
			System.Text.Json.JsonElement debug = default;
			for (var attempt = 0; attempt < 30; attempt++) {
				debug = await app.InvokeAsync("od.workflow-designer.debug.state");
				Assert.True(debug.TryGetProperty("isPaused", out var paused), debug.ToString());
				if (paused.GetBoolean()) break;
				await Task.Delay(100, TestContext.Current.CancellationToken);
			}
			Assert.True(debug.GetProperty("isPaused").GetBoolean(), debug.ToString());
			Assert.Equal("0", debug.GetProperty("currentElementId").GetString());
			var callStack = debug.GetProperty("callStack").EnumerateArray().ToArray();
			Assert.Equal(2, callStack.Length);
			Assert.Equal("", callStack[0].GetProperty("elementId").GetString());
			Assert.Equal("0", callStack[1].GetProperty("elementId").GetString());
			Assert.Equal("Delay", callStack[1].GetProperty("typeName").GetString());
			Assert.Contains(debug.GetProperty("liveTrace").EnumerateArray().Select(entry => entry.GetString()), entry => entry?.Contains("Executing", StringComparison.Ordinal) == true);
			var stepped = await app.InvokeAsync("od.workflow-designer.debug.step");
			Assert.True(stepped.GetProperty("success").GetBoolean(), stepped.ToString());
			for (var attempt = 0; attempt < 30; attempt++) {
				debug = await app.InvokeAsync("od.workflow-designer.debug.state");
				if (debug.GetProperty("isPaused").GetBoolean() && debug.GetProperty("currentElementId").GetString() == "1") break;
				await Task.Delay(100, TestContext.Current.CancellationToken);
			}
			Assert.True(debug.GetProperty("isPaused").GetBoolean(), debug.ToString());
			Assert.Equal("1", debug.GetProperty("currentElementId").GetString());
			var continued = await app.InvokeAsync("od.workflow-designer.debug.continue");
			Assert.True(continued.GetProperty("success").GetBoolean(), continued.ToString());
			for (var attempt = 0; attempt < 30; attempt++) {
				debug = await app.InvokeAsync("od.workflow-designer.debug.state");
				if (debug.GetProperty("runCompleted").GetBoolean()) break;
				await Task.Delay(100, TestContext.Current.CancellationToken);
			}
			Assert.True(debug.GetProperty("runCompleted").GetBoolean(), debug.ToString());
			Assert.True(debug.GetProperty("succeeded").GetBoolean(), debug.ToString());
			var active = await app.InvokeAsync("od.active-view");
			Assert.Equal(workflow, active.GetProperty("fileName").GetString());
			Assert.Equal("ICSharpCode.WorkflowDesigner.WorkflowDesignerViewContent", active.GetProperty("typeName").GetString());

			// Exercise toolbox + history after the execution test has consumed the original
			// workflow. These calls remain against the same external addin child instance.
			var selected = await app.InvokeAsync("od.workflow-designer.select", "");
			Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
			var inserted = await app.InvokeAsync("od.workflow-designer.toolbox.insert", "WriteLine");
			Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
			var undo = await app.InvokeAsync("od.workflow-designer.undo");
			Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
			var redo = await app.InvokeAsync("od.workflow-designer.redo");
			Assert.True(redo.GetProperty("success").GetBoolean(), redo.ToString());

			// A generic activity carries its type argument in the toolbox item's type name, the
			// same form the type-argument prompt produces when one is dropped.
			var rootSelected = await app.InvokeAsync("od.workflow-designer.select", "");
			Assert.True(rootSelected.GetProperty("success").GetBoolean(), rootSelected.ToString());
			var beforeGeneric = (await app.InvokeAsync("od.workflow-designer.status")).GetProperty("elementCount").GetInt32();
			var genericInsert = await app.InvokeAsync("od.workflow-designer.toolbox.insert",
				"System.Activities.Statements.ForEach`1, System.Activities|System.String");
			Assert.True(genericInsert.GetProperty("success").GetBoolean(), genericInsert.ToString());
			Assert.True(genericInsert.GetProperty("elementCount").GetInt32() > beforeGeneric, genericInsert.ToString());
			var genericId = genericInsert.GetProperty("selectedElementId").GetString();
			Assert.False(string.IsNullOrEmpty(genericId), genericInsert.ToString());
			var genericStatus = await app.InvokeAsync("od.workflow-designer.status");
			Assert.Equal("ForEach`1", genericStatus.GetProperty("selectedElementType").GetString());

			// The workflow must still validate: a dropped generic loop is seeded with an empty
			// sequence of its own element type rather than a null required argument.
			var genericValidation = await app.InvokeAsync("od.workflow-designer.validate");
			Assert.True(genericValidation.GetProperty("isValid").GetBoolean(), genericValidation.ToString());

			// Moving an activity into another container keeps the subtree, so the element count
			// does not change.
			var movedInto = await app.InvokeAsync("od.workflow-designer.move-into", "0", genericId + ".$foreach-body");
			Assert.True(movedInto.GetProperty("success").GetBoolean(), movedInto.ToString());
			var refusedMove = await app.InvokeAsync("od.workflow-designer.move-into", genericId!, genericId!);
			Assert.False(refusedMove.GetProperty("success").GetBoolean(), refusedMove.ToString());

			var imports = await app.InvokeAsync("od.workflow-designer.imports");
			Assert.Contains("System", imports.GetProperty("imports").EnumerateArray().Select(entry => entry.GetString()));
		}
		finally
		{
			File.Delete(workflow);
		}
		}
		finally
		{
			child.Dispose();
			await app.InvokeAsync("od.stop-project");
		}
	}

	sealed class AddinHostClient : IDisposable
	{
		readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(240) };
		readonly string baseUrl;
		public AddinHostClient(int port) => baseUrl = $"http://localhost:{port}";
		public async Task<bool> IsReadyAsync()
		{
			try { using var response = await http.GetAsync(baseUrl + "/api/v1/agent/status"); return response.IsSuccessStatusCode; }
			catch (HttpRequestException) { return false; }
		}
		public async Task<JsonElement> InvokeAsync(string action, params object[] args)
		{
			using var content = new StringContent(JsonSerializer.Serialize(new { args }), Encoding.UTF8, "application/json");
			using var response = await http.PostAsync($"{baseUrl}/api/v1/invoke/actions/{action}", content);
			var body = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Action '{action}' failed ({(int)response.StatusCode}): {body}");
			using var envelope = JsonDocument.Parse(body);
			var raw = envelope.RootElement.GetProperty("returnValue").GetString();
			if (string.IsNullOrEmpty(raw)) throw new InvalidOperationException($"Action '{action}' returned no value: {body}");
			using var value = JsonDocument.Parse(raw);
			return value.RootElement.Clone();
		}
		public void Dispose() => http.Dispose();
	}
}
