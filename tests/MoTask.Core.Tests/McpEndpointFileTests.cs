using System.IO;
using FluentAssertions;
using MoTask.Core;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Core.Tests;

public class McpEndpointFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    public McpEndpointFileTests() => Directory.CreateDirectory(_dir);

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Write_ThenTryRead_RoundTrips()
    {
        var path = Path_("endpoint.json");
        McpEndpointFile.Write(path, new McpEndpoint("http://127.0.0.1:52341/mcp", "3F2A", 12345));

        var read = McpEndpointFile.TryRead(path);

        read.Should().Be(new McpEndpoint("http://127.0.0.1:52341/mcp", "3F2A", 12345));
    }

    [Fact]
    public void Write_UsesLowerCamelCaseProperties()
    {
        var path = Path_("endpoint.json");
        McpEndpointFile.Write(path, new McpEndpoint("http://127.0.0.1:1/mcp", "T", 7));

        var json = File.ReadAllText(path);

        json.Should().Contain("\"url\"").And.Contain("\"token\"").And.Contain("\"pid\"");
    }

    [Fact]
    public void Write_CreatesTheDirectory()
    {
        var path = Path.Combine(_dir, "nested", "endpoint.json");

        McpEndpointFile.Write(path, new McpEndpoint("http://127.0.0.1:1/mcp", "T", 7));

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
        => McpEndpointFile.TryRead(Path_("nope.json")).Should().BeNull();

    [Fact]
    public void TryRead_BrokenJson_ReturnsNull()
    {
        var path = Path_("broken.json");
        File.WriteAllText(path, "{ not json");

        McpEndpointFile.TryRead(path).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"token":"T","pid":1}""")]
    [InlineData("""{"url":"","token":"T","pid":1}""")]
    [InlineData("""{"url":"http://127.0.0.1:1/mcp","pid":1}""")]
    [InlineData("""{"url":"http://127.0.0.1:1/mcp","token":"T"}""")]
    public void TryRead_MissingField_ReturnsNull(string json)
    {
        var path = Path_("partial.json");
        File.WriteAllText(path, json);

        McpEndpointFile.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void Delete_RemovesTheFile_AndIsSafeTwice()
    {
        var path = Path_("endpoint.json");
        McpEndpointFile.Write(path, new McpEndpoint("http://127.0.0.1:1/mcp", "T", 7));

        McpEndpointFile.Delete(path);
        McpEndpointFile.Delete(path);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void AppPaths_EndpointFile_SitsNextToTheDatabase()
    {
        AppPaths.EndpointFile.Should().Be(Path.Combine(AppPaths.DataDirectory, "endpoint.json"));
        AppPaths.DataDirectory.Should().EndWith(Path.Combine("Local", "MoTask"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
