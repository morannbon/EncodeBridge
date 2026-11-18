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
            return Migrate(DeserializeStore(result.Value.Value));
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

    // A successful Set is not enough to acknowledge a profile edit: read the
    // authoritative Host storage back and verify all editable fields.
    // Do not advance _revision or modify the in-memory Store on this read.
    public bool VerifyProfilePersisted(EncodeProfile expected)
    {
        try
        {
            var result = _storage.Get(Namespace, StoreKey);
            if (!result.Succeeded || result.Value is null) return false;
            var store = DeserializeStore(result.Value.Value);
            var actual = store.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.ProfileId, expected.ProfileId, StringComparison.OrdinalIgnoreCase));
            return actual is not null
                && string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                && actual.Enabled == expected.Enabled
                && string.Equals(actual.ToolId, expected.ToolId, StringComparison.Ordinal)
                && actual.VideoCodec == expected.VideoCodec
                && actual.Quality == expected.Quality
                && string.Equals(actual.Container, expected.Container, StringComparison.Ordinal)
                && actual.OutputDirectoryMode == expected.OutputDirectoryMode
                && string.Equals(actual.OutputDirectory, expected.OutputDirectory, StringComparison.Ordinal)
                && string.Equals(actual.SourceSubdirectoryName, expected.SourceSubdirectoryName, StringComparison.Ordinal)
                && actual.OutputDirectoryChoiceConfirmed == expected.OutputDirectoryChoiceConfirmed
                && actual.PostEncodeBatchEnabled == expected.PostEncodeBatchEnabled
                && string.Equals(actual.PostEncodeBatch, expected.PostEncodeBatch, StringComparison.Ordinal)
                && actual.PostEncodeBatchRiskAccepted == expected.PostEncodeBatchRiskAccepted;
        }
        catch (Exception) { return false; }
    }

    private static EncodeBridgeStore DeserializeStore(object? raw)
    {
        var store = raw switch
        {
            JsonElement e => e.Deserialize<EncodeBridgeStore>(JsonOptions),
            string value => JsonSerializer.Deserialize<EncodeBridgeStore>(value, JsonOptions),
            _ => JsonSerializer.Deserialize<EncodeBridgeStore>(JsonSerializer.Serialize(raw, JsonOptions), JsonOptions)
        };
        return store ?? throw new InvalidDataException("保存データを読み込めませんでした。");
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
