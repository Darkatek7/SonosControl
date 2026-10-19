using System.Text.Json;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

/// <summary>Payload shapes the real Python sidecar produces that once broke deserialization.</summary>
public class FlyDjContractCompatibilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void FullScopeModelWithASilentShuffledBrainHasNoRatio()
    {
        const string payload = """
            {"dataVersion":"flywire-783","scope":"full","status":"ready","buildProgress":1.0,"message":null,
             "neuronCount":138639,"connectionCount":15091983,"synapseCount":54492922,"populations":[],"attribution":[],
             "shuffleControl":{"realResponse":3.1,"shuffledResponse":0.0,"ratio":null,"trials":8}}
            """;

        var model = JsonSerializer.Deserialize<FlyDjModelInfo>(payload, Json)!;

        Assert.Null(model.ShuffleControl!.Ratio);
        Assert.Equal(15091983, model.ConnectionCount);
    }

    [Fact]
    public void AuditionAcceptsBioMsWithAFractionalPart()
    {
        const string payload = """
            {"results":[{"id":"st-1","ok":true,"error":null,"score":1.27,
              "features":{"low":0.4,"mid":0.3,"high":0.3,"loudness":0.6,"onsetRate":2.1,"centroidHz":1840.0},
              "populationRates":{"approach":3.0}}],
             "bioMs":500.0,"baseline":{"approach":0.0}}
            """;

        var response = JsonSerializer.Deserialize<FlyDjAuditionResponse>(payload, Json)!;

        Assert.Equal(500, response.BioMs);
        Assert.Equal(1.27, response.Results[0].Score);
    }
}
