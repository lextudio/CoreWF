using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenDevelop.IntegrationTests;
using Xunit;

namespace CoreWf.OpenDevelop.AddIn.IntegrationTests;

/// <summary>
/// The release package, end to end: the built <c>WorkflowDesigner.odaddin</c> is served by a fake GitHub, an
/// isolated OpenDevelop trusts the repository and installs it from the AddIn Manager, is restarted, and the
/// installed add-in must then load and open a workflow in the designer.
/// Needs COREWF_ODADDIN (the package built with PackOdAddin) and OD_IT_IDE_DLL (OpenDevelop.dll of the host
/// under test); skipped otherwise.
/// </summary>
public sealed class WorkflowDesignerOdaddinInstallTests
{
	const string Repository = "lextudio/CoreWF";
	const string Identity = "ICSharpCode.WorkflowDesigner";

	[Fact]
	public async Task GitHubReleasePackage_InstallsRestartsLoadsAndOpensAWorkflow()
	{
		string? package = Environment.GetEnvironmentVariable("COREWF_ODADDIN");
		Assert.SkipUnless(!string.IsNullOrEmpty(package) && File.Exists(package) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OD_IT_IDE_DLL")),
			"Set COREWF_ODADDIN (built .odaddin) and OD_IT_IDE_DLL (host OpenDevelop.dll).");

		byte[] bytes = await File.ReadAllBytesAsync(package!);
		string work = Path.Combine(Path.GetTempPath(), "od-it-corewf-" + Guid.NewGuid().ToString("N"));
		string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
		using var github = new FakeGitHub(bytes);
		try
		{
			var environment = new Dictionary<string, string?>
			{
				["OD_TEST_USER_ADDINS"] = "1",
				["OD_ADDIN_GITHUB_API"] = github.Base.ToString(),
				["OD_ADDIN_GITHUB_DOWNLOAD"] = github.Base.ToString(),
			};

			// First run: trust the repository and install the release from the AddIn Manager.
			using (var first = await IsolatedIde.StartAsync(config, environment))
			{
				Assert.True((await first.InvokeAsync("od.addin-manager.show")).GetProperty("success").GetBoolean());
				await first.InvokeAsync("od.test.queue-dialog-answer", "question", "[\"yes\"]");
				Assert.True((await first.InvokeAsync("od.addin-manager.trust", Repository)).GetProperty("success").GetBoolean());
				await first.InvokeAsync("od.addin-manager.refresh");
				var state = await WaitAsync(first, s => Entries(s, "AvailableAddInsViewModel").Any(), "the Workflow Designer to be listed");
				string name = Entries(state, "AvailableAddInsViewModel").Single().GetProperty("name").GetString()!;
				Assert.True((await first.InvokeAsync("od.addin-manager.install", "Available", name)).GetProperty("success").GetBoolean());
				await WaitAsync(first, s => Entries(s, "InstalledAddInsViewModel").Any(e => e.GetProperty("id").GetString() == Identity), "the add-in to be installed");
			}

			// Second run, same configuration: the add-in is part of the tree and works.
			using (var second = await IsolatedIde.StartAsync(config, environment))
			{
				var addIns = await second.InvokeAsync("od.addins");
				var loaded = addIns.GetProperty("addins").EnumerateArray()
					.FirstOrDefault(a => a.GetProperty("fileName").GetString()?.Contains("WorkflowDesigner", StringComparison.OrdinalIgnoreCase) == true);
				Assert.True(loaded.ValueKind == JsonValueKind.Object, "The installed add-in is not in the add-in tree: " + addIns);
				Assert.True(loaded.GetProperty("enabled").GetBoolean(), loaded.ToString());

				string workflow = Path.Combine(work, "flow.xaml");
				await File.WriteAllTextAsync(workflow, """
					<Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
					          xmlns:s="clr-namespace:System.Activities.Statements;assembly=System.Activities">
					  <s:Sequence DisplayName="Installed sequence">
					    <s:Delay Duration="00:00:00.2" />
					    <s:WriteLine Text="installed from a .odaddin" />
					  </s:Sequence>
					</Activity>
					""");
				Assert.True((await second.InvokeAsync("od.open-file", workflow)).GetProperty("opened").GetBoolean());
				var designer = await second.InvokeAsync("od.activate-secondary-view", workflow, "ICSharpCode.WorkflowDesigner.WorkflowDesignerViewContent");
				Assert.True(designer.GetProperty("success").GetBoolean(), designer.ToString());
				var status = await second.InvokeAsync("od.workflow-designer.status");
				Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
				Assert.True(status.GetProperty("elementCount").GetInt32() >= 2, status.ToString());
			}
		}
		finally
		{
			try { Directory.Delete(work, true); } catch (IOException) { }
		}
	}

	static IEnumerable<JsonElement> Entries(JsonElement state, string listType) =>
		state.GetProperty("lists").EnumerateArray()
			.Single(l => l.GetProperty("type").GetString() == listType)
			.GetProperty("entries").EnumerateArray();

	static async Task<JsonElement> WaitAsync(IsolatedIde ide, Func<JsonElement, bool> condition, string what)
	{
		JsonElement last = default;
		bool ok = await OpenDevelopAppFixture.PollUntilAsync(async () =>
		{
			last = await ide.InvokeAsync("od.addin-manager.state");
			return condition(last);
		}, TimeSpan.FromSeconds(90));
		Assert.True(ok, $"Timed out waiting for {what}: {last}\n{ide.Output}");
		return last;
	}

	/// <summary>api.github.com and the asset host in one loopback server, serving one release of the real package.</summary>
	sealed class FakeGitHub : IDisposable
	{
		readonly HttpListener listener = new();
		readonly byte[] package;
		public Uri Base { get; }

		public FakeGitHub(byte[] package)
		{
			this.package = package;
			int port;
			using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
			{
				probe.Start();
				port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			Base = new Uri($"http://127.0.0.1:{port}/");
			listener.Prefixes.Add(Base.ToString());
			listener.Start();
			_ = Task.Run(Serve);
		}

		async Task Serve()
		{
			string assetPath = $"/{Repository}/releases/download/v23.10.5/WorkflowDesigner.odaddin";
			while (listener.IsListening)
			{
				HttpListenerContext context;
				try { context = await listener.GetContextAsync(); }
				catch (Exception) { return; }
				try
				{
					string path = context.Request.Url!.AbsolutePath;
					if (path == $"/repos/{Repository}/releases")
					{
						context.Response.ContentType = "application/json";
						context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
						{
							new Dictionary<string, object?>
							{
								["tag_name"] = "v23.10.5", ["prerelease"] = false, ["draft"] = false,
								["body"] = "test release", ["published_at"] = "2026-11-17T10:00:00Z",
								["assets"] = new[]
								{
									new Dictionary<string, object?>
									{
										["name"] = "WorkflowDesigner.odaddin", ["state"] = "uploaded", ["size"] = package.Length,
										["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
										["browser_download_url"] = new Uri(Base, assetPath.TrimStart('/')).ToString(),
									},
								},
							},
						})));
					}
					else if (path == assetPath)
						context.Response.OutputStream.Write(package);
					else
						context.Response.StatusCode = 404;
				}
				catch (Exception) { context.Response.StatusCode = 500; }
				finally { context.Response.Close(); }
			}
		}

		public void Dispose() => listener.Close();
	}
}
