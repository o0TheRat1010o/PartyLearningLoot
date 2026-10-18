using System;
using System.Threading;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// Game-log output with the [PartyLearningLoot] prefix. Warnings and errors are mirrored into the audit log,
/// so the post-session analysis sees everything the server log shows.
/// </summary>
internal static class ModLog
{
    public const string Prefix = "[PartyLearningLoot] ";

    private static int warningCount;
    private static int errorCount;

    public static int WarningCount => warningCount;
    public static int ErrorCount => errorCount;

    public static void Info(string _message)
    {
        Log.Out(Prefix + _message);
    }

    public static void Warning(string _message, string _code = null)
    {
        Interlocked.Increment(ref warningCount);
        Log.Warning(Prefix + _message);
        if (AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Warning);
            record.Prop("code", _code ?? "general").Prop("message", _message);
            AuditLog.Commit(record);
        }
    }

    public static void Error(string _message, Exception _exception = null, string _code = null)
    {
        Interlocked.Increment(ref errorCount);
        Log.Error(Prefix + _message + (_exception != null ? " (" + _exception.GetType().Name + ": " + _exception.Message + ")" : string.Empty));
        if (_exception != null)
        {
            Log.Exception(_exception);
        }
        if (AuditLog.IsOpen)
        {
            JsonLineWriter record = AuditLog.Begin(AuditRecords.Error);
            record.Prop("code", _code ?? "general").Prop("message", _message);
            if (_exception != null)
            {
                record.Prop("exception", _exception.GetType().FullName)
                    .Prop("exception_message", _exception.Message)
                    .Prop("stack", _exception.ToString());
            }
            AuditLog.Commit(record, _flushSoon: true);
        }
    }

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref warningCount, 0);
        Interlocked.Exchange(ref errorCount, 0);
    }
}
