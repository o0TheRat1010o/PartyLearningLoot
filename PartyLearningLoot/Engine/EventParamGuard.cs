namespace PartyLearningLoot.Engine;

/// <summary>
/// Snapshot and restore of <see cref="MinEventParams"/> instances. Evaluating a member's perks runs vanilla code that
/// overwrites the shared <see cref="MinEventParams.CachedEventParam"/> and the member's own MinEventContext; both are
/// restored field by field afterwards so the opener's evaluation and the member entity are left exactly as before.
/// </summary>
internal static class EventParamGuard
{
    /// <summary>Copies every field, including the three that <see cref="MinEventParams.CopyTo"/> skips.</summary>
    public static void CopyAll(MinEventParams _source, MinEventParams _destination)
    {
        MinEventParams.CopyTo(_source, _destination);
        _destination.ParentType = _source.ParentType;
        _destination.Challenge = _source.Challenge;
        _destination.IsLocal = _source.IsLocal;
    }

    /// <summary>Field-wise equality (references for objects, values for structs).</summary>
    public static bool SameState(MinEventParams _a, MinEventParams _b)
    {
        return ReferenceEquals(_a.TileEntity, _b.TileEntity)
               && ReferenceEquals(_a.Self, _b.Self)
               && ReferenceEquals(_a.Instigator, _b.Instigator)
               && ReferenceEquals(_a.Other, _b.Other)
               && ReferenceEquals(_a.Others, _b.Others)
               && ReferenceEquals(_a.ItemValue, _b.ItemValue)
               && ReferenceEquals(_a.ItemActionData, _b.ItemActionData)
               && ReferenceEquals(_a.ItemInventoryData, _b.ItemInventoryData)
               && _a.StartPosition == _b.StartPosition
               && _a.Position == _b.Position
               && ReferenceEquals(_a.Transform, _b.Transform)
               && ReferenceEquals(_a.Buff, _b.Buff)
               && _a.BlockValue.rawData == _b.BlockValue.rawData
               && ReferenceEquals(_a.POI, _b.POI)
               && _a.Area == _b.Area
               && ReferenceEquals(_a.Biome, _b.Biome)
               && _a.Tags.Equals(_b.Tags)
               && ReferenceEquals(_a.ProgressionValue, _b.ProgressionValue)
               && ReferenceEquals(_a.Challenge, _b.Challenge)
               && _a.Seed == _b.Seed
               && _a.IsLocal == _b.IsLocal
               && _a.ParentType == _b.ParentType;
    }
}
