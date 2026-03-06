using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// Общая коллекция: один экземпляр WebApplicationFactory на все интеграционные тесты.
/// Снижает потребление памяти и предотвращает OOM при запуске тестов.
/// </summary>
[CollectionDefinition(nameof(WebAppCollection))]
public class WebAppCollection : ICollectionFixture<WebAppFactory>
{
}
