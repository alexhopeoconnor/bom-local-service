using BomLocalService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BomLocalService.Tests;

public class DebugPathSafetyTests
{
    [Fact]
    public void Replaced_debug_root_fails_closed_after_construction()
    {
        var root = Path.Combine(Path.GetTempPath(), "bom-debug-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "bom-debug-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var debugPath = Path.Combine(root, "debug");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CacheDirectory"] = root,
                    ["Debug:Enabled"] = "true",
                    ["Debug:WaitMs"] = "0"
                })
                .Build();
            var service = new DebugService(NullLogger<DebugService>.Instance, configuration);

            try
            {
                Directory.Delete(debugPath, recursive: true);
                Directory.CreateSymbolicLink(debugPath, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Equal(string.Empty, service.CreateRequestFolder("req"));
            Assert.True(File.Exists(sentinel));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.False(Directory.Exists(Path.Combine(outside, "req")));
        }
        finally
        {
            if (Directory.Exists(debugPath))
            {
                Directory.Delete(debugPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
