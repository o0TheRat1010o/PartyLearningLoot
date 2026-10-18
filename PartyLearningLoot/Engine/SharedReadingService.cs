using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Noemax.GZip;
using PartyLearningLoot.Core;

namespace PartyLearningLoot.Engine;

/// <summary>
/// Shared Reading: when a party member reads a book, magazine or schematic, every active member receives the effect
/// as if they had read it themselves.
///
/// Detection works without client code: the injected marker buff reports the read item id to the server (see
/// <see cref="ReadableXmlInjector"/>). For every active member the server then
/// <list type="bullet">
/// <item>remote member: adds the item's apply buff on that member's own client, which applies the learning effects
/// relative to its own authoritative state (no absolute values, so nothing can be lost or lowered), and sends the XP;</item>
/// <item>host member (listen server): runs the same effect groups directly on the local player.</item>
/// </list>
/// For a remote member the server only predicts the outcome on its copy (audit, message, client confirmation) and
/// restores that copy afterwards; the reader's own read reaches the server with the reader's next sync, as in vanilla.
/// Routing follows the vanilla shared kill XP: one targeted packet per member.
/// </summary>
internal static class SharedReadingService
{
    public const string MarkerBuff = ReadableXmlInjector.DefaultMarkerBuff;
    public const string ApplyBuffPrefix = ReadableXmlInjector.DefaultApplyBuffPrefix;
    public const double RateCapacity = 30d;
    public const double RateRefillPerSecond = 1d;

    private static readonly KeyedTokenBuckets<int> rateLimits = new KeyedTokenBuckets<int>(RateCapacity, RateRefillPerSecond);
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static string lastTableHash;

    /// <summary>True when the Shared Reading patch group is installed (setting at server start).</summary>
    public static bool Installed { get; set; }

    /// <summary>Category switches as they were at server start (they decide what is injected).</summary>
    public static InjectionOptions StartupOptions { get; set; } = new InjectionOptions();

    public static InjectionResult Injection { get; private set; }
    public static bool BuffInjected { get; private set; }
    public static bool BuffAlreadyPresent { get; private set; }
    public static int ApplyBuffsCached { get; private set; }
    public static int ServerPlaceholders { get; private set; }
    public static string InjectionError { get; private set; }
    public static int InjectionRuns { get; private set; }

    public static bool IsActive => Installed && Injection != null && Injection.Injected.Count > 0 && InjectionError == null && ApplyBuffsCached > 0;

    public static bool IsMarker(string _buffName)
    {
        return _buffName != null && _buffName.Length == MarkerBuff.Length && string.Equals(_buffName, MarkerBuff, StringComparison.OrdinalIgnoreCase);
    }

    // ----- XML injection (server, before the config is cached for clients) -------------------

    public static void OnBuffsXml(XDocument _buffs)
    {
        bool added = ReadableXmlInjector.InjectMarkerBuff(_buffs, MarkerBuff);
        BuffInjected = true;
        BuffAlreadyPresent = !added;
        ModLog.Info(added ? "Shared Reading: marker buff injected into buffs.xml." : "Shared Reading: marker buff already present in buffs.xml.");
        if (Injection != null)
        {
            // buffs.xml is reloaded on its own while the item table of this world is known: keep the apply buffs. The
            // server parses this document as well, so it knows the full apply buffs here instead of placeholders; it
            // still never adds one itself.
            ApplyBuffsCached = ReadableXmlInjector.InjectApplyBuffs(_buffs, Injection.ApplyBuffs, StartupOptions.ApplyBuffPrefix);
            ServerPlaceholders = ApplyBuffsCached;
            ModLog.Info($"Shared Reading: {ApplyBuffsCached} apply buffs kept in the reloaded buffs.xml.");
        }
    }

    /// <summary>
    /// items.xml is processed after buffs.xml, so the apply buffs (copies of the item effects) are added to the already
    /// cached client copy of buffs.xml here. The server itself never applies them; it only knows them as placeholders
    /// without effects, because the clients report their active buffs back.
    /// </summary>
    public static void OnItemsXml(XDocument _items)
    {
        InjectionResult result = ReadableXmlInjector.InjectItems(_items, StartupOptions);
        InjectionRuns++;
        if (lastTableHash != null && lastTableHash != result.TableHash)
        {
            ModLog.Warning($"Shared Reading: the item table changed during the session (hash {lastTableHash} -> {result.TableHash}); clients that joined earlier may report old ids until they reconnect.", "table_changed");
        }
        lastTableHash = result.TableHash;
        foreach (string warning in result.Warnings)
        {
            ModLog.Warning("Shared Reading: " + warning, "injection");
        }

        try
        {
            ApplyBuffsCached = PatchCachedBuffs(result.ApplyBuffs);
            ServerPlaceholders = RegisterServerPlaceholders(result.ApplyBuffs);
        }
        catch
        {
            // Without apply buffs no remote member could learn: take the markers out again, so no client reports reads.
            ReadableXmlInjector.RemoveAllMarkerGroups(_items, StartupOptions.MarkerBuffName);
            throw;
        }
        Injection = result;
        InjectionError = null;
        ModLog.Info($"Shared Reading: marker injected into {result.Injected.Count} readable items " +
                    $"({result.CountOf(ReadableCategory.Book)} books, {result.CountOf(ReadableCategory.Magazine)} magazines, {result.CountOf(ReadableCategory.Schematic)} schematics; " +
                    $"{result.SkippedByCategory} skipped by category), {ApplyBuffsCached} apply buffs added to the client buffs.xml, table {result.TableHash}.");
    }

    private static int PatchCachedBuffs(IReadOnlyList<XElement> _applyBuffs)
    {
        WorldStaticData.XmlLoadInfo info = WorldStaticData.xmlsToLoad.FirstOrDefault(x => x != null && x.XmlName == "buffs");
        if (info?.CompressedXmlData == null)
        {
            throw new InvalidOperationException("buffs.xml has no cached client copy to extend");
        }
        XmlFile xml;
        using (var input = new MemoryStream(info.CompressedXmlData))
        using (var inflater = new DeflateInputStream(input))
        {
            xml = new XmlFile(inflater);
        }
        int added = ReadableXmlInjector.InjectApplyBuffs(xml.XmlDoc, _applyBuffs, StartupOptions.ApplyBuffPrefix);
        // Same compression as the vanilla cache (level 3), so the clients receive it through the regular config sync.
        using (var memory = new MemoryStream())
        using (var deflater = new DeflateOutputStream(memory, 3))
        {
            ThreadManager.RunCoroutineSync(WorldStaticData.cacheSingleXml(info, xml, memory, deflater));
        }
        return added;
    }

    /// <summary>
    /// A member's client reports its active buffs to the server about every 5.5 s (NetPackageEntityStatsBuff) and in its
    /// saved player data, so an apply buff that is still running comes back. Unknown names would be logged as errors
    /// ("Buff Class not found"); placeholders with the same name and no effects let the server read them silently.
    /// </summary>
    private static int RegisterServerPlaceholders(IEnumerable<XElement> _applyBuffs)
    {
        if (BuffManager.Buffs == null)
        {
            throw new InvalidOperationException("buffs.xml is not loaded on the server");
        }
        int registered = 0;
        foreach (XElement applyBuff in _applyBuffs)
        {
            BuffsFromXml.ParseBuff(ReadableXmlInjector.CreatePlaceholderBuff(applyBuff));
            registered++;
        }
        return registered;
    }

    public static void OnInjectionFailed(Exception _exception)
    {
        InjectionError = _exception.GetType().Name + ": " + _exception.Message;
        Injection = null;
        ApplyBuffsCached = 0;
        ServerPlaceholders = 0;
    }

    public static void ResetSession()
    {
        rateLimits.Clear();
    }

    /// <summary>
    /// The configs of the next world are loaded (and injected) again; never keep the table of the previous world,
    /// otherwise a failed injection there would leave a stale table active.
    /// </summary>
    public static void OnWorldUnloaded()
    {
        Injection = null;
        InjectionError = null;
        BuffInjected = false;
        BuffAlreadyPresent = false;
        ApplyBuffsCached = 0;
        ServerPlaceholders = 0;
        lastTableHash = null;
    }

    // ----- read detection ---------------------------------------------------------------------

    /// <summary>A remote client reported the marker buff (NetPackageAddRemoveBuff).</summary>
    public static void OnRemoteMarker(ClientInfo _sender, int _entityId, float _duration)
    {
        World world = GameManager.Instance?.World;
        if (world == null)
        {
            return;
        }
        if (_sender == null || _sender.entityId != _entityId)
        {
            Reject(null, _entityId, _duration, "remote", "sender_mismatch", $"marker for entity {_entityId} sent by {_sender?.playerName ?? "unknown"} ({_sender?.entityId.ToString() ?? "?"})");
            return;
        }
        if (!(world.GetEntity(_entityId) is EntityPlayer reader))
        {
            Reject(null, _entityId, _duration, "remote", "reader_not_found", null);
            return;
        }
        OnMarker(reader, _duration, "remote");
    }

    /// <summary>The host's own player read an item (listen server).</summary>
    public static void OnHostMarker(EntityPlayer _reader, float _duration)
    {
        OnMarker(_reader, _duration, "host");
    }

    private static void OnMarker(EntityPlayer _reader, float _duration, string _source)
    {
        if (!IsActive)
        {
            Reject(_reader, _reader.entityId, _duration, _source, "inactive", InjectionError);
            return;
        }
        if (!ReadableXmlInjector.TryDecodeDuration(_duration, out int id) || !Injection.ById.TryGetValue(id, out ReadableItem item))
        {
            Reject(_reader, _reader.entityId, _duration, _source, "unknown_id", $"duration {_duration}");
            return;
        }
        ItemClass itemClass = ItemClass.GetItemClass(item.Name);
        if (itemClass == null)
        {
            Reject(_reader, _reader.entityId, _duration, _source, "item_not_loaded", item.Name);
            return;
        }
        if (!rateLimits.TryTake(_reader.entityId, clock.Elapsed.TotalSeconds))
        {
            RuntimeStats.SharedReadsRateLimited++;
            ModLog.Warning($"Shared Reading: {PartySnapshot.DisplayName(_reader)} exceeded the read rate limit, '{item.Name}' was not shared.", "rate_limited");
            if (AuditLog.IsOpen)
            {
                JsonLineWriter record = AuditLog.Begin(AuditRecords.RateLimited);
                record.BeginObject("reader").Prop("id", _reader.entityId).Prop("name", PartySnapshot.DisplayName(_reader)).EndObject()
                    .Prop("item", item.Name)
                    .Prop("source", _source);
                AuditLog.Commit(record);
            }
            return;
        }
        Process(_reader, item, itemClass, _source);
    }

    private static void Process(EntityPlayer _reader, ReadableItem _item, ItemClass _itemClass, string _source)
    {
        var watch = Stopwatch.StartNew();
        ModSettings settings = PartyLearningLootMod.Settings;
        RuntimeStats.SharedReads++;
        string applyBuff = Injection.ApplyBuffById.TryGetValue(_item.Id, out string name) ? name : null;
        bool inventorySeen = InventoryContains(_reader, _itemClass);

        PartySnapshot snapshot = PartySnapshot.Capture(_reader, settings.SharedReadingMaxDistance, settings.IncludeDeadMembers, settings.IncludeSpectators);
        var results = new List<MemberReadResult>();
        if (snapshot.VanillaReason == null)
        {
            string message = settings.FormatSharedReadingMessage(PartySnapshot.DisplayName(_reader), LocalizedName(_itemClass));
            foreach (PartyMember member in snapshot.Members)
            {
                MemberReadResult result;
                try
                {
                    // Remote: side-effect-free prediction, the client applies the read itself. Host: applied directly.
                    result = SharedReadExecutor.Apply(member.Entity, member.Candidate.Distance, _itemClass, settings);
                    if (!result.IsHost)
                    {
                        SharedReadExecutor.SendApply(result, applyBuff);
                    }
                    if (result.Learned)
                    {
                        RuntimeStats.SharedMemberLearnings++;
                        if (settings.SharedReadingNotify)
                        {
                            SharedReadExecutor.Notify(result, message);
                        }
                    }
                    if (result.Xp > 0)
                    {
                        RuntimeStats.SharedXpGrants++;
                    }
                }
                catch (Exception e)
                {
                    result = new MemberReadResult
                    {
                        Member = member.Entity,
                        Name = member.Candidate.Name,
                        Distance = member.Candidate.Distance,
                        IsHost = !member.Entity.isEntityRemote,
                        Predicted = member.Entity.isEntityRemote,
                        Error = e.GetType().Name + ": " + e.Message
                    };
                    ModLog.Error($"Shared Reading failed for member {member.Candidate.Name} ({_item.Name}).", e, "shared_read_member");
                }
                results.Add(result);
            }
        }

        bool anyLearned = results.Any(r => r.Learned);
        long seq = 0;
        if (AuditLog.IsOpen && (AuditLog.Detailed || snapshot.VanillaReason == null))
        {
            seq = WriteRecord(_reader, _item, _source, snapshot, results, applyBuff, inventorySeen, watch.Elapsed.TotalMilliseconds);
        }
        foreach (MemberReadResult result in results.Where(r => !r.IsHost && r.Error == null))
        {
            SyncConfirmationTracker.Expect(result, seq, _item.Name);
        }
        if (anyLearned)
        {
            ModLog.Info($"Shared Reading: {PartySnapshot.DisplayName(_reader)} read {_item.Name}; learned by {string.Join(", ", results.Where(r => r.Learned).Select(r => r.Name))}.");
        }
    }

    /// <summary>
    /// Informational check whether the server's copy of the reader's toolbelt or backpack holds the item when the marker
    /// arrives. Not a rejection reason: when the last copy was read, the client's inventory update can arrive before the
    /// marker, so false is legitimate; a long run of false values for one reader is worth a look in the audit.
    /// </summary>
    private static bool InventoryContains(EntityPlayer _reader, ItemClass _itemClass)
    {
        try
        {
            var itemValue = new ItemValue(_itemClass.Id);
            return (_reader.inventory?.GetItemCount(itemValue) ?? 0) > 0 || (_reader.bag?.GetItemCount(itemValue) ?? 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static long WriteRecord(EntityPlayer _reader, ReadableItem _item, string _source, PartySnapshot _snapshot, List<MemberReadResult> _results,
        string _applyBuff, bool _inventorySeen, double _ms)
    {
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SharedRead);
        record.BeginObject("reader").Prop("id", _reader.entityId).Prop("name", PartySnapshot.DisplayName(_reader)).EndObject()
            .BeginObject("item").Prop("name", _item.Name).Prop("id", _item.Id).Prop("category", ReadableItem.CategoryCode(_item.Category)).EndObject()
            .Prop("source", _source)
            .Prop("apply_buff", _applyBuff)
            .Prop("inventory_seen", _inventorySeen)
            .BeginObject("validation").Prop("ok", true).EndObject();
        if (_snapshot.VanillaReason != null)
        {
            record.Prop("reason", _snapshot.VanillaReason);
        }
        record.BeginArray("results");
        foreach (MemberReadResult result in _results)
        {
            record.BeginObject()
                .Prop("id", result.Member.entityId)
                .Prop("name", result.Name)
                .Prop("dist", PartySnapshot.Round(result.Distance))
                .Prop("host", result.IsHost)
                .Prop("predicted", result.Predicted)
                .Prop("learned", result.Learned);
            WriteChanges(record, result);
            record.Prop("xp", result.Xp)
                .PropStrings("packets", result.Packets)
                .Prop("message", result.MessageSent)
                .PropStrings("skipped_groups", result.SkippedGroups)
                .PropStrings("skipped_actions", result.SkippedActions);
            if (result.Error != null)
            {
                record.Prop("error", result.Error);
            }
            record.EndObject();
        }
        record.EndArray();
        _snapshot.WriteMembers(record, true);
        record.Prop("ms", Math.Round(_ms, 3));
        return AuditLog.Commit(record);
    }

    private static void WriteChanges(JsonLineWriter _record, MemberReadResult _result)
    {
        _record.BeginArray("levels");
        foreach (KeyValuePair<string, int[]> change in _result.LevelChanges)
        {
            _record.BeginObject().Prop("name", change.Key).Prop("before", change.Value[0]).Prop("after", change.Value[1]).EndObject();
        }
        _record.EndArray();
        _record.BeginArray("cvars");
        foreach (KeyValuePair<string, float[]> change in _result.CVarChanges)
        {
            _record.BeginObject().Prop("name", change.Key).Prop("before", change.Value[0]).Prop("after", change.Value[1])
                .Prop("learning", _result.LearningCVars.Contains(change.Key)).EndObject();
        }
        _record.EndArray();
    }

    private static void Reject(EntityPlayer _reader, int _entityId, float _duration, string _source, string _reason, string _detail)
    {
        RuntimeStats.SharedReadsRejected++;
        ModLog.Warning($"Shared Reading: marker from entity {_entityId} rejected ({_reason}{(_detail != null ? ": " + _detail : string.Empty)}).", "shared_read_rejected");
        if (!AuditLog.IsOpen)
        {
            return;
        }
        JsonLineWriter record = AuditLog.Begin(AuditRecords.SharedRead);
        record.BeginObject("reader").Prop("id", _entityId).Prop("name", _reader != null ? PartySnapshot.DisplayName(_reader) : null).EndObject()
            .Prop("source", _source)
            .Prop("duration", _duration)
            .BeginObject("validation").Prop("ok", false).Prop("reason", _reason).Prop("detail", _detail).EndObject();
        AuditLog.Commit(record);
    }

    private static string LocalizedName(ItemClass _item)
    {
        try
        {
            string name = _item.GetLocalizedItemName();
            return string.IsNullOrEmpty(name) ? _item.Name : name;
        }
        catch
        {
            return _item.Name;
        }
    }

    /// <summary>Summary of the injection for session_start, status and selftest.</summary>
    public static void WriteInjection(JsonLineWriter _record)
    {
        _record.BeginObject("shared_reading")
            .Prop("installed", Installed)
            .Prop("active", IsActive)
            .Prop("marker_buff", MarkerBuff)
            .Prop("buff_injected", BuffInjected)
            .Prop("buff_already_present", BuffAlreadyPresent)
            .Prop("apply_buffs", ApplyBuffsCached)
            .Prop("server_placeholders", ServerPlaceholders)
            .Prop("injection_runs", InjectionRuns);
        if (Injection != null)
        {
            _record.Prop("items", Injection.Injected.Count)
                .Prop("books", Injection.CountOf(ReadableCategory.Book))
                .Prop("magazines", Injection.CountOf(ReadableCategory.Magazine))
                .Prop("schematics", Injection.CountOf(ReadableCategory.Schematic))
                .Prop("skipped_by_category", Injection.SkippedByCategory)
                .Prop("skipped_without_effects", Injection.SkippedWithoutEffects)
                .Prop("table_hash", Injection.TableHash);
        }
        if (InjectionError != null)
        {
            _record.Prop("error", InjectionError);
        }
        _record.EndObject();
    }
}
