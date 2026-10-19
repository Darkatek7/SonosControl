namespace SonosControl.Web.Services.FlyDj;

// Wire contracts for the FlyDJ sidecar API. See flydj/API.md; JSON is camelCase.

public sealed record FlyDjPopulation(int Id, string Key, string Label, string Role, int NeuronCount);

public sealed record FlyDjAttribution(string Text, string? Url);

// Ratio is null when the shuffled brain stays silent (division by zero), e.g. for the full connectome.
public sealed record FlyDjShuffleControl(double RealResponse, double ShuffledResponse, double? Ratio, int Trials);

public sealed record FlyDjModelInfo(
    string DataVersion,
    string Scope,
    string Status,
    double BuildProgress,
    string? Message,
    int NeuronCount,
    long ConnectionCount,
    long SynapseCount,
    IReadOnlyList<FlyDjPopulation> Populations,
    IReadOnlyList<FlyDjAttribution> Attribution,
    FlyDjShuffleControl? ShuffleControl);

public sealed record FlyDjAudioFeatures(double Low, double Mid, double High, double Loudness, double OnsetRate, double CentroidHz);

public sealed record FlyDjSimulationState(
    string Mode,
    bool Running,
    int Viewers,
    double BioTimeMs,
    double RealtimeFactor,
    double CpuMsPerBioSecond,
    double SpikesPerSecond,
    double? Engagement,
    IReadOnlyDictionary<string, double> PopulationRates,
    string? ListeningUrl,
    FlyDjAudioFeatures? LastFeatures,
    string ModelStatus);

public sealed record FlyDjAuditionCandidate(string Id, string Url);

public sealed record FlyDjAuditionRequest(IReadOnlyList<FlyDjAuditionCandidate> Candidates, int BioMs = 500);

public sealed record FlyDjAuditionResult(
    string Id,
    bool Ok,
    string? Error,
    double? Score,
    FlyDjAudioFeatures? Features,
    IReadOnlyDictionary<string, double>? PopulationRates);

public sealed record FlyDjAuditionResponse(
    IReadOnlyList<FlyDjAuditionResult> Results,
    double BioMs,
    IReadOnlyDictionary<string, double> Baseline);

public sealed record FlyDjListenRequest(string? Url);

public sealed record FlyDjListenResponse(
    bool Ok,
    string? Error,
    double? Engagement,
    FlyDjAudioFeatures? Features,
    IReadOnlyDictionary<string, double>? PopulationRates);
