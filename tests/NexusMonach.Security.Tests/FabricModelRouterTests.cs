using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class FabricModelRouterTests
{
    [Theory]
    [InlineData((int)FabricWorkload.TextAnalysis, (int)FabricModelKind.QwenText)]
    [InlineData((int)FabricWorkload.PageTranslation, (int)FabricModelKind.OpusTranslation)]
    [InlineData((int)FabricWorkload.SpeechRecognition, (int)FabricModelKind.WhisperSpeech)]
    [InlineData((int)FabricWorkload.ImageUnderstanding, (int)FabricModelKind.SmolVlmVision)]
    [InlineData((int)FabricWorkload.SemanticEmbedding, (int)FabricModelKind.MultilingualE5)]
    public void Workload_IsRoutedToDedicatedLocalModel(int workloadValue, int expectedValue)
    {
        var workload = (FabricWorkload)workloadValue;
        var expected = (FabricModelKind)expectedValue;
        var route = FabricModelRouter.Route(workload);

        Assert.Equal(expected, route.Model);
        Assert.False(string.IsNullOrWhiteSpace(route.ModelId));
    }

    [Fact]
    public void UnknownWorkload_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FabricModelRouter.Route((FabricWorkload)int.MaxValue));
    }
}
