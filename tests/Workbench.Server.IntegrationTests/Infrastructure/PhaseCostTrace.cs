// Copyright (c) 2026 The White Stag Collection.
using System.Diagnostics;
using Xunit.Abstractions;

namespace Workbench.Server.IntegrationTests.Infrastructure;

// Temporary approved diagnostic: call sites supply only fixed labels, never SQL or inputs.
internal sealed class PhaseCostTrace : IDisposable
{
    private static readonly AsyncLocal<PhaseCostTrace?> Current = new();
    private readonly PhaseCostTrace? _previous;
    private readonly ITestOutputHelper _output;
    private readonly string _case;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _sequence;

    private PhaseCostTrace(ITestOutputHelper output, string label)
    {
        _previous = Current.Value;
        _output = output;
        _case = label;
    }

    internal static PhaseCostTrace Enable(ITestOutputHelper output, string label)
    {
        var trace = new PhaseCostTrace(output, label);
        Current.Value = trace;
        trace.Write("case", 0, "begin", 0);
        return trace;
    }

    internal static IDisposable? Measure(string label) => Current.Value is { } trace ? new Phase(trace, label) : null;

    private void Write(string label, int sequence, string state, long elapsed)
    {
        // A timed-out test can finish cleanup after its output sink closes. Logging must not change its result.
        try { _output.WriteLine($"PHASE_COST {_case} {label} {sequence} {state} elapsedMs={elapsed} caseMs={_clock.ElapsedMilliseconds}"); }
        catch { }
    }

    public void Dispose()
    {
        Write("case", 0, "end", _clock.ElapsedMilliseconds);
        Current.Value = _previous;
    }

    private sealed class Phase : IDisposable
    {
        private readonly PhaseCostTrace _trace;
        private readonly string _label;
        private readonly int _sequence;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        internal Phase(PhaseCostTrace trace, string label)
        {
            _trace = trace;
            _label = label;
            _sequence = Interlocked.Increment(ref trace._sequence);
            trace.Write(label, _sequence, "begin", 0);
        }

        public void Dispose() => _trace.Write(_label, _sequence, "end", _clock.ElapsedMilliseconds);
    }
}
