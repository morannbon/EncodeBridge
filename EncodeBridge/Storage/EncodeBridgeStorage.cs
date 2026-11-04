using System.Text.Json;
using TvAIrPlugin.Storage;
using TvAIrPlugin.Runtime;
using EncodeBridge.Domain;

namespace EncodeBridge.Storage;

internal sealed class EncodeBridgeStorage
{
    private const string Namespace = "encodebridge";
    private const string StoreKey = "state";
    private readonly ITvAirPluginStorageApi _storage;
    private long? _revision;

    public EncodeBridgeStorage(ITvAirPluginStorageApi storage) => _storage = storage;

    public EncodeBridgeStore Load()
    {
        var result = _storage.Get(Namespace, StoreKey);
        // Only a confirmed missing key represents first use. Storage failures must
        // never reset configuration, history or the finalized-recording cursor.
        if (!result.Succeeded)
        {
            if (result.Error?.Code == TvAirErrorCode.EntityNotFound)
                return new EncodeBridgeStore();
            throw new InvalidDataException("EncodeBridgeの保存データを読み込めませんでした。自動変換は開始しません。");
        }
        if (result.Value is null)
            throw new InvalidDataException("EncodeBridgeの保存データを読み込めませんでした。自動変換は開始しません。");
        _revision = result.Value.Revision;
        try
        {
            var raw = result.Value.Value;
            EncodeBridgeStore? store = raw switch
            {
                JsonElement e => e.Deserialize<EncodeBridgeStore>(JsonOptions),
                string s => JsonSerializer.Deserialize<EncodeBridgeStore>(s, JsonOptions),
                _ => JsonSerializer.Deserialize<EncodeBridgeStore>(JsonSerializer.Serialize(raw, JsonOptions), JsonOptions)
            };
            if (store is null) throw new InvalidDataException("保存データを読み込めませんでした。");
            return Migrate(store);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("EncodeBridgeの保存データを読み込めませんでした。自動変換は開始しません。", ex);
        }
    }

    public void Save(EncodeBridgeStore store)
    {
        store.SchemaVersion = EncodeBridgeDefaults.SchemaVersion;
        var payload = JsonSerializer.SerializeToElement(store, JsonOptions);
        var result = _storage.Set(Namespace, StoreKey, payload, _revision);
        if (!result.Succeeded || result.Value is null)
            throw new InvalidOperationException(result.Error?.Message ?? "EncodeBridgeの設定を保存できませんでした。");
        _revision = result.Value.Revision;
    }

    private static EncodeBridgeStore Migrate(EncodeBridgeStore store)
    {
        if (store.SchemaVersion == EncodeBridgeDefaults.SchemaVersion) return store;
        if (store.SchemaVersion == 1)
        {
            foreach (var profile in store.Profiles)
                profile.PostEncodeBatchRiskAccepted = profile.PostEncodeBatchEnabled && store.Settings.PostEncodeBatchRiskAccepted;
            store.SchemaVersion = EncodeBridgeDefaults.SchemaVersion;
            return store;
        }
        throw new InvalidDataException($"未対応の保存形式です: {store.SchemaVersion}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}
