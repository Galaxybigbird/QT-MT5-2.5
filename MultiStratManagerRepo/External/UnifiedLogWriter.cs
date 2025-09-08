using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Grpc.Core;
using NTGrpcClient;

namespace Trading.Proto
{
    // Redirects Console output to Bridge via LoggingService.Log while preserving original output
    public sealed class UnifiedLogWriter : TextWriter
    {
    private readonly TextWriter _fallback;
    private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
    private readonly Thread _worker;
    private volatile bool _running = true;
    // Failure backoff to avoid hangs when bridge is down
    private int _consecutiveFailures = 0;
    private DateTime _firstFailureAt = DateTime.MinValue;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(10);
    private const int FailureThreshold = 8; // after ~8 failures in 10s, stop sender thread

    // Duplicate suppression (message hash -> last seen ms)
    private static readonly ConcurrentDictionary<string, long> _recent = new ConcurrentDictionary<string, long>();
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(600); // slightly wider to absorb paired prints
    private const int RecentPruneThreshold = 5000; // prune dictionary if it grows

    // Correlation reuse via shared provider

    // Regex precompiled patterns
    private static readonly Regex BracketLevelRegex = new Regex(@"\[(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Note: in verbatim strings, backslash escaping differs; we double quotes by ""
    private static readonly Regex JsonBaseIdRegex = new Regex(@"""base_id""\s*:\s*""(?<id>[^""\\]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlainBaseIdRegex = new Regex(@"base_id=([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _source;
        private readonly string _component;
        private readonly Channel _channel;
        private readonly LoggingService.LoggingServiceClient _logging;

        public UnifiedLogWriter(TextWriter fallback, string serverAddress, string source = "nt", string component = "console")
        {
            _fallback = fallback;
            _source = source;
            _component = component;

            // Normalize address for Grpc.Core (strip http/https)
            var address = (serverAddress ?? "").Replace("http://", string.Empty).Replace("https://", string.Empty);
            var options = new[]
            {
                new ChannelOption("grpc.keepalive_time_ms", 15000),
                new ChannelOption("grpc.keepalive_timeout_ms", 5000),
                new ChannelOption("grpc.http2.min_time_between_pings_ms", 10000),
                new ChannelOption("grpc.keepalive_permit_without_calls", 1)
            };
            _channel = new Channel(address, ChannelCredentials.Insecure, options);
            _logging = new LoggingService.LoggingServiceClient(_channel);

            _worker = new Thread(Worker) { IsBackground = true, Name = "UnifiedLogSender" };
            _worker.Start();
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string value)
        {
            try { _queue.Add(value ?? string.Empty); } catch { /* drop */ }
            _fallback?.WriteLine(value);
        }

        private static string DetectLevel(string line)
        {
            if (string.IsNullOrEmpty(line)) return "INFO";

            // Collect all bracketed level tokens then choose the highest severity
            var matches = BracketLevelRegex.Matches(line);
            int severity = -1; // higher is more severe
            string level = null;
            foreach (Match m in matches)
            {
                var tok = m.Groups[1].Value.ToUpperInvariant();
                int cur = -1;
                if (tok == "FATAL") cur = 5;
                else if (tok == "ERROR") cur = 4;
                else if (tok == "WARN" || tok == "WARNING") cur = 3;
                else if (tok == "INFO") cur = 2;
                else if (tok == "DEBUG") cur = 1;
                else if (tok == "TRACE") cur = 0;
                if (cur > severity)
                {
                    severity = cur;
                    level = tok == "WARNING" ? "WARN" : tok;
                }
            }
            if (level != null) return level;

            // Fallback prefix detection
            var l = line.TrimStart();
            if (l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return "ERROR";
            if (l.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) || l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) return "WARN";
            if (l.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase)) return "DEBUG";
            if (l.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase)) return "TRACE";
            return "INFO";
        }

        private void Worker()
        {
            while (_running)
            {
                string line = null;
                try
                {
                    if (!_queue.TryTake(out line, 250))
                        continue;

                    // Duplicate suppression
                    if (IsDuplicate(line))
                        continue;

                    var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
                    var ev = new LogEvent
                    {
                        TimestampNs = nowNs,
                        Source = _source,
                        Level = DetectLevel(line),
                        Component = _component,
                        Message = line ?? string.Empty
                    };

                    // BaseId extraction heuristics:
                    // 1. JSON fragment: "base_id":"VALUE"
                    // 2. Plain token: base_id=VALUE
                    try
                    {
                        string baseId = null;
                        var jm = JsonBaseIdRegex.Match(line);
                        if (jm.Success)
                            baseId = jm.Groups["id"].Value.Trim();
                        if (string.IsNullOrWhiteSpace(baseId))
                        {
                            var pm = PlainBaseIdRegex.Match(line);
                            if (pm.Success)
                                baseId = pm.Groups[1].Value.Trim();
                        }
                        if (!string.IsNullOrWhiteSpace(baseId))
                        {
                            ev.BaseId = baseId;
                            var corr = CorrelationProvider.Get(baseId);
                            if (!string.IsNullOrEmpty(corr)) ev.Tags["correlation_id"] = corr;
                        }
                        if (ev.Level != "WARN" && line.IndexOf("[WARN]", StringComparison.OrdinalIgnoreCase) >= 0)
                            ev.Level = "WARN";
                        ev.Tags["normalized"] = "true";
                    }
                    catch { /* ignore parsing issues */ }

                    try
                    {
                        _ = _logging.LogAsync(ev);
                        _consecutiveFailures = 0;
                        _firstFailureAt = DateTime.MinValue;
                    }
                    catch (RpcException)
                    {
                        HandleFailure();
                    }
                    catch
                    {
                        HandleFailure();
                    }
                }
                catch
                {
                    // If dequeue or serialization throws, skip this line
                }
            }
        }

        private void HandleFailure()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_firstFailureAt == DateTime.MinValue)
                    _firstFailureAt = now;
                _consecutiveFailures++;
                if (_consecutiveFailures >= FailureThreshold && (now - _firstFailureAt) <= FailureWindow)
                {
                    _running = false; // stop loop; Console still writes to fallback
                }
                if ((now - _firstFailureAt) > FailureWindow)
                {
                    // reset window
                    _consecutiveFailures = 0;
                    _firstFailureAt = DateTime.MinValue;
                }
            }
            catch { /* ignore */ }
        }

        private static bool IsDuplicate(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false; // let blank lines through once
            try
            {
        // Normalize whitespace to improve duplicate detection across tiny formatting differences
        var key = NormalizeForDedup(line);
                var nowMs = NowMs();
                long last;
                if (_recent.TryGetValue(key, out last))
                {
                    var elapsed = nowMs - last;
                    if (elapsed >= 0 && elapsed < (long)DuplicateWindow.TotalMilliseconds)
                    {
                        return true;
                    }
                    _recent[key] = nowMs;
                }
                else
                {
                    _recent[key] = nowMs;
                }
                // Opportunistic prune
                if (_recent.Count > RecentPruneThreshold)
                {
                    foreach (var kvp in _recent)
                    {
                        if (nowMs - kvp.Value > 5_000) // older than 5s
                            _recent.TryRemove(kvp.Key, out _);
                    }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private static string NormalizeForDedup(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var t = s.Trim();
            // Collapse multiple spaces/tabs
            t = Regex.Replace(t, "\\t+", " ");
            t = Regex.Replace(t, "\\s{2,}", " ");
            return t;
        }

        private static long NowMs()
        {
            // Millisecond timestamp using UTC clock (sufficient for duplicate suppression window)
            return (long)(DateTime.UtcNow - new DateTime(1970,1,1)).TotalMilliseconds;
        }

        protected override void Dispose(bool disposing)
        {
            _running = false;
            try { _channel?.ShutdownAsync().Wait(1000); } catch { /* ignore */ }
            base.Dispose(disposing);
        }
    }
}
