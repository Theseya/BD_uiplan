using Xunit;

namespace WebApplication1.Tests.Integration;

[CollectionDefinition(nameof(LiveWebAppCollection), DisableParallelization = true)]
public class LiveWebAppCollection : ICollectionFixture<LiveWebAppFactory>
{
}
