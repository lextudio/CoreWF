using OpenDevelop.IntegrationTests;
using Xunit;

namespace CoreWf.OpenDevelop.AddIn.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenDevelopInstalledAppCollection : ICollectionFixture<OpenDevelopAppFixture>
{
	public const string Name = "OpenDevelop installed application";
}
