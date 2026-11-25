namespace Roster.Web.Components.Ui;

/// <summary>One day of a <see cref="DayBarsChart"/>.</summary>
public sealed record DayBarDatum
{
    public required string Key { get; init; }

    /// <summary>The day of the month, written under the bar.</summary>
    public required int Day { get; init; }

    /// <summary>The actual's height as a fraction of the scale, 0-1.</summary>
    public double Actual { get; init; }

    /// <summary>The forecast's height as a fraction of the scale, 0-1.</summary>
    public double Forecast { get; init; }

    public bool Weekend { get; init; }
    public bool Today { get; init; }

    /// <summary>What a screen reader says for the day.</summary>
    public required string Label { get; init; }

    /// <summary>The tooltip: the day, then a line per figure.</summary>
    public IReadOnlyList<string> Tip { get; init; } = [];
}

/// <summary>A gridline of a chart: where it sits as a fraction of the scale, and its label.</summary>
public sealed record ChartTick(double At, string Label);
