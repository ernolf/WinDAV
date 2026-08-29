// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using WinDav.Core.Logging;
using Xunit;

namespace WinDav.Core.Tests;

public sealed class LogLevelTests
{
    [Theory]
    [InlineData("off", LogLevel.None)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    public void ALevelIsReadFromItsName(string name, LogLevel expected)
    {
        Assert.True(LogLevels.TryParse(name, out LogLevel level));
        Assert.Equal(expected, level);
    }

    // The name a level is asked for under is the name it is written under, and off is the one
    // that stands for no name in the file at all.
    [Fact]
    public void EveryLevelIsCalledWhatARecordCallsIt()
    {
        foreach (LogLevel level in LogLevels.All.Where(level => level != LogLevel.None))
        {
            Assert.Equal(LogFormat.Name(level), LogLevels.Name(level));
        }

        Assert.Equal(LogLevels.OffName, LogLevels.Name(LogLevel.None));
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData("Trace")]
    [InlineData("WARN")]
    public void TheNameIsReadInAnyCase(string name) => Assert.True(LogLevels.TryParse(name, out _));

    [Theory]
    [InlineData("fatal")]
    [InlineData("verbose")]
    [InlineData("warning")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameThatIsNoLevelIsNotOne(string? name)
    {
        Assert.False(LogLevels.TryParse(name, out LogLevel level));
        Assert.Equal(LogLevels.Default, level);
    }

    // Critical is written as error, so error is what a person asks for to get it. There is no
    // sixth name to know.
    [Fact]
    public void CriticalIsAskedForAsError()
    {
        Assert.True(LogLevels.TryParse(LogFormat.Name(LogLevel.Critical), out LogLevel level));
        Assert.Equal(LogLevel.Error, level);
    }

    [Fact]
    public void EveryLevelIsInTheListOnce() =>
        Assert.Equal(LogLevels.All.Count, LogLevels.All.Distinct().Count());
}
