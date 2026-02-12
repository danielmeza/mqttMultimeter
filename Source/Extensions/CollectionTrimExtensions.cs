using System;
using System.Collections.Generic;
using DynamicData;

namespace mqttMultimeter.Extensions;

public static class CollectionTrimExtensions
{
    /// <summary>
    /// Adds items and trims the oldest entries inside a single
    /// <see cref="SourceList{T}.Edit"/> changeset so the UI receives one update.
    /// </summary>
    public static void AddRangeAndTrim<T>(
        this SourceList<T> source,
        IList<T> items,
        int maxItems,
        int trimBatchSize) where T : notnull
    {
        source.Edit(list =>
        {
            list.AddRange(items);

            if (list.Count > maxItems)
            {
                list.RemoveRange(0, Math.Min(trimBatchSize, list.Count));
            }
        });
    }
}
