using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SonosControl.DAL.Models.Json;

namespace SonosControl.DAL.Models;

/// <summary>Captures immutable edits relative to the snapshot read by a caller.</summary>
public sealed class SettingsChanges
{
    private static readonly ConditionalWeakTable<SonosSettings, Baseline> Baselines = new();
    private static readonly JsonSerializerSettings Options = new()
    {
        Formatting = Formatting.Indented,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Converters = { new DateOnlyJsonConverter() }
    };
    private sealed record Baseline(JObject Json);
    private readonly JObject _before;
    private readonly JObject _after;

    private SettingsChanges(JObject before, JObject after) { _before = before; _after = after; }
    public static string Serialize(SonosSettings settings) => JsonConvert.SerializeObject(settings, Options);
    public static SonosSettings Deserialize(string json) => JsonConvert.DeserializeObject<SonosSettings>(json, Options)
        ?? throw new JsonSerializationException("A settings object is required.");

    public static SonosSettings Track(SonosSettings settings)
    {
        lock (Baselines)
        {
            Baselines.Remove(settings);
            Baselines.Add(settings, new Baseline(JObject.Parse(Serialize(settings))));
        }
        return settings;
    }

    public static SettingsChanges Capture(SonosSettings settings)
    {
        lock (Baselines)
        {
            if (!Baselines.TryGetValue(settings, out var baseline))
                throw new InvalidOperationException("Settings must be loaded before they can be edited.");
            return new SettingsChanges((JObject)baseline.Json.DeepClone(), JObject.Parse(Serialize(settings)));
        }
    }

    public static void Accept(SonosSettings settings, SettingsChanges changes)
    {
        lock (Baselines)
        {
            Baselines.Remove(settings);
            Baselines.Add(settings, new Baseline((JObject)changes._after.DeepClone()));
        }
    }

    public void Apply(SonosSettings current)
    {
        var json = Merge(JObject.Parse(Serialize(current)), _before, _after);
        JsonConvert.PopulateObject(json.ToString(), current, Options);
    }

    public static T MergeEntity<T>(T current, T original, T edited) where T : class
        => Merge(JToken.Parse(JsonConvert.SerializeObject(current, Options)),
            JToken.Parse(JsonConvert.SerializeObject(original, Options)),
            JToken.Parse(JsonConvert.SerializeObject(edited, Options))).ToObject<T>(JsonSerializer.Create(Options))!;

    private static JToken Merge(JToken? current, JToken? before, JToken? after)
    {
        if (JToken.DeepEquals(before, after)) return current?.DeepClone() ?? JValue.CreateNull();
        if (before is JObject oldObject && after is JObject newObject)
        {
            if (current is not JObject currentObject)
                throw new InvalidOperationException("The edited entry was removed. Reload before editing it.");
            var result = (JObject)currentObject.DeepClone();
            foreach (var property in newObject.Properties())
                result[property.Name] = Merge(result[property.Name], oldObject[property.Name], property.Value);
            foreach (var property in oldObject.Properties().Where(p => newObject[p.Name] is null))
                result.Remove(property.Name);
            return result;
        }
        if (before is JArray oldArray && after is JArray newArray && current is JArray currentArray)
        {
            var result = (JArray)currentArray.DeepClone();
            // Entity arrays merge by existing IDs, speaker addresses or media URLs.
            var key = oldArray.Concat(newArray).OfType<JObject>().Select(IdentityProperty).FirstOrDefault();
            if (key is not null)
            {
                string Identity(JToken item) => item[key]?.ToString() ?? throw new InvalidOperationException("An entry identity is required.");
                var comparison = key == "Url" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                JToken? Find(IEnumerable<JToken> items, string id) => items.FirstOrDefault(i => string.Equals(Identity(i), id, comparison));
                var removedItems = oldArray.Where(item => Find(newArray, Identity(item)) is null).ToList();
                var addedItems = newArray.Where(item => Find(oldArray, Identity(item)) is null).ToList();
                // URL, address and date edits locate the original entry before changing its identity.
                var renamed = key != "Id" && removedItems.Count == 1 && addedItems.Count == 1 && oldArray.Count == newArray.Count;
                foreach (var oldItem in oldArray)
                {
                    var edited = Find(newArray, Identity(oldItem));
                    if (edited is null && renamed) edited = addedItems[0];
                    var latest = Find(result, Identity(oldItem));
                    if (edited is null)
                    {
                        if (latest is null && newArray.Any(i => Find(oldArray, Identity(i)) is null))
                            throw new InvalidOperationException("The edited entry was removed. Reload before editing it.");
                        latest?.Remove();
                    }
                    else if (!JToken.DeepEquals(oldItem, edited))
                    {
                        if (latest is null) throw new InvalidOperationException("The edited entry was removed. Reload before editing it.");
                        if (renamed && ReferenceEquals(edited, addedItems[0]) && Find(result, Identity(edited)) is not null)
                            throw new InvalidOperationException("This entry already exists. Reload before editing it.");
                        latest.Replace(Merge(latest, oldItem, edited));
                    }
                }
                foreach (var added in renamed ? Enumerable.Empty<JToken>() : addedItems)
                {
                    var existing = Find(result, Identity(added));
                    if (existing is null) result.Add(added.DeepClone());
                    else throw new InvalidOperationException("This entry already exists. Reload before adding it.");
                }
            }
            else if (oldArray.Concat(newArray).Any(item => item is JObject))
            {
                // Ordered value objects (queue tracks) form one field.
                return newArray.DeepClone();
            }
            else
            {
                // Date/day sets retain independent additions and removals.
                foreach (var removed in oldArray.Where(i => !newArray.Any(n => JToken.DeepEquals(n, i))))
                    result.FirstOrDefault(i => JToken.DeepEquals(i, removed))?.Remove();
                foreach (var added in newArray.Where(i => !oldArray.Any(o => JToken.DeepEquals(o, i))))
                    if (!result.Any(i => JToken.DeepEquals(i, added))) result.Add(added.DeepClone());
            }
            return result;
        }
        return after?.DeepClone() ?? JValue.CreateNull();
    }

    private static string? IdentityProperty(JObject item)
        => new[] { "Id", "IpAddress", "SpeakerIp", "Url", "UserName", "Date" }.FirstOrDefault(name => item[name] is not null);
}
