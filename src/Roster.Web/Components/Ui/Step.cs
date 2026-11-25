namespace Roster.Web.Components.Ui;

public enum StepState { Done, Current, Attention, Todo }

/// <summary>One step of a <see cref="StepTrack"/>: what it is and, under it, when it happened or what it waits for.</summary>
public sealed record Step(string Key, string Title, string Detail, StepState State);
