using System.Text.Json;

namespace Roster.Web.Components.Ui;

/// <summary>What a <see cref="SaveScope"/> holds for each form it saves.</summary>
public interface ISaveSlot
{
    /// <summary>Saves the form; false when its action refused it (the form shows why).</summary>
    Task<bool> SaveAsync();

    /// <summary>Puts the form back to what is saved.</summary>
    Task DiscardAsync();
}

/// <summary>
/// A form's values against what is saved (useFormDraft). <see cref="Value"/> is
/// what is on screen, <see cref="Base"/> what is saved. Give it the saved values
/// again whenever the page reloads them (<see cref="Sync"/>): a form with no
/// edits takes the new saved values; one with edits keeps them. Values are
/// compared by content.
/// </summary>
public sealed class FormDraft<T>
{
    private T _saved;

    public FormDraft(T saved)
    {
        _saved = saved;
        Base = saved;
        Value = saved;
    }

    public T Value { get; private set; }

    public T Base { get; private set; }

    /// <summary>True while the form holds an edit.</summary>
    public bool IsDirty => !SaveBar.Same(Value, Base);

    /// <summary>The latest saved values, from the page (call it from OnParametersSet).</summary>
    public void Sync(T saved)
    {
        if (SaveBar.Same(saved, _saved)) return;
        var clean = SaveBar.Same(Value, Base);
        _saved = saved;
        if (clean)
        {
            Base = saved;
            Value = saved;
        }
    }

    public void Set(T next) => Value = next;

    public void Set(Func<T, T> change) => Value = change(Value);

    /// <summary>Back to what is saved.</summary>
    public void Discard() => Value = Base;

    /// <summary>After a save: what was sent is what is saved now.</summary>
    public void Commit(T sent) => Base = sent;
}

public static class SaveBar
{
    /// <summary>The same value, or the same content.</summary>
    public static bool Same<T>(T a, T b) =>
        EqualityComparer<T>.Default.Equals(a, b) || JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    /// <summary>How many of the fields differ between saved and edited rows, matched by position (countChanged).</summary>
    public static int CountChanged<T>(IReadOnlyList<T> saved, IReadOnlyList<T> edited, params Func<T, object?>[] fields)
    {
        var n = Math.Abs(saved.Count - edited.Count);
        for (var i = 0; i < Math.Min(saved.Count, edited.Count); i++)
            foreach (var field in fields)
                if (!Same(field(saved[i]), field(edited[i]))) n++;
        return n;
    }
}
