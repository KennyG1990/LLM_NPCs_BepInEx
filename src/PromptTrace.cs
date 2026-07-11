using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    public static class PromptFlowTypes
    {
        public const string PlayerToNpc         = "player->npc";
        public const string NpcToNpc            = "npc->npc";
        public const string AutonomousChatter   = "autonomous/chatter";
        public const string MemorySummarization = "memory/summarization"; // uses npc_decisions model
        public const string NpcDecisions        = "npc/decisions";
        public const string ColonyAdvisor       = "colony/advisor";        // overseer narrative nudge; task "adviser"
    }

    public class LLMTraceMetadata
    {
        public string FlowType { get; set; }
        public string SenderName { get; set; }
        public string TargetName { get; set; }
    }

    public struct PromptTraceHandle
    {
        internal Guid Id;
        internal bool IsValid;

        internal static PromptTraceHandle Invalid => new PromptTraceHandle
        {
            Id = Guid.Empty,
            IsValid = false
        };
    }

    public class PromptTraceEvent
    {
        public string TimestampUtc { get; set; }
        public string FlowType { get; set; }
        public string SenderName { get; set; }
        public string TargetName { get; set; }
        public string ModelId { get; set; }
        public string Provider { get; set; }
        public string Endpoint { get; set; }
        public string PromptPreview { get; set; }
        public int PromptLength { get; set; }
        public string ResponsePreview { get; set; }
        public int ResponseLength { get; set; }
        public string Status { get; set; }
        public long LatencyMs { get; set; }
        public string ErrorText { get; set; }
    }

    public struct PromptTraceCounters
    {
        public int Sent;
        public int Success;
        public int Error;
    }

    internal class PendingPromptTrace
    {
        public DateTime SentAtUtc;
        public Stopwatch Stopwatch;
        public string FlowType;
        public string SenderName;
        public string TargetName;
        public string ModelId;
        public string Provider;
        public string Endpoint;
        public string PromptRaw;
    }

    public static class PromptTrace
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<Guid, PendingPromptTrace> _pending = new Dictionary<Guid, PendingPromptTrace>();
        private static readonly List<PromptTraceEvent> _recentEvents = new List<PromptTraceEvent>(256);
        private const int MaxRecentEvents = 256;

        private static bool _enabled = true;
        private static bool _initialized;
        private static int _sentCount;
        private static int _successCount;
        private static int _errorCount;
        private static StreamWriter _traceWriter;
        private static string _traceFilePath;

        public static void Initialize(string logDirectory, bool enabled)
        {
            lock (_lock)
            {
                _enabled = enabled;

                if (_initialized)
                    return;

                try
                {
                    var resolvedDir = string.IsNullOrEmpty(logDirectory)
                        ? Path.Combine(Application.persistentDataPath, "LLM_NPCs", "logs")
                        : logDirectory;

                    Directory.CreateDirectory(resolvedDir);
                    _traceFilePath = Path.Combine(resolvedDir, $"prompt_trace_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    _traceWriter = new StreamWriter(_traceFilePath, true) { AutoFlush = true };
                    _traceWriter.WriteLine($"# Prompt trace session started {DateTime.UtcNow:O}");
                }
                catch (Exception ex)
                {
                    LLMNPCsPlugin.Log?.LogError($"[PromptTrace] Failed to initialize trace writer: {ex.Message}");
                    _traceWriter = null;
                }

                _initialized = true;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                _enabled = enabled;
            }
        }

        public static PromptTraceHandle RecordSent(
            string flowType,
            string senderName,
            string targetName,
            string modelId,
            string provider,
            string endpoint,
            string prompt)
        {
            lock (_lock)
            {
                if (!_enabled)
                    return PromptTraceHandle.Invalid;

                var id = Guid.NewGuid();
                var now = DateTime.UtcNow;

                _pending[id] = new PendingPromptTrace
                {
                    SentAtUtc = now,
                    Stopwatch = Stopwatch.StartNew(),
                    FlowType = flowType,
                    SenderName = senderName,
                    TargetName = targetName,
                    ModelId = modelId,
                    Provider = provider,
                    Endpoint = endpoint,
                    PromptRaw = prompt ?? string.Empty
                };

                _sentCount++;

                var traceEvent = BuildTraceEvent(
                    now,
                    flowType,
                    senderName,
                    targetName,
                    modelId,
                    provider,
                    endpoint,
                    prompt,
                    null,
                    "sent",
                    0,
                    null);

                AddRecentEvent(traceEvent);
                WriteTraceLine(traceEvent);

                return new PromptTraceHandle
                {
                    Id = id,
                    IsValid = true
                };
            }
        }

        public static void RecordSuccess(PromptTraceHandle handle, string response)
        {
            lock (_lock)
            {
                if (!_enabled)
                    return;

                PendingPromptTrace pending;
                if (!TryTakePending(handle, out pending))
                {
                    pending = new PendingPromptTrace
                    {
                        SentAtUtc = DateTime.UtcNow,
                        FlowType = PromptFlowTypes.AutonomousChatter,
                        PromptRaw = string.Empty,
                        Provider = "unknown"
                    };
                }

                pending.Stopwatch?.Stop();
                var latency = pending.Stopwatch != null ? pending.Stopwatch.ElapsedMilliseconds : 0;
                _successCount++;

                var traceEvent = BuildTraceEvent(
                    DateTime.UtcNow,
                    pending.FlowType,
                    pending.SenderName,
                    pending.TargetName,
                    pending.ModelId,
                    pending.Provider,
                    pending.Endpoint,
                    pending.PromptRaw,
                    response,
                    "success",
                    latency,
                    null);

                AddRecentEvent(traceEvent);
                WriteTraceLine(traceEvent);
            }
        }

        public static void RecordError(PromptTraceHandle handle, string errorText, string response = null)
        {
            lock (_lock)
            {
                if (!_enabled)
                    return;

                PendingPromptTrace pending;
                if (!TryTakePending(handle, out pending))
                {
                    pending = new PendingPromptTrace
                    {
                        SentAtUtc = DateTime.UtcNow,
                        FlowType = PromptFlowTypes.AutonomousChatter,
                        PromptRaw = string.Empty,
                        Provider = "unknown"
                    };
                }

                pending.Stopwatch?.Stop();
                var latency = pending.Stopwatch != null ? pending.Stopwatch.ElapsedMilliseconds : 0;
                _errorCount++;

                var traceEvent = BuildTraceEvent(
                    DateTime.UtcNow,
                    pending.FlowType,
                    pending.SenderName,
                    pending.TargetName,
                    pending.ModelId,
                    pending.Provider,
                    pending.Endpoint,
                    pending.PromptRaw,
                    response,
                    "error",
                    latency,
                    errorText);

                AddRecentEvent(traceEvent);
                WriteTraceLine(traceEvent);
            }
        }

        public static List<PromptTraceEvent> GetRecentSnapshot(int maxEvents)
        {
            lock (_lock)
            {
                var safeMax = Math.Max(1, maxEvents);
                return _recentEvents
                    .AsEnumerable()
                    .Reverse()
                    .Take(safeMax)
                    .ToList();
            }
        }

        public static PromptTraceCounters GetCounters()
        {
            lock (_lock)
            {
                return new PromptTraceCounters
                {
                    Sent = _sentCount,
                    Success = _successCount,
                    Error = _errorCount
                };
            }
        }

        public static string GetTraceFilePath()
        {
            lock (_lock)
            {
                return _traceFilePath;
            }
        }

        public static void ClearRecent()
        {
            lock (_lock)
            {
                _recentEvents.Clear();
            }
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                try
                {
                    _traceWriter?.Flush();
                    _traceWriter?.Close();
                }
                catch
                {
                    // No-op in shutdown path.
                }
                finally
                {
                    _traceWriter = null;
                    _pending.Clear();
                }
            }
        }

        private static bool TryTakePending(PromptTraceHandle handle, out PendingPromptTrace pending)
        {
            pending = null;
            if (!handle.IsValid)
                return false;

            if (_pending.TryGetValue(handle.Id, out pending))
            {
                _pending.Remove(handle.Id);
                return true;
            }

            return false;
        }

        private static PromptTraceEvent BuildTraceEvent(
            DateTime timestampUtc,
            string flowType,
            string senderName,
            string targetName,
            string modelId,
            string provider,
            string endpoint,
            string prompt,
            string response,
            string status,
            long latencyMs,
            string errorText)
        {
            return new PromptTraceEvent
            {
                TimestampUtc = timestampUtc.ToString("O"),
                FlowType = string.IsNullOrEmpty(flowType) ? PromptFlowTypes.AutonomousChatter : flowType,
                SenderName = senderName,
                TargetName = targetName,
                ModelId = modelId,
                Provider = provider,
                Endpoint = endpoint,
                PromptPreview = CreatePreview(prompt),
                PromptLength = prompt?.Length ?? 0,
                ResponsePreview = CreatePreview(response),
                ResponseLength = response?.Length ?? 0,
                Status = status,
                LatencyMs = latencyMs,
                ErrorText = errorText
            };
        }

        private static void AddRecentEvent(PromptTraceEvent traceEvent)
        {
            _recentEvents.Add(traceEvent);
            if (_recentEvents.Count > MaxRecentEvents)
            {
                var removeCount = _recentEvents.Count - MaxRecentEvents;
                _recentEvents.RemoveRange(0, removeCount);
            }
        }

        private static void WriteTraceLine(PromptTraceEvent traceEvent)
        {
            if (_traceWriter == null)
                return;

            try
            {
                _traceWriter.WriteLine(JsonConvert.SerializeObject(traceEvent));
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log?.LogError($"[PromptTrace] Failed to write trace event: {ex.Message}");
            }
        }

        private static string CreatePreview(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var flattened = text.Replace("\r", " ").Replace("\n", " ").Trim();
            const int maxChars = 220;
            if (flattened.Length <= maxChars)
                return flattened;

            return flattened.Substring(0, maxChars) + "…";
        }
    }
}
