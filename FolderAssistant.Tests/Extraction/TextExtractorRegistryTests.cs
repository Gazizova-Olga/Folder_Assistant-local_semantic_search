using System.Text;
using FluentAssertions;
using FolderAssistant.Extraction;

namespace FolderAssistant.Tests.Extraction;

/// <summary>
/// The extraction registry (SPEC-120): one source of which extensions are read, which extractor
/// reads each, and which formats scan as raw lines — asserted directly, because a second list
/// drifting from this one would look exactly like a file that was never saved.
/// </summary>
public sealed class TextExtractorRegistryTests
{
	[Fact]
	public void The_Default_Registry_Reads_Plain_Text_And_Nothing_Else()
	{
		TextExtractorRegistry registry = TextExtractorRegistry.Default;

		registry.IsSupported(".md").Should().BeTrue();
		registry.IsSupported(".MD").Should().BeTrue();
		registry.IsSupported(".cs").Should().BeTrue();
		registry.IsSupported(".png").Should().BeFalse();
		registry.IsSupported(".docx").Should().BeFalse();
		registry.IsSupported("").Should().BeFalse();
		registry.IsSupported(null).Should().BeFalse();
		registry.Find(".md").Should().BeOfType<PlainTextExtractor>();
		registry.Extensions.Should().Contain([".txt", ".md", ".json", ".cs"]).And.NotContain([".docx", ".pdf"]);
	}

	[Fact]
	public void Raw_Line_Scanning_Is_The_Extractors_Answer_And_False_For_The_Unknown()
	{
		TextExtractorRegistry registry = new(new PlainTextExtractor(), new ContainerFormat(".fake"));

		registry.SupportsRawLineScanning(".md").Should().BeTrue();
		registry.SupportsRawLineScanning(".fake").Should().BeFalse();
		registry.SupportsRawLineScanning(".png").Should().BeFalse();
		registry.SupportsRawLineScanning(null).Should().BeFalse();
	}

	[Fact]
	public void An_Extension_Claimed_Twice_Is_Refused_At_Construction()
	{
		Action twice = () => _ = new TextExtractorRegistry(new PlainTextExtractor(), new ContainerFormat(".md"));
		Action noDot = () => _ = new TextExtractorRegistry(new ContainerFormat("md"));

		twice.Should().Throw<ArgumentException>().WithMessage("*'.md'*claimed by both*");
		noDot.Should().Throw<ArgumentException>().WithMessage("*must start with a dot*");
	}

	[Fact]
	public void Require_Throws_For_An_Extension_Nothing_Reads()
	{
		Action act = () => TextExtractorRegistry.Default.Require(".png");

		act.Should().Throw<NotSupportedException>();
	}

	[Fact]
	public void Plain_Text_Is_Decoded_As_The_Readers_Decode_It()
	{
		PlainTextExtractor extractor = new();
		Byte[] withMark = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("first\nsecond")];

		extractor.Extract(Encoding.UTF8.GetBytes("plain")).Should().Be("plain");
		extractor.Extract(withMark).Should().Be("first\nsecond");
		extractor.SupportsRawLineScanning.Should().BeTrue();
	}

	private sealed class ContainerFormat(String extension) : ITextExtractor
	{
		public IReadOnlyCollection<String> Extensions { get; } = [extension];

		public Boolean SupportsRawLineScanning => false;

		public String Extract(Byte[] bytes) => "extracted";
	}
}
