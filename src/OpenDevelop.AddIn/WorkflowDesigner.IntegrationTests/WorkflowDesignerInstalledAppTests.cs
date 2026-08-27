using OpenDevelop.IntegrationTests;
using Xunit;

namespace CoreWf.OpenDevelop.AddIn.IntegrationTests;

[Collection(OpenDevelopInstalledAppCollection.Name)]
public sealed class WorkflowDesignerInstalledAppTests
{
	readonly OpenDevelopAppFixture app;

	public WorkflowDesignerInstalledAppTests(OpenDevelopAppFixture app) => this.app = app;

	[Fact]
	public async Task InstalledOpenDevelop_LoadsWorkflowDesignerAddin_AndOpensWorkflowXaml()
	{
		var appPath = Environment.GetEnvironmentVariable("OPENDEVELOP_APP_PATH")
			?? "/Applications/OpenDevelop.app/Contents/MacOS/OpenDevelop";
		var addin = Path.Combine(Path.GetDirectoryName(appPath)!, "AddIns", "DisplayBindings",
			"WorkflowDesigner", "WorkflowDesigner.addin");
		Assert.True(File.Exists(addin), "The installed OpenDevelop app does not contain the WorkflowDesigner addin: " + addin);

		var workflow = Path.Combine(Path.GetTempPath(), "opendevelop-corewf-" + Guid.NewGuid().ToString("N") + ".xaml");
		await File.WriteAllTextAsync(workflow, "<Activity xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\" />");
		try
		{
			var opened = await app.InvokeAsync("od.open-file", workflow);
			Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
			var active = await app.InvokeAsync("od.active-view");
			Assert.Equal(workflow, active.GetProperty("fileName").GetString());
		}
		finally
		{
			File.Delete(workflow);
		}
	}
}
