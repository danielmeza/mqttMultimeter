using System;
using System.Collections.Generic;
using DynamicData;

namespace mqttMultimeter.Extensions;

public static class CollectionTrimExtensions
{
    /// <summary>
    /// Adds items and trims the oldest entries inside a single
    /// <see cref="SourceList{T}.Edit"/> changeset so the UI receives one update.
    /// Removes exactly the overflow (count − max) so the list stays at
    /// <paramref name="maxItems"/> rather than dropping a fixed batch size.
    /// </summary>
    public static void AddRangeAndTrim<T>(
        this SourceList<T> source,
        IList<T> items,
        int maxItems) where T : notnull
    {
        source.Edit(list =>
        {
            list.AddRange(items);

            var overflow = list.Count - maxItems;
            if (overflow > 0)
            {
                list.RemoveRange(0, overflow);
            }
        });
    }
}
