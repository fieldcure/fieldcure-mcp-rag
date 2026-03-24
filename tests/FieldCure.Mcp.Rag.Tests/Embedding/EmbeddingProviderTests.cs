using FieldCure.Mcp.Rag.Embedding;

namespace FieldCure.Mcp.Rag.Tests.Embedding;

[TestClass]
public class EmbeddingProviderTests
{
    [TestMethod]
    public async Task NullEmbeddingProvider_ReturnsEmptyArrays()
    {
        var provider = new NullEmbeddingProvider();

        Assert.AreEqual(0, provider.Dimension);
        Assert.AreEqual("null", provider.ModelId);

        var single = await provider.EmbedAsync("test");
        Assert.AreEqual(0, single.Length);

        var batch = await provider.EmbedBatchAsync(new[] { "a", "b" });
        Assert.AreEqual(2, batch.Length);
        Assert.AreEqual(0, batch[0].Length);
        Assert.AreEqual(0, batch[1].Length);
    }

    [TestMethod]
    public void EmbeddingProviderFactory_CreatesProvider()
    {
        // With default environment variables (no EMBEDDING_* set),
        // factory should still create a provider without throwing
        var provider = EmbeddingProviderFactory.CreateFromEnvironment();

        Assert.IsNotNull(provider);
        Assert.IsInstanceOfType(provider, typeof(OpenAiCompatibleEmbeddingProvider));
        Assert.AreEqual("nomic-embed-text", provider.ModelId);
    }
}
