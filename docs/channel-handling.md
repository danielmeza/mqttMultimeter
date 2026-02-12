# Channel handling

This app uses a three-stage pipeline per connection:

1. **Channels** (ingestion + backpressure): buffer raw events from MQTTnet callbacks.
2. **Reactive streams** (distribution): `Subject<T>` → hot `IObservable<T>` for multicasting.
3. **Subscriber pipelines** (transformation + batched UI): `.Select(Map)` → `.Buffer(N ms)` → `.ObserveOn(UI)` → `SourceList.Edit()`.

Both the message channel and the packet inspection channel share the same buffering
strategy configured in the Connection page under Message Processing.

**Important:** The packet inspection channel always uses non-blocking writes internally,
regardless of the configured full mode. See [Packet inspection and keep-alive](#packet-inspection-and-keep-alive)
for details.

## Why channels

MQTT traffic can arrive much faster than the UI thread can process it. Channels provide
a buffer between the MQTT client callbacks and the UI handlers so the app can smooth
bursts, apply backpressure or drops, and keep the UI responsive. If you want more detail
on these concepts, see:

- Backpressure: https://en.wikipedia.org/wiki/Backpressure
- Bounded buffers: https://en.wikipedia.org/wiki/Buffer_(computer_science)
- Producer-consumer: https://en.wikipedia.org/wiki/Producer%E2%80%93consumer_problem

For high-throughput scenarios (8K+ messages/second), see the dedicated
[High-throughput guide](high-throughput-guide.md).

## Bounded vs unbounded

### Bounded channels

Bounded channels have a fixed capacity. When the buffer is full, a "full mode" decides
what happens next.

Pros
- Predictable memory usage.
- Better control under heavy load.
- Supports backpressure or selective dropping.

Cons
- Data can be delayed or dropped when producers are faster than consumers.
- Requires tuning capacity and full mode to match workload.

When to use
- Long-running sessions where stability matters.
- High-throughput brokers where memory limits are important.
- UI responsiveness is critical and you can tolerate drops.

### Unbounded channels

Unbounded channels grow as needed until memory runs out.

Pros
- No drops caused by the buffer being full.
- Simple behavior under normal load.

Cons
- Memory usage can grow without limit on sustained high throughput.
- Risk of large GC pauses or application instability.

When to use
- Short sessions or low message volume.
- Environments with plenty of memory and predictable traffic.

## Full modes (bounded only)

The full mode decides how the channel behaves when it reaches capacity.

### Wait

The producer waits until a slot is free.

Pros
- No drops; preserves ordering.
- Natural backpressure.

Cons
- Producers can block; may increase latency.
- MQTT processing may slow down under heavy load.

Use when
- You prefer correctness over latency.
- You can accept backpressure on incoming traffic.

### Drop newest

Drops the newest item and keeps older buffered items.

Pros
- Preserves older backlog.
- Stable memory usage.

Cons
- You may miss the most recent data.

Use when
- Older data is more important than the latest.
- You want to avoid churn in the buffer.

### Drop oldest

Drops the oldest item and keeps the most recent data.

Pros
- Keeps latest information.
- Good for live dashboards.

Cons
- Loses historical backlog.

Use when
- You care more about current state than history.

### Drop write

Rejects the write immediately when full.

Pros
- Minimal overhead on the producer.
- Explicitly signals overload (drop counters increase).

Cons
- Drops are frequent under sustained load.

Use when
- You want hard limits and can accept data loss.
- You are monitoring drop counters and sizing accordingly.

## Capacity

Capacity is the maximum buffered items per channel. Increasing capacity reduces drops
but increases memory use and can increase latency during catch-up.

Tips
- Start small and increase while monitoring drop counters and UI responsiveness.
- If drops are frequent, consider higher capacity or a different full mode.

## Delays

Two independent delays can be configured:

- Message delay: applied after each message delivery to UI handlers.
- Packet delay: applied after each packet inspection callback.

Purpose
- Avoid flooding the UI thread dispatcher queue with pending tasks. Even when work
  is scheduled at the lowest priority, the dispatcher must still manage that queue.
  If the queue grows, the dispatcher spends more time scanning to find higher-priority
  work, which degrades overall responsiveness.
- Smooth bursty traffic so the UI thread can render and remain responsive.
- Reduce CPU spikes from rapid handler callbacks.
- Give expensive packet inspectors time to keep up.

Tradeoffs
- Adds latency to delivery and inspection.
- Can increase buffered items if producers are faster than consumers.

Examples
- If the UI feels laggy during bursts, start with 2-5 ms delays and adjust.
- If packet inspection is heavy (large payloads), a larger packet delay can help.
- If you need low latency, keep delays at 0 and rely on capacity/full mode.

## Observability

Use the status bar counters to monitor:

- Received: total messages received in the session.
- Notified: messages delivered to handlers.
- Buffered: messages currently queued.
- Dropped: messages discarded due to bounded/full-mode settings.

Counters are refreshed via an `Observable.Interval` on the UI thread. The default
interval is 200 ms (configurable per profile via **Counter update (ms)**). This is
fast enough for visual feedback while avoiding unnecessary UI thread wake-ups. The
observable is created on connect and disposed on disconnect.

If buffered keeps growing or drops are increasing, consider adjusting capacity,
full mode, or delays.

## Packet inspection and keep-alive

The packet inspection channel behaves differently from the message channel to protect
connection stability.

### Why packet inspection never blocks

The MQTT client library processes all packets—including keep-alive (PINGREQ/PINGRESP)—on
an internal thread. The packet inspector callback runs on this same thread. If that
callback blocks (for example, waiting for a full bounded channel), keep-alive packets
cannot be sent or received. The broker will then time out the connection, typically
after the configured keep-alive interval (often 10–30 seconds).

Symptoms of this issue in the log:
- "Communication error while receiving packets"
- "Communication error while sending/receiving keep alive packets"
- "Disconnecting [Timeout=00:00:10]"

### How the app handles this

To prevent accidental disconnections:

1. **Handler always registered before connect.** The `InspectPacketAsync` handler is
   registered before `ConnectAsync()` is called, not dynamically after connection.
   
   **Why?** MQTTnet's `InspectPacketAsync` event is not safe to subscribe/unsubscribe
   while the client is connected and processing packets. Adding or removing handlers
   during the receive loop causes race conditions that lead to disconnections.

2. **Flag-based enable/disable.** Instead of subscribing/unsubscribing from the event,
   the handler checks `_isInspectingPackets` flag and returns immediately if disabled.
   This has minimal overhead (just a boolean check).

3. **Non-blocking writes.** The handler uses `TryWrite`, which never blocks. If the
   channel is full, the packet is simply dropped.

4. **Drops are logged.** When a packet cannot be written because the channel is
   full, the drop is counted and logged.

### MQTTnet source code analysis

The race condition occurs in the following MQTTnet code paths:

**1. `AsyncEvent<T>.AddHandler()` in `MQTTnet/Internal/AsyncEvent.cs`:**
```csharp
public void AddHandler(Func<TEventArgs, Task> handler)
{
    lock (_handlers)
    {
        _handlers.Add(new AsyncEventInvocator<TEventArgs>(null, handler));
        HasHandlers = true;
        _handlersForInvoke = new List<AsyncEventInvocator<TEventArgs>>(_handlers);  // Creates NEW list
    }
}
```

**2. `MqttPacketInspector.InspectPacket()` in `MQTTnet/Diagnostics/PacketInspection/MqttPacketInspector.cs`:**
```csharp
async Task InspectPacket(byte[] buffer, MqttPacketFlowDirection direction)
{
    var eventArgs = new InspectMqttPacketEventArgs(direction, buffer);
    await _asyncEvent.InvokeAsync(eventArgs).ConfigureAwait(false);  // Called on receive loop
}
```

**3. `AsyncEvent<T>.InvokeAsync()` in `MQTTnet/Internal/AsyncEvent.cs`:**
```csharp
public async Task InvokeAsync(TEventArgs eventArgs)
{
    var handlers = _handlersForInvoke;  // Gets reference
    foreach (var handler in handlers)   // Iterates
    {
        await handler.InvokeAsync(eventArgs).ConfigureAwait(false);
    }
}
```

**The race window:**
1. Receive loop calls `InvokeAsync()`, gets `_handlersForInvoke` reference
2. User calls `AddHandler()` on another thread, replaces `_handlersForInvoke` with new list
3. The iteration behavior becomes undefined
4. Socket operations may fail, keep-alive times out, connection drops

### Trade-off

We accept the small overhead of always having the `InspectPacketAsync` handler
registered in exchange for:
- Stable connections without risk of disconnections
- Ability to toggle inspection on/off at any time
- No race conditions during enable/disable

The overhead when inspection is disabled is minimal: just a boolean check that
returns `Task.CompletedTask` immediately.

### Recommendations for packet inspection

- Use a generous capacity if you want to capture more packets.
- Accept that under extreme load, some packets may be dropped to keep the
  connection alive.
- If you see frequent packet drops, increase capacity or reduce traffic.
- Do not rely on Wait mode for lossless packet capture; the connection would
  disconnect before you see the benefit.

## Thread safety and counters

The status bar counters (Received, Notified, Buffered, Dropped) are updated from
background threads and read from the UI thread.

### Why plain reads and writes are sufficient

Each counter has exactly one writer thread. The app starts two dedicated
`Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` consumer tasks per
connection — one for the message channel, one for the packet inspection channel.
Each task runs a loop that reads from its channel and calls `Subject<T>.OnNext()`
to push events into the reactive stream. Because each consumer task is a single
long-running thread, the counter increments within that task (`_notifiedMessagesCount++`)
are always single-writer.

| Counter | Writer thread | Where |
|---------|---------------|-------|
| Received | MQTTnet receive loop (single internal thread) | `OnApplicationMessageReceived` |
| Enqueued | MQTTnet receive loop (same thread as Received) | `OnApplicationMessageReceived`, after channel write |
| Notified | Message consumer task (`ConsumeReceivedMessagesAsync`, `LongRunning`) | After `_messageSubject.OnNext()` |
| Dropped | Channel drop callback (invoked on the writer's thread — the MQTTnet receive loop) | `OnMessageDropped` |

The packet inspection consumer task (`ConsumePacketInspectionsAsync`, also `LongRunning`)
follows the same single-writer pattern for its own processing.

With a single writer per counter, plain `counter++` and reads are safe because:

1. **No concurrent writes**: Since only one thread modifies each counter, there's
   no risk of lost updates from interleaved read-modify-write operations.

2. **64-bit atomicity**: On 64-bit systems (the common case), reads and writes of
   `long` values are atomic at the hardware level — no "torn reads."

3. **Eventual visibility**: CPU cache coherence protocols (like MESI) ensure that
   updates eventually become visible to other cores. The UI polls counters on a
   timer (every 200ms), so brief staleness is imperceptible.

4. **Display-only purpose**: These counters are informational. Millisecond-level
   staleness doesn't affect correctness — users cannot perceive such delays.

### Consumer tasks and Subject.OnNext()

Each consumer task's loop looks like:

```csharp
// ConsumeReceivedMessagesAsync — runs on a dedicated LongRunning thread
while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
{
    while (reader.TryRead(out var eventArgs))
    {
        _notifiedMessagesCount++;
        _messageSubject?.OnNext(eventArgs);  // single-writer: only this task calls OnNext

        var delayMs = Volatile.Read(ref _messageProcessingDelayMs);
        if (delayMs > 0)
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }
}
```

`Subject<T>.OnNext()` is called from exactly one thread. This matters because:
- The downstream `.Select(Map)` runs on this same thread (no scheduler switch yet).
- `Buffer(TimeSpan)` internally uses a timer, but the event delivery is serialized.
- Only after `.ObserveOn(RxApp.MainThreadScheduler)` does work move to the UI thread.

### When would you need Volatile or Interlocked?

- **`Volatile`**: Use when you need guaranteed immediate visibility across threads,
  or when targeting 32-bit systems where `long` reads could tear.

- **`Interlocked`**: Use when multiple threads may increment the same counter
  concurrently, because `counter++` is three operations that could interleave.

## DynamicData SourceList for UI collections

For high-throughput scenarios, the app uses `SourceList<T>` from the DynamicData
library instead of plain `ObservableCollection<T>`.

### Why SourceList?

`ObservableCollection<T>` has limitations:
- Must be modified on the UI thread
- Each `Add()` triggers an immediate `CollectionChanged` event
- No built-in batching or throttling

`SourceList<T>` from DynamicData provides:
- Thread-safe modifications from any thread
- Change sets that batch multiple operations
- Reactive pipeline with filtering, sorting, throttling
- Automatic UI thread marshalling via `ObserveOn()`

### Critical: Add() vs Edit()

`SourceList.Add()` is a convenience method that internally calls
`source.Edit(list => list.Add(item))` — each call emits a **separate**
`IChangeSet<T>`. In a loop of 50 items, this means 50 changesets →
50 `Bind()` updates → 50 `CollectionChanged` events → potentially 50
layout/render passes.

**Always use `Edit()` when inserting multiple items:**

```csharp
// BAD: 50 items → 50 changesets → 50 UI updates
foreach (var item in batch)
    _source.Add(item);           // Each Add = Edit(list => list.Add(item))

// GOOD: 50 items → 1 changeset → 1 UI update
_source.Edit(list =>
{
    foreach (var item in batch)
    {
        list.Add(item);
        if (list.Count > 1000) list.RemoveAt(0);
    }
});
```

From DynamicData source (`SourceListEditConvenienceEx.cs`):
```csharp
public static void Add<T>(this ISourceList<T> source, T item)
{
    source.Edit(list => list.Add(item));  // Each call = separate changeset!
}
```

The `Edit()` method uses an internal `_editLevel` counter. Changes accumulate
while `_editLevel > 0` and only emit a single `IChangeSet` when the outermost
`Edit()` completes.

### Usage pattern

```csharp
// Field: thread-safe source list
readonly SourceList<ItemViewModel> _itemsSource = new();
readonly ReadOnlyObservableCollection<ItemViewModel> _items;

// Constructor: set up the reactive pipeline
_itemsSource.Connect()
    .Filter(filter)
    .Sort(SortExpressionComparer<ItemViewModel>.Ascending(t => t.Number))
    .ObserveOn(RxApp.MainThreadScheduler)  // Marshal to UI thread
    .Bind(out _items)                       // Bind to ReadOnlyObservableCollection
    .Subscribe();

// Insert a batch — always use Edit()
_itemsSource.Edit(list =>
{
    foreach (var vm in batch)
    {
        list.Add(vm);
        if (list.Count > 1000) list.RemoveAt(0);
    }
});
```

### Benefits for high throughput

1. **No dispatcher flooding**: `Edit()` batches all changes into one `IChangeSet`.
2. **Thread-safe adds**: Background threads can add items without `Dispatcher.Post`.
3. **Built-in operators**: Use `.Filter()`, `.Sort()`, `.Throttle()` in the pipeline.
4. **Automatic cleanup**: `ObserveOn()` handles all UI thread marshalling.

### When to use each collection type

| Type | Use case |
|------|----------|
| `ObservableCollection<T>` | Low-throughput, UI-thread only modifications |
| `SourceList<T>` | High-throughput, multi-threaded modifications |
| `SourceCache<T,K>` | Keyed data with updates by key |

## Reactive stream pipeline

After channels decouple ingestion from processing, **reactive streams** handle
distribution and transformation.

### Architecture overview

```
MQTTnet callback
    → Channel<T> (backpressure/drops)
        → Consumer task (LongRunning, single thread)
            → Subject<T>.OnNext()
                → IObservable<T> (hot, Publish().RefCount())
                    → Subscriber pipeline:
                        .Select(Map)           ← transform on stream thread
                        .Buffer(200ms)         ← collect mapped items
                        .Where(batch.Count>0)  ← skip empty batches
                        .ObserveOn(UI)         ← marshal to UI thread
                        .Subscribe(batch =>    ← SourceList.Edit() bulk insert)

    Topic Explorer variant:
        .Select(unwrap)                        ← unwrap eventArgs (stream thread)
        .Buffer(200ms)                         ← collect messages
        .Where(batch.Count>0)                  ← skip empty batches
        .Select(PrepareBatchInserts)           ← tree walk + node creation (stream thread)
        .ObserveOn(UI)                         ← marshal to UI thread
        .Subscribe(FlushBatchInserts)          ← Edit() + AddMessage (UI thread)
```

### Session lifecycle

On **Connect**:
1. `StartChannelProcessing()` creates channels + consumer tasks.
2. `StartReactiveStreams()` creates `Subject<T>` + `Publish().RefCount()` observables.
3. Session events (`MessageStreamConnected`, etc.) fire, passing the `IObservable<T>`.
4. Each subscriber builds its pipeline and stores the subscription in a `CompositeDisposable`.

On **Disconnect**:
1. `StopReactiveStreams()` calls `OnCompleted()` + `Dispose()` on subjects.
2. Session events (`MessageStreamDisconnected`, etc.) fire.
3. Each subscriber disposes its `CompositeDisposable`.
4. `StopChannelProcessingAsync()` cancels consumer tasks and completes channels.

### Why .Select() before .Buffer()

The `.Select()` operator runs **on the stream thread** (the consumer task thread),
not on the UI thread. This moves ViewModel creation off the UI thread:

```csharp
stream
    .Select(MapPacket)                     // ← runs on consumer task thread
    .Buffer(TimeSpan.FromMilliseconds(500)) // ← collects already-mapped VMs
    .Where(batch => batch.Count > 0)
    .ObserveOn(RxApp.MainThreadScheduler)   // ← only the insert hits the UI thread
    .Subscribe(batch => InsertBatch(batch));
```

**Before** (transform on UI thread):
```
stream → Buffer(raw events) → ObserveOn(UI) → foreach: create VM + Add() ← UI thread does ALL work
```

**After** (transform on stream thread):
```
stream → Select(create VM) → Buffer(VMs) → ObserveOn(UI) → Edit(bulk insert) ← UI thread only inserts
```

**Topic Explorer** (two-phase offload):
```
stream → Select(unwrap) → Buffer(msgs) → Select(PrepareBatchInserts) → ObserveOn(UI) → FlushBatchInserts
                                                ↑ stream thread                            ↑ UI thread
                                           tree walk + node creation               Edit() + AddMessage
```

This matters because ViewModel creation can involve:
- String formatting (timestamps, hex encoding)
- Object allocation (closures, event handlers)
- Property initialization

None of this needs the UI thread. Only the `SourceList.Edit()` bulk insert
(which updates the bound collection) must run on the UI thread.

### Per-subscriber pipelines

| Subscriber | `.Select()` maps | Buffer | UI work |
|------------|-------------------|--------|---------|
| Inflight | `EventArgs` → `InflightPageItemViewModel` | configurable (profile) | `SourceList.AddRangeAndTrim()` via `Edit()` |
| Packet Inspector | `InspectMqttPacketEventArgs` → `PacketViewModel` | configurable (profile) | `SourceList.AddRangeAndTrim()` via `Edit()` |
| Log | `MqttNetLogMessagePublishedEventArgs` → `LogItemViewModel` | configurable (profile) | `SourceList.AddRangeAndTrim()` via `Edit()` |
| Topic Explorer | `EventArgs` → `MqttApplicationMessage` (unwrap) → `PrepareBatchInserts` (tree walk + node creation) | configurable (profile) | `FlushBatchInserts`: `Edit()` per SourceList + `AddMessage()` |

Buffer windows are set per-profile (Low = 50 ms, Medium = 150 ms, High = 300 ms).
A shorter window gives faster on-screen updates but wakes the UI thread more often;
a longer window reduces UI thread pressure at the cost of perceived latency.
See the high-throughput guide for a detailed explanation of the trade-off.

### Topic Explorer two-phase offload

Unlike the other subscribers that create a single ViewModel per message,
the Topic Explorer must walk a hierarchical tree, create intermediate
nodes, and maintain a lookup dictionary. This work is now split across
two threads:

| Phase | Thread | Work |
|-------|--------|------|
| `PrepareBatchInserts` | Stream thread | `string.Split('/')`, `ConcurrentDictionary` lookups, `TopicExplorerTreeNodeViewModel` creation, staging `SourceList` adds into a `pendingAdds` dictionary |
| `FlushBatchInserts` | UI thread | `sourceList.Edit(inner => inner.AddRange(newNodes))` per SourceList, `node.AddMessage()` / `node.Clear()` |

This means the UI thread only performs the minimal SourceList mutations
and message data application — all tree traversal and allocation runs
on the consumer task thread.

