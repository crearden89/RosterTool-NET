// ─── JavaScript Map and Set: insertion order, exactly ───────────────────────
// The engine iterates its maps and sets, and the order it meets the entries in
// decides ties all over the code ("the first best", "the earliest key"). A JS
// Map iterates in insertion order; re-setting a key keeps its place; deleting
// and re-adding moves it to the end; entries added DURING an iteration are
// visited by it and deleted ones are skipped. .NET's Dictionary promises none
// of that, so the port uses these instead wherever the TypeScript used a Map
// or a Set (or a string-keyed object, whose own string keys keep insertion
// order the same way).
//
// Keys compare with the comparer given (default equality otherwise). Doubles
// compare as SameValueZero (0 and -0 alike, NaN equal to itself), which is
// what double.Equals already does. Reference-type keys that override Equals
// (records) should be given ReferenceEqualityComparer.Instance when the
// TypeScript keyed by object identity.

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Roster.Engine.Core;

public class JsMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    private struct Entry
    {
        public TKey Key;
        public TValue Value;
        public bool Deleted;
    }

    private readonly Dictionary<TKey, int> _index;
    private readonly List<Entry> _entries = new();
    private int _count;

    public JsMap() : this((IEqualityComparer<TKey>?)null) { }

    public JsMap(IEqualityComparer<TKey>? comparer) => _index = new Dictionary<TKey, int>(comparer);

    public JsMap(IEnumerable<KeyValuePair<TKey, TValue>> entries, IEqualityComparer<TKey>? comparer = null) : this(comparer)
    {
        foreach (var (k, v) in entries) Set(k, v);
    }

    public JsMap(IEnumerable<(TKey, TValue)> entries, IEqualityComparer<TKey>? comparer = null) : this(comparer)
    {
        foreach (var (k, v) in entries) Set(k, v);
    }

    public IEqualityComparer<TKey> Comparer => _index.Comparer;

    /// <summary><c>map.size</c></summary>
    public int Size => _count;

    public bool Has(TKey key) => _index.ContainsKey(key);

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary><c>map.get(key) ?? fallback</c> - the fallback when the key is absent.</summary>
    public TValue GetOr(TKey key, TValue fallback) => TryGet(key, out var v) ? v : fallback;

    /// <summary><c>map.get(key)</c> - default(TValue) when absent (null for references).</summary>
    public TValue? Get(TKey key) => TryGet(key, out var v) ? v : default;

    /// <summary><c>map.set(key, value)</c>: a new key goes last, an existing one keeps its place.</summary>
    public JsMap<TKey, TValue> Set(TKey key, TValue value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            var e = _entries[i];
            e.Value = value;
            _entries[i] = e;
        }
        else
        {
            _index[key] = _entries.Count;
            _entries.Add(new Entry { Key = key, Value = value });
            _count++;
        }
        return this;
    }

    public TValue this[TKey key]
    {
        get => TryGet(key, out var v) ? v : throw new KeyNotFoundException(key.ToString());
        set => Set(key, value);
    }

    public bool Delete(TKey key)
    {
        if (!_index.Remove(key, out var i)) return false;
        var e = _entries[i];
        e.Deleted = true;
        e.Value = default!;
        _entries[i] = e;
        _count--;
        return true;
    }

    public void Clear()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            e.Deleted = true;
            _entries[i] = e;
        }
        _index.Clear();
        _count = 0;
        // Entries stay as tombstones so a live iteration ends where JS's would;
        // a later set appends after them.
    }

    /// <summary>The live entries in insertion order (sees additions made while iterating).</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (!e.Deleted) yield return new KeyValuePair<TKey, TValue>(e.Key, e.Value);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerable<TKey> Keys()
    {
        foreach (var kv in this) yield return kv.Key;
    }

    public IEnumerable<TValue> Values()
    {
        foreach (var kv in this) yield return kv.Value;
    }

    /// <summary><c>[...map]</c>: a snapshot of the entries.</summary>
    public List<KeyValuePair<TKey, TValue>> Entries() => new(this);

    /// <summary><c>new Map(map)</c>: a copy with the same order.</summary>
    public JsMap<TKey, TValue> Copy()
    {
        var m = new JsMap<TKey, TValue>(_index.Comparer);
        foreach (var (k, v) in this) m.Set(k, v);
        return m;
    }
}

public class JsSet<T> : IEnumerable<T> where T : notnull
{
    private readonly JsMap<T, bool> _map;

    public JsSet() : this((IEqualityComparer<T>?)null) { }

    public JsSet(IEqualityComparer<T>? comparer) => _map = new JsMap<T, bool>(comparer);

    public JsSet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null) : this(comparer)
    {
        foreach (var x in items) Add(x);
    }

    public int Size => _map.Size;

    public bool Has(T item) => _map.Has(item);

    public JsSet<T> Add(T item)
    {
        if (!_map.Has(item)) _map.Set(item, true);
        return this;
    }

    public bool Delete(T item) => _map.Delete(item);

    public void Clear() => _map.Clear();

    public IEnumerator<T> GetEnumerator() => _map.Keys().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary><c>new Set(set)</c></summary>
    public JsSet<T> Copy() => new(this, _map.Comparer);
}
