namespace Waddamburo.Formats.Tests;

public sealed class ParserLimitsTests
{
    [Fact]
    public void DefaultDefinesEveryPhaseThreeResourceCeiling()
    {
        var limits = ParserLimits.Default;

        Assert.True(limits.MaxFileBytes > 0);
        Assert.True(limits.MaxAllocationBytes > 0);
        Assert.True(limits.MaxRecordCount > 0);
        Assert.True(limits.MaxStringBytes > 0);
        Assert.True(limits.MaxStringCount > 0);
        Assert.True(limits.MaxTextureDimension > 0);
        Assert.True(limits.MaxTextureBytes > 0);
        Assert.True(limits.MaxActionBytes > 0);
        Assert.True(limits.MaxVertices > 0);
        Assert.True(limits.MaxIndices > 0);
        Assert.True(limits.MaxBones > 0);
        Assert.True(limits.MaxFrames > 0);
        Assert.True(limits.MaxMeasures > 0);
        Assert.True(limits.MaxNotes > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimitsAreRejected(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserLimits(maxRecordCount: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserLimits(maxFileBytes: invalid));
    }
}
