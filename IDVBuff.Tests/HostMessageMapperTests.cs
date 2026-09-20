using IDVBuff.Features.Maps;
using IDVBuff.Features.Plugins;
using IDVBuff.PluginHostMessages;
using Xunit;

namespace IDVBuff.Tests;

public class HostMessageMapperTests
{
    private static readonly Guid MapId = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    private static readonly Guid MatchId = new("5d42f9d0-8c1a-4a2e-b8e0-7a1d3c1f4b5a");
    private static readonly Guid SurveyId = new("9a7f3c2e-6d1b-4f5a-9e3c-2b8a1d0c4e6f");

    private static MapSimilarityTransform ValidTransform() =>
        new()
        {
            Scale = 1.25,
            RotationDegrees = 2.5,
            TranslationX = 100d,
            TranslationY = 200d
        };

    private static MapSessionSnapshot LockedSessionSnapshot() =>
        new()
        {
            Version = 7,
            AlignmentRevision = 5,
            MapId = MapId,
            Floor = "1F",
            State = MapSessionState.Locked,
            LocationMethod = MapLocationMethod.DualAnchor,
            RecalibrationReason = MapRecalibrationReason.None,
            LockedTransform = ValidTransform(),
            Confidence = 0.95,
            StableCandidateFrames = 12,
            Detail = "test"
        };

    private static MapMatchSnapshot StartedMatchSnapshot() =>
        new(
            MapMatchState.Started,
            PlayerSlot.Player1,
            3,
            MapClass: "S1",
            MatchId,
            MapRunMode.Normal,
            SurveyId,
            "1F");

    [Fact]
    public void ToSessionStateChanged_MapsFieldsAndStringifiesEnums()
    {
        var session = LockedSessionSnapshot();
        var match = StartedMatchSnapshot();

        var message = HostMessageMapper.ToSessionStateChanged(
            session,
            match,
            "status",
            overlayVisible: true,
            gameMapOpen: true,
            MapAlignmentTrackingMode.GatePairLocked);

        Assert.Equal("Locked", message.SessionState);
        Assert.Equal(MapId.ToString(), message.MapId);
        Assert.Equal("1F", message.Floor);
        Assert.True(message.IsLocked);
        Assert.Equal(0.95, message.Confidence);
        Assert.Equal(12, message.StableCandidateFrames);
        Assert.Equal("DualAnchor", message.LocationMethod);
        Assert.Equal("None", message.RecalibrationReason);
        Assert.Equal("status", message.StatusMessage);
        Assert.True(message.OverlayVisible);
        Assert.True(message.GameMapOpen);
        Assert.Equal("GatePairLocked", message.AlignmentMode);
        Assert.Equal(5L, message.AlignmentRevision);
    }

    [Fact]
    public void ToMatchStateChanged_MapsFieldsAndStringifiesEnums()
    {
        var message = HostMessageMapper.ToMatchStateChanged(StartedMatchSnapshot());

        Assert.Equal("Started", message.State);
        Assert.Equal("Player1", message.PlayerSlot);
        Assert.Equal(3, message.Version);
        Assert.Equal("S1", message.MapClass);
        Assert.Equal(MatchId.ToString(), message.MatchId);
        Assert.Equal("Normal", message.Mode);
        Assert.Equal(SurveyId.ToString(), message.SurveyProjectId);
        Assert.Equal("1F", message.FloorKey);
    }

    [Fact]
    public void ToMatchStateChanged_EmptyMatchId_IsNull()
    {
        var ended = new MapMatchSnapshot(MapMatchState.Ended, null, 0);

        var message = HostMessageMapper.ToMatchStateChanged(ended);

        Assert.Equal("Ended", message.State);
        Assert.Null(message.PlayerSlot);
        Assert.Null(message.MatchId);
        Assert.Null(message.SurveyProjectId);
    }

    [Fact]
    public void TryToMapLocked_FirstLocked_Publishes()
    {
        var session = LockedSessionSnapshot();
        long last = 0;

        var message = HostMessageMapper.TryToMapLocked(
            session,
            session.LockedTransform,
            ref last);

        Assert.NotNull(message);
        Assert.Equal(MapId.ToString(), message!.MapId);
        Assert.Equal("1F", message.Floor);
        Assert.Equal(1.25, message.Scale);
        Assert.Equal(2.5, message.RotationDegrees);
        Assert.Equal(100d, message.TranslationX);
        Assert.Equal(200d, message.TranslationY);
        Assert.Equal(0.95, message.Confidence);
        Assert.Equal(5L, last);
    }

    [Fact]
    public void TryToMapLocked_SameRevision_ReturnsNull()
    {
        var session = LockedSessionSnapshot();
        long last = 0;
        _ = HostMessageMapper.TryToMapLocked(session, session.LockedTransform, ref last);

        var again = HostMessageMapper.TryToMapLocked(session, session.LockedTransform, ref last);

        Assert.Null(again);
    }

    [Fact]
    public void TryToMapLocked_InvalidTransform_ReturnsNull()
    {
        var invalid = new MapSimilarityTransform { Scale = double.NaN };
        var session = new MapSessionSnapshot
        {
            State = MapSessionState.Locked,
            LockedTransform = invalid,
            AlignmentRevision = 1
        };
        long last = 0;

        var message = HostMessageMapper.TryToMapLocked(session, invalid, ref last);

        Assert.Null(message);
    }

    [Fact]
    public void TryToMapLocked_NotLocked_ReturnsNull()
    {
        var session = new MapSessionSnapshot
        {
            State = MapSessionState.IdentifyingMap,
            AlignmentRevision = 1
        };
        long last = 0;

        var message = HostMessageMapper.TryToMapLocked(
            session,
            ValidTransform(),
            ref last);

        Assert.Null(message);
    }
}
