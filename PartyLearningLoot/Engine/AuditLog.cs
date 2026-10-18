using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// JSON-lines audit log with a background writer thread.
/// Records are built on the main thread (they need the game clock) and queued; the writer drains the queue every
/// two seconds, immediately after errors, and synchronously on shutdown.
/// <list type="bullet">
/// <item>Sequence numbers run on across files within one session (log level off and on again, rollover), so every
/// session forms one gap-free series.</item>
/// <item>Files roll over at 50 MB. Rotation keeps at most ten files but never deletes a file of the running session;
/// a single session is capped at <see cref="MaxSessionBytes"/> to protect the disk. Beyond the cap only essential
/// records (session frame, errors) are written, from a reserve of <see cref="EssentialReserveBytes"/>.</item>
/// </list>
/// </summary>
internal static class AuditLog
{
    public const int MaxFiles = 10;
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int FlushIntervalMs = 2000;
    public const int MaxQueuedLines = 250000;
    public const long MaxSessionBytes = 2L * MaxFiles * MaxFileBytes;
    public const long EssentialReserveBytes = 8L * 1024 * 1024;
    public const string FilePrefix = "audit_";
    public const string FileExtension = ".jsonl";

    private static readonly object fileLock = new object();
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static ConcurrentQueue<QueuedLine> queue = new ConcurrentQueue<QueuedLine>();
    private static AutoResetEvent wake;
    private static Thread writerThread;
    private static volatile bool stopping;
    private static StreamWriter stream;
    private static string directory;
    private static string baseName;
    private static int part;
    private static long fileBytes;
    private static long sequence;
    private static string sequenceSession;
    private static readonly HashSet<string> sessionFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static long sessionBytes;
    private static bool sessionCapReached;
    private static int queued;

    public static volatile bool IsOpen;
    public static AuditLevel Level { get; private set; } = AuditLevel.Off;
    public static string SessionId { get; private set; } = "-";
    public static string FilePath { get; private set; }
    public static long Written;
    public static long Dropped;
    public static long WriteErrors;

    public static bool Summary => IsOpen && Level >= AuditLevel.Summary;
    public static bool Detailed => IsOpen && Level >= AuditLevel.Detailed;

    private readonly struct QueuedLine
    {
        public readonly string Text;
        public readonly bool Essential;

        public QueuedLine(string _text, bool _essential)
        {
            Text = _text;
            Essential = _essential;
        }
    }

    /// <summary>Opens a new audit file for a session. Returns false when logging is off or no folder is writable.</summary>
    public static bool Open(string _modPath, AuditLevel _level, string _sessionId)
    {
        Close();
        Level = _level;
        SessionId = _sessionId ?? "-";
        if (_level == AuditLevel.Off)
        {
            return false;
        }
        if (!string.Equals(sequenceSession, SessionId, StringComparison.Ordinal))
        {
            // A new session starts a new series; reopening within the same session continues it.
            sequenceSession = SessionId;
            Interlocked.Exchange(ref sequence, 0);
            lock (fileLock)
            {
                sessionFiles.Clear();
                sessionBytes = 0;
                sessionCapReached = false;
            }
        }

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        baseName = FilePrefix + stamp;
        part = 1;
        foreach (string candidate in CandidateDirectories(_modPath))
        {
            try
            {
                // Normalized, so the rotation recognizes the files of this session (the game builds paths with '/').
                string full = Path.GetFullPath(candidate);
                Directory.CreateDirectory(full);
                directory = full;
                lock (fileLock)
                {
                    RotateOldFiles();
                    OpenStream();
                }
                break;
            }
            catch (Exception e)
            {
                Log.Warning(ModLog.Prefix + "Audit log folder '" + candidate + "' is not writable: " + e.Message);
                directory = null;
            }
        }
        if (directory == null)
        {
            Log.Error(ModLog.Prefix + "No writable folder for the audit log; auditing is disabled for this session.");
            return false;
        }

        Interlocked.Exchange(ref queued, 0);
        queue = new ConcurrentQueue<QueuedLine>();
        stopping = false;
        wake = new AutoResetEvent(false);
        writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "PartyLearningLoot-Audit" };
        writerThread.Start();
        IsOpen = true;
        return true;
    }

    /// <summary>Changes the level at runtime; switching from Off opens a file, switching to Off closes it.</summary>
    public static void SetLevel(AuditLevel _level, string _modPath)
    {
        if (_level == AuditLevel.Off)
        {
            Close();
            Level = AuditLevel.Off;
            return;
        }
        if (!IsOpen)
        {
            Open(_modPath, _level, SessionId);
            return;
        }
        Level = _level;
    }

    /// <summary>
    /// Starts a record with the common header. Must be called on the main thread for the game clock. A record begun
    /// while the log is closed gets sequence 0 and is never written, so it does not leave a gap in the series.
    /// </summary>
    public static JsonLineWriter Begin(string _type)
    {
        int? day = null;
        string time = null;
        try
        {
            World world = GameManager.Instance?.World;
            if (world != null && ThreadManager.IsMainThread())
            {
                ulong worldTime = world.worldTime;
                day = GameUtils.WorldTimeToDays(worldTime);
                time = AuditRecords.FormatGameTime(GameUtils.WorldTimeToHours(worldTime), GameUtils.WorldTimeToMinutes(worldTime));
            }
        }
        catch
        {
            // The game clock is informative only.
        }
        long seq = IsOpen ? Interlocked.Increment(ref sequence) : 0;
        return AuditRecords.BeginRecord(_type, seq, DateTime.UtcNow, SessionId, day, time);
    }

    /// <summary>Queues a finished record. Returns the sequence number of the record (0 when not written).</summary>
    public static long Commit(JsonLineWriter _record, bool _flushSoon = false)
    {
        if (!IsOpen || _record == null || _record.Sequence == 0)
        {
            return 0;
        }
        if (Interlocked.Increment(ref queued) > MaxQueuedLines)
        {
            Interlocked.Decrement(ref queued);
            Interlocked.Increment(ref Dropped);
            return 0;
        }
        string line = _record.Finish();
        queue.Enqueue(new QueuedLine(line, AuditRecords.IsEssential(_record.RecordType)));
        if (_flushSoon)
        {
            wake?.Set();
        }
        return _record.Sequence;
    }

    /// <summary>Synchronously writes everything that is queued.</summary>
    public static void Flush()
    {
        if (!IsOpen)
        {
            return;
        }
        Drain();
    }

    public static void Close()
    {
        if (!IsOpen && writerThread == null)
        {
            return;
        }
        IsOpen = false;
        stopping = true;
        try
        {
            wake?.Set();
            if (writerThread != null && writerThread.IsAlive && !writerThread.Join(5000))
            {
                Log.Warning(ModLog.Prefix + "Audit writer thread did not stop within 5 s.");
            }
        }
        catch (Exception e)
        {
            Log.Warning(ModLog.Prefix + "Stopping the audit writer failed: " + e.Message);
        }
        Drain();
        lock (fileLock)
        {
            try
            {
                stream?.Flush();
                stream?.Dispose();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref WriteErrors);
                Log.Warning(ModLog.Prefix + "Closing the audit file failed: " + e.Message);
            }
            stream = null;
        }
        writerThread = null;
        wake?.Dispose();
        wake = null;
    }

    // ----- writer -------------------------------------------------------------------------------

    private static void WriterLoop()
    {
        while (!stopping)
        {
            try
            {
                wake?.WaitOne(FlushIntervalMs);
                Drain();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref WriteErrors);
                Log.Warning(ModLog.Prefix + "Audit writer error: " + e.Message);
                Thread.Sleep(500);
            }
        }
    }

    private static void Drain()
    {
        lock (fileLock)
        {
            if (stream == null)
            {
                return;
            }
            bool wrote = false;
            while (queue.TryDequeue(out QueuedLine queuedLine))
            {
                Interlocked.Decrement(ref queued);
                string line = queuedLine.Text;
                try
                {
                    int bytes = Utf8NoBom.GetByteCount(line) + 1;
                    if (sessionBytes + bytes > MaxSessionBytes + (queuedLine.Essential ? EssentialReserveBytes : 0))
                    {
                        Interlocked.Increment(ref Dropped);
                        if (!sessionCapReached)
                        {
                            sessionCapReached = true;
                            Log.Error(ModLog.Prefix + $"The audit log of this session reached {MaxSessionBytes / (1024 * 1024)} MB; further records (except the session frame and errors) are dropped until the next session.");
                        }
                        continue;
                    }
                    if (fileBytes > MaxFileBytes)
                    {
                        RollOver();
                    }
                    stream.Write(line);
                    stream.Write('\n');
                    fileBytes += bytes;
                    sessionBytes += bytes;
                    Interlocked.Increment(ref Written);
                    wrote = true;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref WriteErrors);
                    Log.Warning(ModLog.Prefix + "Writing to the audit file failed: " + e.Message);
                    break;
                }
            }
            if (wrote)
            {
                try
                {
                    stream.Flush();
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref WriteErrors);
                    Log.Warning(ModLog.Prefix + "Flushing the audit file failed: " + e.Message);
                }
            }
        }
    }

    private static void OpenStream()
    {
        string name = baseName + (part > 1 ? "_part" + part : string.Empty) + FileExtension;
        FilePath = Path.GetFullPath(Path.Combine(directory, name));
        var fileStream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream = new StreamWriter(fileStream, Utf8NoBom) { AutoFlush = false, NewLine = "\n" };
        fileBytes = fileStream.Length;
        sessionFiles.Add(FilePath);
    }

    private static void RollOver()
    {
        stream.Flush();
        stream.Dispose();
        part++;
        RotateOldFiles();
        OpenStream();
    }

    /// <summary>
    /// Runs before a new file is opened: keeps the newest files of earlier sessions so that, together with the files of
    /// the running session and the new one, at most <see cref="MaxFiles"/> remain. Files of the running session are
    /// never deleted (a long Detailed session must keep its session_start and selftest).
    /// </summary>
    private static void RotateOldFiles()
    {
        try
        {
            FileInfo[] older = new DirectoryInfo(directory).GetFiles(FilePrefix + "*" + FileExtension)
                .Where(f => !sessionFiles.Contains(Path.GetFullPath(f.FullName)))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToArray();
            int keep = Math.Max(0, MaxFiles - 1 - sessionFiles.Count);
            for (int i = keep; i < older.Length; i++)
            {
                older[i].Delete();
            }
        }
        catch (Exception e)
        {
            Log.Warning(ModLog.Prefix + "Audit log rotation failed: " + e.Message);
        }
    }

    private static string[] CandidateDirectories(string _modPath)
    {
        string primary = string.IsNullOrEmpty(_modPath) ? null : Path.Combine(_modPath, "Logs");
        string fallback = null;
        try
        {
            fallback = Path.Combine(GameIO.GetUserGameDataDir(), "PartyLearningLoot", "Logs");
        }
        catch
        {
            // GameIO is unavailable in unit-test hosts; the primary folder is enough there.
        }
        return new[] { primary, fallback }.Where(d => !string.IsNullOrEmpty(d)).ToArray();
    }
}
