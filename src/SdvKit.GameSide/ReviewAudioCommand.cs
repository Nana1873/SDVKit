using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;

namespace SdvKit.GameSide;

internal sealed class StardewReviewAudioSource : IReviewAudioSource
{
    private const string AudioChangesAsset = "Data/AudioChanges";
    private const string JukeboxTracksAsset = "Data/JukeboxTracks";
    private readonly IModHelper _helper;

    public StardewReviewAudioSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<ReviewAudioChangeDefinition> LoadAudioChanges()
    {
        IDictionary<string, AudioCueData> values =
            _helper.GameContent.Load<Dictionary<string, AudioCueData>>(
                AudioChangesAsset);
        if (values is null
            || values.Count > ReviewAudioContract.MaximumAudioChangeEntries)
        {
            throw new InvalidDataException(
                "Data/AudioChanges exceeds the bounded source population.");
        }

        var definitions = new List<ReviewAudioChangeDefinition>(values.Count);
        foreach (KeyValuePair<string, AudioCueData> pair in values)
        {
            definitions.Add(new ReviewAudioChangeDefinition(
                pair.Key,
                pair.Value?.Id!,
                pair.Value?.FilePaths?.Count,
                pair.Value?.Category,
                pair.Value?.StreamedVorbis ?? false,
                pair.Value?.Looped ?? false,
                pair.Value?.UseReverb ?? false));
        }

        return definitions;
    }

    public IReadOnlyList<ReviewAudioJukeboxDefinition> LoadJukeboxTracks()
    {
        IDictionary<string, JukeboxTrackData> values =
            _helper.GameContent.Load<Dictionary<string, JukeboxTrackData>>(
                JukeboxTracksAsset);
        if (values is null
            || values.Count > ReviewAudioContract.MaximumJukeboxTrackEntries)
        {
            throw new InvalidDataException(
                "Data/JukeboxTracks exceeds the bounded source population.");
        }

        var definitions = new List<ReviewAudioJukeboxDefinition>(values.Count);
        var alternativeReferences = 0;
        foreach (KeyValuePair<string, JukeboxTrackData> pair in values)
        {
            JukeboxTrackData data = pair.Value
                ?? throw new InvalidDataException(
                    "Data/JukeboxTracks contains a null entry model.");
            List<string>? alternatives = data.AlternativeTrackIds;
            if (alternatives is not null)
            {
                if (alternatives.Count > ReviewAudioContract.MaximumAlternativesPerTrack
                    || (long)alternativeReferences + alternatives.Count
                        > ReviewAudioContract.MaximumAlternativeReferences)
                {
                    throw new InvalidDataException(
                        "Data/JukeboxTracks exceeds the bounded alternative-reference population.");
                }

                alternativeReferences += alternatives.Count;
            }

            definitions.Add(new ReviewAudioJukeboxDefinition(
                pair.Key,
                alternatives?.Select(value => (string?)value).ToArray()));
        }

        return definitions;
    }

    public ReviewAudioSoundBankStatus GetSoundBankStatus()
    {
        ISoundBank? soundBank = Game1.soundBank;
        if (soundBank is null)
        {
            return ReviewAudioSoundBankStatus.Unavailable;
        }
        if (soundBank is DummySoundBank)
        {
            return ReviewAudioSoundBankStatus.Dummy;
        }

        return soundBank.IsDisposed
            ? ReviewAudioSoundBankStatus.Disposed
            : ReviewAudioSoundBankStatus.Ready;
    }

    public ReviewAudioCueProbe ProbeCue(string cueId)
    {
        if (!ReviewAudioValidation.IsSafeCueId(cueId))
        {
            throw new ArgumentException("The cue ID is invalid.", nameof(cueId));
        }

        ISoundBank soundBank = Game1.soundBank
            ?? throw new InvalidOperationException("The active soundbank is unavailable.");
        if (soundBank is DummySoundBank || soundBank.IsDisposed)
        {
            throw new InvalidOperationException("The active soundbank cannot be queried.");
        }

        bool exists = soundBank.Exists(cueId);
        if (!exists)
        {
            return new ReviewAudioCueProbe(
                cueId,
                false,
                false,
                null,
                null);
        }

        CueDefinition? definition = soundBank.GetCueDefinition(cueId);
        return new ReviewAudioCueProbe(
            cueId,
            true,
            definition is not null,
            definition?.name,
            definition?.sounds?.Count);
    }
}

internal static class ReviewAudioCommand
{
    public static void Handle(
        string[] arguments,
        IReviewAudioSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-audio runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-audio rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewAudioReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewAudioOperation.Failure(
                operation,
                source,
                new ReviewAudioProblem(
                    "audioReviewTopologyUnsupported",
                    "Review-audio queries require an active owned single project review."));
        }
        else if (!ReviewAudioArguments.TryParse(
                arguments,
                out ReviewAudioQuery? query,
                out ReviewAudioProblem? problem))
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewAudioOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewAudioOperation.Execute(query!, source);
            }
            catch (Exception exception) when (!ReviewException.IsFatal(exception))
            {
                report = ReviewAudioOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewAudioProblem(
                        "audioQueryFailed",
                        $"The review-audio query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewAudioResponseEnvelope(
            ReviewAudioContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            report = ReviewAudioResponsePublisher.Write(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-audio completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            monitor.Log(
                $"SDVKit review-audio could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

}
