using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FolderAssistant.Tests;

public sealed class AgentConfigTests
{
	[Fact]
	public void Defaults_Apply_When_Nothing_Is_Configured()
	{
		AgentConfig config = BindFrom([]);

		config.Port.Should().Be(5000);
		config.ConnectionTimeoutSeconds.Should().Be(30);
		config.Provider.Type.Should().Be(ProviderConfig.AiProviderType.Azure);
		config.Provider.DeploymentName.Should().Be("gpt-chat");
		config.Persistence.MetadataFolderName.Should().Be(".folderassistant");
		config.Persistence.DatabaseFileName.Should().Be("manifest.db");
	}

	[Fact]
	public void Configured_Values_Override_The_Defaults()
	{
		AgentConfig config = BindFrom(new Dictionary<String, String?>
		{
			["FolderAssistant:Provider:Type"] = "OpenAI",
			["FolderAssistant:Provider:DeploymentName"] = "some-model",
			["FolderAssistant:ConnectionTimeoutSeconds"] = "45",
		});

		config.Provider.Type.Should().Be(ProviderConfig.AiProviderType.OpenAI);
		config.Provider.DeploymentName.Should().Be("some-model");
		config.ConnectionTimeout.Should().Be(TimeSpan.FromSeconds(45));
	}

	[Fact]
	public void An_Unset_Analyzed_Folder_Resolves_To_The_Current_Directory()
	{
		new AgentConfig().ResolveAnalyzedFolderPath().Should().Be(Directory.GetCurrentDirectory());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Analyzed_Folder_Is_Treated_As_Unset(String configured)
	{
		AgentConfig config = new() { Persistence = new PersistenceConfig { AnalyzedFolderPath = configured } };

		config.ResolveAnalyzedFolderPath().Should().Be(Directory.GetCurrentDirectory());
	}

	[Fact]
	public void A_Relative_Analyzed_Folder_Is_Resolved_To_An_Absolute_Path()
	{
		AgentConfig config = new() { Persistence = new PersistenceConfig { AnalyzedFolderPath = "sub/folder" } };

		config.ResolveAnalyzedFolderPath().Should().Be(Path.GetFullPath("sub/folder"));
	}

	private static AgentConfig BindFrom(Dictionary<String, String?> values)
	{
		AgentConfig config = new();

		new ConfigurationBuilder()
			.AddInMemoryCollection(values)
			.Build()
			.GetSection(AgentConfig.SectionName)
			.Bind(config);

		return config;
	}
}
