using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Per-model FIFO request queue with rate limiting and 429 exponential backoff.
    ///
    /// Problem: Free OpenRouter models (e.g. gpt-oss-120b:free) enforce strict RPM
    /// limits. Firing multiple requests simultaneously causes cascading 429s that
    /// silence all NPC responses for the full cooldown window.
    ///
    /// Solution: Each model gets its own queue.  A background worker drains the queue
    /// one-at-a-time, respecting a configurable minimum gap between requests.
    /// On 429, the worker backs off exponentially (base 5s, max 120s) and retries
    /// the SAME request — the caller never needs to retry manually.
    /// </summary>
    public class LLMRequestQueue : IDisposable
    {
        // ── Configuration ──────────────────────────────────────────────────────────
        /// <summary>Minimum seconds between any two requests to the same model.</summary>
        public float ModelCooldownSeconds { get; set; } = 4f;

        /// <summary>Initial backoff on 429 (seconds). Doubles on each consecutive 429.</summary>
        private const float BACKOFF_BASE_SECONDS = 5f;
        /// <summary>Maximum backoff cap.</summary>
        private const float BACKOFF_MAX_SECONDS = 120f;
        /// <summary>Max queued items per model before oldest is dropped.</summary>
        private const int MAX_QUEUE_DEPTH = 12;

        // ── Internal state ─────────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, ModelQueue> _queues =
            new ConcurrentDictionary<string, ModelQueue>(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Enqueues an async LLM call and returns a Task that completes when the
        /// request eventually executes (or is evicted from the queue).
        /// </summary>
        /// <param name="modelId">The model the request will be sent to.</param>
        /// <param name="work">
        ///   The actual async work to perform.  Must return a (success: bool, result: T)
        ///   tuple where success=false signals a 429 / retriable error.
        /// </param>
        public Task<TResult> Enqueue<TResult>(
            string modelId,
            Func<Task<(bool success, bool is429, TResult result)>> work,
            CancellationToken ct = default)
        {
            if (_disposed) return Task.FromResult(default(TResult));

            var queue = _queues.GetOrAdd(modelId, _ => new ModelQueue(modelId, this));
            return queue.Enqueue(work, ct);
        }

        /// <summary>
        /// Returns current queue lengths and backoff states for all known models.
        /// Useful for the debug monitor or status bar.
        /// </summary>
        public IReadOnlyList<ModelQueueStatus> GetStatus()
        {
            var list = new List<ModelQueueStatus>();
            foreach (var kvp in _queues)
                list.Add(kvp.Value.GetStatus());
            return list;
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var q in _queues.Values)
                q.Dispose();
        }

        // ── Inner: per-model queue ─────────────────────────────────────────────────

        private class ModelQueue : IDisposable
        {
            private readonly string _modelId;
            private readonly LLMRequestQueue _parent;
            private readonly ConcurrentQueue<PendingItem> _pending = new ConcurrentQueue<PendingItem>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, int.MaxValue);
            private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

            private float _backoffSeconds;
            private int _consecutiveRateErrors;
            private DateTime _nextAllowedUtc = DateTime.MinValue;
            private int _queuedCount;

            internal ModelQueue(string modelId, LLMRequestQueue parent)
            {
                _modelId = modelId;
                _parent  = parent;
                _ = Task.Run(DrainLoop);
            }

            internal Task<T> Enqueue<T>(
                Func<Task<(bool success, bool is429, T result)>> work,
                CancellationToken ct)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                // If queue is full, drop the oldest item
                if (_queuedCount >= MAX_QUEUE_DEPTH)
                {
                    if (_pending.TryDequeue(out var dropped))
                    {
                        dropped.SetCancelled();
                        Interlocked.Decrement(ref _queuedCount);
                        LLMNPCsPlugin.LogToFile(
                            $"[LLMRequestQueue] Queue full for {_modelId} — dropped oldest request");
                    }
                }

                Interlocked.Increment(ref _queuedCount);
                _pending.Enqueue(new PendingItem<T>(work, tcs, ct));
                _signal.Release();
                return tcs.Task;
            }

            private async Task DrainLoop()
            {
                var ct = _shutdownCts.Token;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _signal.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!_pending.TryDequeue(out var item))
                        continue;

                    Interlocked.Decrement(ref _queuedCount);

                    // ── Cooldown gap ──────────────────────────────────────────────
                    var now = DateTime.UtcNow;
                    var gap = _nextAllowedUtc - now;
                    if (gap > TimeSpan.Zero)
                    {
                        LLMNPCsPlugin.LogToFile(
                            $"[LLMRequestQueue] {_modelId} cooldown {gap.TotalSeconds:F1}s");
                        try { await Task.Delay(gap, ct); }
                        catch (OperationCanceledException) { item.SetCancelled(); break; }
                    }

                    // ── Execute with 429 retry ────────────────────────────────────
                    bool executed = false;
                    bool cancelled = false;
                    while (!executed && !cancelled && !ct.IsCancellationRequested)
                    {
                        var (success, is429, result) = await item.ExecuteAsync();

                        if (success)
                        {
                            _consecutiveRateErrors = 0;
                            _backoffSeconds = 0;
                            _nextAllowedUtc = DateTime.UtcNow.AddSeconds(
                                _parent.ModelCooldownSeconds);
                            executed = true;
                        }
                        else if (is429)
                        {
                            _consecutiveRateErrors++;
                            _backoffSeconds = Math.Min(
                                BACKOFF_MAX_SECONDS,
                                _consecutiveRateErrors == 1
                                    ? BACKOFF_BASE_SECONDS
                                    : _backoffSeconds * 2f);

                            LLMNPCsPlugin.Log.LogWarning(
                                $"[LLMRequestQueue] {_modelId} 429 #{_consecutiveRateErrors} — " +
                                $"backing off {_backoffSeconds:F0}s");
                            LLMNPCsPlugin.LogToFile(
                                $"[LLMRequestQueue] {_modelId} backoff={_backoffSeconds:F0}s " +
                                $"consecutiveErrors={_consecutiveRateErrors}");

                            _nextAllowedUtc = DateTime.UtcNow.AddSeconds(_backoffSeconds);

                            try { await Task.Delay(TimeSpan.FromSeconds(_backoffSeconds), ct); }
                            catch (OperationCanceledException) { item.SetCancelled(); cancelled = true; }
                        }
                        else
                        {
                            executed = true;
                        }
                    }

                }
            }

            internal ModelQueueStatus GetStatus() => new ModelQueueStatus
            {
                ModelId             = _modelId,
                QueuedCount         = _queuedCount,
                BackoffSeconds      = _backoffSeconds,
                ConsecutiveErrors   = _consecutiveRateErrors,
                NextAllowedUtc      = _nextAllowedUtc,
                IsBackingOff        = _backoffSeconds > 0 && DateTime.UtcNow < _nextAllowedUtc
            };

            public void Dispose() => _shutdownCts.Cancel();
        }

        // ── Inner: pending item ────────────────────────────────────────────────────

        private abstract class PendingItem
        {
            internal abstract Task<(bool success, bool is429, object result)> ExecuteAsync();
            internal abstract void SetCancelled();
        }

        private class PendingItem<T> : PendingItem
        {
            private readonly Func<Task<(bool, bool, T)>> _work;
            private readonly TaskCompletionSource<T>     _tcs;
            private readonly CancellationToken           _ct;

            internal PendingItem(
                Func<Task<(bool, bool, T)>> work,
                TaskCompletionSource<T> tcs,
                CancellationToken ct)
            {
                _work = work;
                _tcs  = tcs;
                _ct   = ct;
            }

            internal override async Task<(bool success, bool is429, object result)> ExecuteAsync()
            {
                if (_ct.IsCancellationRequested)
                {
                    _tcs.TrySetCanceled(_ct);
                    return (true, false, default(T));
                }

                try
                {
                    var (success, is429, value) = await _work();
                    if (success)
                        _tcs.TrySetResult(value);
                    return (success, is429, (object)value);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                    return (true, false, default(T));
                }
            }

            internal override void SetCancelled() => _tcs.TrySetCanceled();
        }
    }

    // ── Status DTO ─────────────────────────────────────────────────────────────────

    public class ModelQueueStatus
    {
        public string   ModelId           { get; set; }
        public int      QueuedCount       { get; set; }
        public float    BackoffSeconds    { get; set; }
        public int      ConsecutiveErrors { get; set; }
        public DateTime NextAllowedUtc    { get; set; }
        public bool     IsBackingOff      { get; set; }

        /// <summary>Human-readable label for the status bar.</summary>
        public string ToStatusLabel()
        {
            if (IsBackingOff)
            {
                var wait = (NextAllowedUtc - DateTime.UtcNow).TotalSeconds;
                return $"⏳ {ModelId.Split('/').Last()} rate-limited — retry in {wait:F0}s";
            }
            if (QueuedCount > 0)
                return $"📨 {ModelId.Split('/').Last()} — {QueuedCount} queued";
            return null;
        }
    }
}
