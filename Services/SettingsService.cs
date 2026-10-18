using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TrayCer.Core.Models;
using TrayCer.Core.Monitoring;
using Windows.Storage;

namespace TrayCer.Services;

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app. Not for secrets: printer access
/// codes and keys live in <see cref="AccessCodeVault"/>.
/// </summary>
/// <remarks>
/// LocalSettings values must be small (8 KB each) WinRT primitives. Store enums as int, and lists
/// or records as JSON through a source-generated <c>JsonSerializerContext</c> (trim-safe) with
/// <see cref="GetJson"/> / <see cref="SetJson"/>. Anything larger belongs in a file under
/// <c>ApplicationData.Current.LocalFolder</c>.
/// </remarks>
internal static class SettingsService
{
    /// <summary>Raised on the thread that changed a setting the monitors care about.</summary>
    public static event EventHandler? MonitoringChanged;

    /// <summary>False until the first launch has shown the flyout.</summary>
    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    /// <summary>The printers, in the order the user added them. Secrets are not in here.</summary>
    public static IReadOnlyList<PrinterConfig> Printers
    {
        get => GetJson(nameof(Printers), SettingsJsonContext.Default.ListPrinterConfig)?
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && Enum.IsDefined(p.Kind))
            .ToList() ?? [];
        set => SetJson(nameof(Printers), [.. value], SettingsJsonContext.Default.ListPrinterConfig);
    }

    /// <summary>Ids of printers whose finished print hasn't been marked complete, so the tray stays green across restarts.</summary>
    public static IReadOnlyList<string> NeedsMarkComplete
    {
        get => GetJson(nameof(NeedsMarkComplete), SettingsJsonContext.Default.ListString) ?? [];
        set => SetJson(nameof(NeedsMarkComplete), [.. value], SettingsJsonContext.Default.ListString);
    }

    /// <summary>How often to check a printer while it prints, snapped to the offered choices.</summary>
    public static TimeSpan ActiveInterval
    {
        get => MonitorOptions.SanitizeActiveInterval(Get(nameof(ActiveInterval), MonitorOptions.DefaultActiveInterval.TotalSeconds));
        set
        {
            Set(nameof(ActiveInterval), MonitorOptions.SanitizeActiveInterval(value.TotalSeconds).TotalSeconds);
            MonitoringChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool NotifyWhenFinished
    {
        get => Get(nameof(NotifyWhenFinished), true);
        set => Set(nameof(NotifyWhenFinished), value);
    }

    /// <summary>Failed prints, pauses and "needs attention".</summary>
    public static bool NotifyAboutProblems
    {
        get => Get(nameof(NotifyAboutProblems), true);
        set => Set(nameof(NotifyAboutProblems), value);
    }

    public static T? GetJson<T>(string key, JsonTypeInfo<T> typeInfo)
    {
        if (Get<string?>(key, null) is not { } json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static void SetJson<T>(string key, T? value, JsonTypeInfo<T> typeInfo) =>
        Set(key, value is null ? null : JsonSerializer.Serialize(value, typeInfo));

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<PrinterConfig>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
