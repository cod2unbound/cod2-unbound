using System.Collections.ObjectModel;

namespace CoD2Unbound.Core;

public static class ObservableRows
{
    public static void Reconcile<T, TKey>(this ObservableCollection<T> rows, IReadOnlyList<T> next,
        Func<T, TKey> key, Func<T, T, T>? update = null) where T : class where TKey : notnull
    {
        var keys = next.Select(key).ToArray();
        if (keys.Distinct().Count() != keys.Length || rows.Select(key).Distinct().Count() != rows.Count)
        {
            // Rows that share an identity cannot be matched one to one; replace the contents.
            rows.Clear();
            foreach (var item in next)
                rows.Add(item);
            return;
        }

        var existing = new Dictionary<TKey, (T Row, int Index)>();
        for (int index = 0; index < rows.Count; index++)
            existing.Add(key(rows[index]), (rows[index], index));

        var anchored = AnchoredIndices(keys.Where(existing.ContainsKey).Select(id => existing[id].Index));
        // WinUI's ListView may translate a Move notification into a full Reset.
        // Remove and reinsert only the rows whose relative positions changed.
        for (int index = rows.Count - 1; index >= 0; index--)
            if (!anchored.Contains(index))
                rows.RemoveAt(index);

        for (int index = 0; index < next.Count; index++)
        {
            var incoming = next[index];
            if (!existing.TryGetValue(keys[index], out var old))
            {
                rows.Insert(index, incoming);
                continue;
            }
            var row = update is not null ? update(old.Row, incoming) : Equals(old.Row, incoming) ? old.Row : incoming;
            if (!anchored.Contains(old.Index))
                rows.Insert(index, row);
            else if (!ReferenceEquals(old.Row, row))
                rows[index] = row;
        }
    }

    // The longest increasing run of old positions (with gaps) can stay in the list.
    // A server moving down the list should not displace every row that it passes.
    private static HashSet<int> AnchoredIndices(IEnumerable<int> indices)
    {
        var tails = new List<int>();
        var previous = new Dictionary<int, int>();
        foreach (int index in indices)
        {
            int position = ~tails.BinarySearch(index);
            if (position > 0)
                previous.Add(index, tails[position - 1]);
            if (position == tails.Count)
                tails.Add(index);
            else
                tails[position] = index;
        }

        var anchored = new HashSet<int>();
        if (tails.Count == 0)
            return anchored;
        int tail = tails[^1];
        anchored.Add(tail);
        while (previous.TryGetValue(tail, out tail))
            anchored.Add(tail);
        return anchored;
    }
}
