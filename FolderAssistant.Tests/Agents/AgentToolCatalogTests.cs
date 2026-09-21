using FluentAssertions;
using FolderAssistant.Agents;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>The catalog (SPEC-100): every tool by name, narrowed to an allowlist in its order, and a name it does not hold refused.</summary>
public sealed class AgentToolCatalogTests
{
	private static AITool Tool(String name) => AIFunctionFactory.Create(() => name, name);

	[Fact]
	public void Selects_The_Named_Tools_In_The_Order_Named_And_None_For_None()
	{
		AgentToolCatalog catalog = new([Tool("A"), Tool("B"), Tool("C")]);

		catalog.Names.Should().Equal("A", "B", "C");
		catalog.Select(["C", "A"]).Select(tool => tool.Name).Should().Equal("C", "A");
		catalog.Select([]).Should().BeEmpty();
	}

	[Fact]
	public void An_Unknown_Name_And_A_Repeated_Tool_Are_Refused()
	{
		AgentToolCatalog catalog = new([Tool("A")]);

		Action unknown = () => catalog.Select(["a"]);
		Action repeated = () => new AgentToolCatalog([Tool("A"), Tool("A")]);

		unknown.Should().Throw<InvalidOperationException>().WithMessage("*No tool is named 'a'*A*");
		repeated.Should().Throw<InvalidOperationException>().WithMessage("*share one name: A*");
	}
}
