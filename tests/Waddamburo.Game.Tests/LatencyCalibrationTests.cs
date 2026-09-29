using Waddamburo.Game.Flow;

namespace Waddamburo.Game.Tests;

public sealed class LatencyCalibrationTests
{
    [Fact]
    public void NudgesFromTheSavedOffsetUntilConfirmed()
    {
        var calibration = new LatencyCalibration(TimeSpan.Zero, audioOffsetMs: 10);
        for (var step = 0; step < 5; step++)
            calibration.Nudge(-1);
        calibration.Confirm();
        Assert.True(calibration.Done);
        Assert.Equal(5, calibration.AudioOffsetMs);
        calibration.Nudge(100); // done: no more changes
        Assert.Equal(5, calibration.AudioOffsetMs);
    }

    [Fact]
    public void OffsetStaysInTheSettingsRange()
    {
        var calibration = new LatencyCalibration(TimeSpan.Zero, audioOffsetMs: 495);
        calibration.Nudge(10);
        Assert.Equal(500, calibration.AudioOffsetMs);
    }

    [Fact]
    public void FirstClickIsOnABeatAfterTheLeadIn()
    {
        var calibration = new LatencyCalibration(TimeSpan.FromMilliseconds(100));
        Assert.True(calibration.FirstClick >= TimeSpan.FromMilliseconds(100) + LatencyCalibration.LeadIn);
        Assert.Equal(0, calibration.FirstClick.Ticks % LatencyCalibration.Beat.Ticks);
    }

    [Fact]
    public void LastNoteByFindsTheChartNoteReached()
    {
        var chart = LatencyCalibration.Chart();
        Assert.Equal(6, LatencyCalibration.LastNoteBy(chart.HitObjects[7].StartTime - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(7, LatencyCalibration.LastNoteBy(chart.HitObjects[7].StartTime));
        Assert.Equal(-1, LatencyCalibration.LastNoteBy(TimeSpan.FromMilliseconds(599)));
        Assert.All(chart.HitObjects, note => Assert.Equal(0, note.StartTime.Ticks % LatencyCalibration.Beat.Ticks));
    }
}
