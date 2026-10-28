using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KarSlides.Core.Clicker;
using KarSlides.Core.Configuration;

namespace KarSlides.Control;

/// <summary>
/// Monitor: a list in the options (one entry per slot), chosen from /api/monitors in the UI.
/// AudioDevice: "" (Windows default) or an MMDevice id, chosen from /api/audio/devices.
/// </summary>
public enum SettingKind { Bool, Int, Number, Text, Choice, List, Monitor, AudioDevice }

/// <summary>One setting editable in the web UI. <see cref="Path"/> is the property path in <see cref="KarSlidesOptions"/>.</summary>
public sealed record SettingField(string Path, string Group, string Label, SettingKind Kind, string? Help = null,
    double? Min = null, double? Max = null, IReadOnlyList<string>? Choices = null, bool Danger = false);

/// <summary>
/// Settings the operator may change from the panel. Deliberately not here: output size (fixed),
/// send mode, slot count, internal timings – they stay in appsettings.json.
/// </summary>
public static class SettingsCatalog
{
    private const string KeyHelp = "Через запятую: PageDown, PageUp, Right, Left, Space, B, F5, OemPeriod…";

    public static readonly IReadOnlyList<SettingField> Fields =
    [
        new("Output.Fps", "Выход NDI®", "Частота кадров", SettingKind.Choice, "Как в проекте vMix. Об NDI: https://ndi.video/",
            Choices: ["25", "29.97", "30", "50", "59.94", "60"]),
        new("Output.Source", "Выход NDI®", "Источник", SettingKind.Choice,
            "capture – показ PowerPoint; testpattern – тестовая таблица для проверки связи с vMix",
            Choices: ["capture", "testpattern"]),
        new("Slots.NdiNameTemplate", "Выход NDI®", "Имя источника NDI", SettingKind.Text, "{n} – номер слота. После смены – заново добавить инпут в vMix"),
        new("Slots.Monitors", "Показ", "Монитор показа", SettingKind.Monitor,
            "Виртуальный монитор, HDMI-заглушка или отдельный монитор, 1920×1080. Не тот, куда vMix выводит Fullscreen"),
        new("Show.Topmost", "Показ", "Окно показа поверх всех окон", SettingKind.Bool, "Случайное окно на мониторе показа не попадёт в эфир"),
        new("Show.Focus", "Показ", "Защита фокуса vMix", SettingKind.Choice,
            "both – рекомендуется; none – только для диагностики", Choices: ["both", "restore", "noactivate", "none"]),
        new("Preflight.Thumbnails", "Показ", "Миниатюры слайдов при загрузке", SettingKind.Bool),

        new("Preview.Live", "Живая картинка в пульте", "Включена", SettingKind.Bool, "Анимации и видео на пульте и у спикера"),
        new("Preview.Fps", "Живая картинка в пульте", "Кадров в секунду", SettingKind.Number, "Меньше – меньше трафик и нагрузка", 1, 30),
        new("Preview.Width", "Живая картинка в пульте", "Ширина, px", SettingKind.Choice, Choices: ["480", "640", "960", "1280"]),
        new("Preview.Quality", "Живая картинка в пульте", "Качество JPEG", SettingKind.Int, null, 30, 95),

        new("Audio.Enabled", "Звук", "Звук PowerPoint в NDI", SettingKind.Bool),
        new("Audio.BufferMs", "Звук", "Буфер, мс", SettingKind.Number, "Запас после опустошения буфера", 5, 200),
        new("Audio.OutputDevice", "Звук", "Куда PowerPoint играет звук здесь", SettingKind.AudioDevice,
            "Динамики или наушники этого компьютера. В NDI звук идёт в любом случае. Чтобы в комнате было тихо – " +
            "устройство, которое никуда не выводит"),

        new("Clicker.Enabled", "Кликер", "Включён при запуске", SettingKind.Bool),
        new("Clicker.Next", "Кликер", "Вперёд", SettingKind.List, KeyHelp),
        new("Clicker.Prev", "Кликер", "Назад", SettingKind.List, KeyHelp),
        new("Clicker.Black", "Кликер", "Black", SettingKind.List, "Пусто – без клавиши"),
        new("Clicker.Swallow", "Кликер", "Не пропускать в другие программы", SettingKind.List,
            "Эти клавиши не дойдут до vMix, пока кликер включён"),

        new("Vmix.Enabled", "vMix", "Связь с vMix (tally)", SettingKind.Bool),
        new("Vmix.Host", "vMix", "Адрес", SettingKind.Text, "auto – vMix на этом компьютере"),
        new("Vmix.HttpPort", "vMix", "Порт Web Controller", SettingKind.Int, "0 – найти автоматически", 0, 65535),
        new("Vmix.TcpPort", "vMix", "Порт TCP API", SettingKind.Int, null, 1, 65535),

        new("Paths.Inbox", "Папки", "Папка входящих презентаций", SettingKind.Text),
        new("Paths.Cache", "Папки", "Кэш", SettingKind.Text, "Копии загруженных файлов и миниатюры"),

        new("Watchdog.Enabled", "Сторожевой таймер", "Включён", SettingKind.Bool, "Перезапуск зависшего PowerPoint"),
        new("Watchdog.MissedLimit", "Сторожевой таймер", "Пропусков до перезапуска", SettingKind.Int, null, 1, 30),
        new("Watchdog.ComTimeoutMs", "Сторожевой таймер", "Таймаут команды PowerPoint, мс", SettingKind.Int, null, 500, 60000),
        new("Watchdog.RestartPausedShow", "Сторожевой таймер", "Перезапускать показ, поставленный на паузу", SettingKind.Bool,
            "Документ, открытый в PowerPoint, ставит показ на паузу, видео не играет. Когда документ закроют – " +
            "показ перезапустится на той же позиции, в эфире на это время стоп-кадр"),

        new("Logging.Level", "Журнал", "Подробность", SettingKind.Choice, "Debug – для разбора проблем",
            Choices: ["Verbose", "Debug", "Information", "Warning", "Error"]),
        new("Logging.RetainedDays", "Журнал", "Хранить дней", SettingKind.Int, null, 1, 365),

        new("Control.Port", "Доступ к пульту", "Порт", SettingKind.Int,
            "После перезапуска пульт откроется на новом порту. Не 8088 и не 8099 (vMix)", 1024, 65535, Danger: true),
        new("Control.Token", "Доступ к пульту", "Токен", SettingKind.Text,
            "Если задан, адрес пульта – http://…:порт/?token=…  Пусто – без токена", Danger: true),
    ];

    private const string KeyHelpEn = "Comma separated: PageDown, PageUp, Right, Left, Space, B, F5, OemPeriod…";

    /// <summary>English group, label and help of every field (the catalog above is the Russian UI).</summary>
    private static readonly Dictionary<string, (string Group, string Label, string? Help)> English = new()
    {
        ["Output.Fps"] = ("NDI® output", "Frame rate", "As in the vMix project. About NDI: https://ndi.video/"),
        ["Output.Source"] = ("NDI® output", "Source", "capture – the PowerPoint show; testpattern – a test pattern to check the link to vMix"),
        ["Slots.NdiNameTemplate"] = ("NDI® output", "NDI source name", "{n} – slot number. After a change, add the input in vMix again"),
        ["Slots.Monitors"] = ("Show", "Show monitor",
            "A virtual monitor, an HDMI dummy plug or a separate monitor, 1920×1080. Not the one vMix uses for Fullscreen"),
        ["Show.Topmost"] = ("Show", "Show window on top of all windows", "A stray window on the show monitor never gets on air"),
        ["Show.Focus"] = ("Show", "vMix focus protection", "both – recommended; none – for diagnostics only"),
        ["Preflight.Thumbnails"] = ("Show", "Slide thumbnails on load", null),
        ["Preview.Live"] = ("Live picture in the panel", "Enabled", "Animations and video in the panel and on the presenter page"),
        ["Preview.Fps"] = ("Live picture in the panel", "Frames per second", "Lower – less traffic and load"),
        ["Preview.Width"] = ("Live picture in the panel", "Width, px", null),
        ["Preview.Quality"] = ("Live picture in the panel", "JPEG quality", null),
        ["Audio.Enabled"] = ("Sound", "PowerPoint sound in NDI", null),
        ["Audio.BufferMs"] = ("Sound", "Buffer, ms", "Margin after the buffer ran dry"),
        ["Audio.OutputDevice"] = ("Sound", "Where PowerPoint plays sound here",
            "Speakers or headphones of this computer. NDI gets the sound anyway. For silence in the room – a device that outputs nowhere"),
        ["Clicker.Enabled"] = ("Clicker", "On at start", null),
        ["Clicker.Next"] = ("Clicker", "Next", KeyHelpEn),
        ["Clicker.Prev"] = ("Clicker", "Previous", KeyHelpEn),
        ["Clicker.Black"] = ("Clicker", "Black", "Empty – no key"),
        ["Clicker.Swallow"] = ("Clicker", "Keep from other programs", "These keys do not reach vMix while the clicker is on"),
        ["Vmix.Enabled"] = ("vMix", "Connection to vMix (tally)", null),
        ["Vmix.Host"] = ("vMix", "Address", "auto – vMix on this computer"),
        ["Vmix.HttpPort"] = ("vMix", "Web Controller port", "0 – find automatically"),
        ["Vmix.TcpPort"] = ("vMix", "TCP API port", null),
        ["Paths.Inbox"] = ("Folders", "Inbox folder for presentations", null),
        ["Paths.Cache"] = ("Folders", "Cache", "Copies of loaded files and thumbnails"),
        ["Watchdog.Enabled"] = ("Watchdog", "Enabled", "Restarts a hung PowerPoint"),
        ["Watchdog.MissedLimit"] = ("Watchdog", "Missed heartbeats before a restart", null),
        ["Watchdog.ComTimeoutMs"] = ("Watchdog", "PowerPoint command timeout, ms", null),
        ["Watchdog.RestartPausedShow"] = ("Watchdog", "Restart a show paused by PowerPoint",
            "A document opened in PowerPoint pauses the show, videos stop. When the document is closed, the show restarts " +
            "at the same position; the output holds the last frame meanwhile"),
        ["Logging.Level"] = ("Log", "Detail", "Debug – to investigate problems"),
        ["Logging.RetainedDays"] = ("Log", "Keep days", null),
        ["Control.Port"] = ("Panel access", "Port", "After the restart the panel opens on the new port. Not 8088 or 8099 (vMix)"),
        ["Control.Token"] = ("Panel access", "Token", "If set, the panel address is http://…:port/?token=…  Empty – no token"),
    };

    /// <summary>UI language of a request: "en…" is English, anything else Russian.</summary>
    public static bool IsEnglish(string? lang) => lang?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Group, label and help of a field in the UI language.</summary>
    public static (string Group, string Label, string? Help) Text(SettingField f, string? lang) =>
        IsEnglish(lang) && English.TryGetValue(f.Path, out var en) ? en : (f.Group, f.Label, f.Help);

    /// <summary>Fields without an English text (a test keeps this empty).</summary>
    public static IEnumerable<string> MissingEnglish => Fields.Select(f => f.Path).Where(p => !English.ContainsKey(p));

    public static SettingField? Find(string path) => Fields.FirstOrDefault(f => f.Path == path);

    /// <summary>Value of a field in an options object as JSON (bool, number, string, string array, null).</summary>
    public static JsonNode? Get(KarSlidesOptions options, SettingField field)
    {
        object? value = options;
        foreach (var name in field.Path.Split('.'))
            value = value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        return value switch
        {
            null => null,
            List<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            int i when field.Kind == SettingKind.Choice => JsonValue.Create(i.ToString(CultureInfo.InvariantCulture)),
            _ => JsonSerializer.SerializeToNode(value),
        };
    }

    /// <summary>Checks and normalizes a value from the UI; throws <see cref="ArgumentException"/> with a message for the operator.</summary>
    public static JsonNode? Normalize(SettingField field, JsonElement value, string? lang = null)
    {
        var english = IsEnglish(lang);
        ArgumentException Bad(string ru, string en) =>
            new(english ? $"\"{Text(field, lang).Label}\": {en}" : $"«{field.Label}»: {ru}");
        switch (field.Kind)
        {
            case SettingKind.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("нужно да/нет", "yes or no expected");
                return JsonValue.Create(value.GetBoolean());

            case SettingKind.Int:
            case SettingKind.Number:
            {
                double d;
                if (value.ValueKind == JsonValueKind.Number) d = value.GetDouble();
                else if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString()!.Trim().Replace(',', '.'),
                             NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) d = parsed;
                else throw Bad("нужно число", "a number expected");
                if (field.Kind == SettingKind.Int && d != Math.Floor(d)) throw Bad("нужно целое число", "a whole number expected");
                if (field.Min is { } min && d < min || field.Max is { } max && d > max) throw Bad($"допустимо {field.Min}…{field.Max}", $"allowed {field.Min}…{field.Max}");
                if (field.Path == "Control.Port" && d is 8088 or 8099) throw Bad("порты 8088 и 8099 занимает vMix", "ports 8088 and 8099 belong to vMix");
                return field.Kind == SettingKind.Int ? JsonValue.Create((int)d) : JsonValue.Create(d);
            }

            case SettingKind.Text:
            {
                var s = value.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => value.GetString()!.Trim(),
                    _ => throw Bad("нужен текст", "text expected"),
                };
                if (field.Path == "Control.Token") return s.Length == 0 ? null : JsonValue.Create(s);
                if (s.Length == 0) throw Bad("не может быть пустым", "cannot be empty");
                if (field.Path == "Slots.NdiNameTemplate" && !s.Contains("{n}")) throw Bad("должно содержать {n}", "must contain {n}");
                return JsonValue.Create(s);
            }

            case SettingKind.Choice:
            {
                var s = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (s is null || !field.Choices!.Contains(s)) throw Bad($"одно из: {string.Join(", ", field.Choices!)}", $"one of: {string.Join(", ", field.Choices!)}");
                // Numeric choices are numbers in the options (Preview.Width); Output.Fps is a string there.
                return field.Path == "Preview.Width" ? JsonValue.Create(int.Parse(s, CultureInfo.InvariantCulture)) : JsonValue.Create(s);
            }

            case SettingKind.List:
            {
                IEnumerable<string> items = value.ValueKind switch
                {
                    JsonValueKind.Array => value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : ""),
                    JsonValueKind.String => value.GetString()!.Split([',', ';', '\n']),
                    _ => throw Bad("нужен список", "a list expected"),
                };
                var list = items.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (field.Path.StartsWith("Clicker.", StringComparison.Ordinal))
                {
                    foreach (var k in list)
                    {
                        try { VirtualKeys.Parse(k); }
                        catch (FormatException) { throw Bad($"неизвестная клавиша «{k}»", $"unknown key \"{k}\""); }
                    }
                }
                if (field.Path == "Slots.Monitors" && list.Count == 0) throw Bad("нужен хотя бы один монитор", "at least one monitor expected");
                return new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            }

            case SettingKind.Monitor:
            {
                // "virtual", "virtual:2", a DeviceID (part) or \\.\DISPLAYn. A monitor that is not connected now
                // (a dummy plug put in later) may still be chosen: start-up reports it if it is missing.
                var spec = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()!.Trim(),
                    JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String => value[0].GetString()!.Trim(),
                    _ => throw Bad("нужен монитор", "a monitor expected"),
                };
                if (spec.Length == 0) throw Bad("нужен монитор", "a monitor expected");
                if (spec.StartsWith("virtual", StringComparison.OrdinalIgnoreCase) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(spec, @"^virtual(:[1-9]\d*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw Bad("ожидается virtual или virtual:N", "virtual or virtual:N expected");
                return new JsonArray(JsonValue.Create(spec));
            }

            case SettingKind.AudioDevice:
            {
                // A device that is unplugged now may still be chosen: the app warns while it is missing.
                var id = value.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => value.GetString()!.Trim(),
                    _ => throw Bad("нужно устройство", "a device expected"),
                };
                if (id.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(id, @"^\{[0-9.]+\}\.\{[0-9a-fA-F-]{36}\}$"))
                    throw Bad("ожидается идентификатор устройства Windows вида {0.0.0.00000000}.{guid}", "a Windows device id like {0.0.0.00000000}.{guid} expected");
                return JsonValue.Create(id);
            }

            default:
                throw Bad("неизвестный тип", "unknown type");
        }
    }

    /// <summary>Same setting value? Numbers compare by value (25 = 25.0), a numeric choice equals its text ("960" = 960).</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    private static string Canonical(JsonNode? n) => n switch
    {
        null => "null",
        JsonArray arr => "[" + string.Join(",", arr.Select(Canonical)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => // int or double inside: read the JSON text
            double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == JsonValueKind.String && double.TryParse(v.GetValue<string>(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) => d.ToString("R", CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => "s:" + v.GetValue<string>(),
        _ => n.ToJsonString(),
    };
}

/// <summary>Files and actions behind /api/settings and /api/restart; built by the app (tests use temp files).</summary>
/// <param name="UserFile">%APPDATA%\KarSlides\settings.json – the only file the UI writes.</param>
/// <param name="Current">Options the running app started with.</param>
/// <param name="LoadSaved">appsettings.json + settings.json as on disk now (what the next start uses).</param>
/// <param name="LoadBase">appsettings.json alone: values equal to it are not written to settings.json.</param>
/// <param name="Restart">Starts a new instance and stops this one gracefully.</param>
public sealed record SettingsContext(string UserFile, KarSlidesOptions Current, Func<KarSlidesOptions> LoadSaved,
    Func<KarSlidesOptions> LoadBase, Action Restart);

public sealed record SettingValue(string Path, string Group, string Label, SettingKind Kind, string? Help, double? Min,
    double? Max, IReadOnlyList<string>? Choices, bool Danger, JsonNode? Current, JsonNode? Saved);

public sealed record SettingsResponse(string File, bool RestartNeeded, IReadOnlyList<SettingValue> Fields)
{
    public KarSlides.Core.ProductInfo Product => KarSlides.Core.ProductInfo.Current;
}

public sealed record SettingsUpdate(Dictionary<string, JsonElement> Values);

public static class SettingsStore
{
    public static SettingsResponse Read(SettingsContext ctx, string? lang = null)
    {
        var saved = ctx.LoadSaved();
        var fields = SettingsCatalog.Fields.Select(f =>
        {
            var (group, label, help) = SettingsCatalog.Text(f, lang);
            return new SettingValue(f.Path, group, label, f.Kind, help, f.Min, f.Max, f.Choices, f.Danger,
                SettingsCatalog.Get(ctx.Current, f), SettingsCatalog.Get(saved, f));
        }).ToArray();
        var restart = fields.Any(f => !SettingsCatalog.Same(f.Current, f.Saved));
        return new SettingsResponse(ctx.UserFile, restart, fields);
    }

    /// <summary>
    /// Validates all values first, then writes settings.json: values equal to appsettings.json are removed from it,
    /// other keys of the file are kept as they are. The previous file is kept as settings.json.bak.
    /// </summary>
    public static void Write(SettingsContext ctx, SettingsUpdate update, string? lang = null)
    {
        var normalized = new List<(SettingField Field, JsonNode? Value)>();
        foreach (var (path, value) in update.Values)
        {
            var field = SettingsCatalog.Find(path) ?? throw new ArgumentException($"Unknown setting '{path}'");
            normalized.Add((field, SettingsCatalog.Normalize(field, value, lang)));
        }

        var root = File.Exists(ctx.UserFile)
            ? JsonNode.Parse(File.ReadAllText(ctx.UserFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidOperationException($"{ctx.UserFile} is not a JSON object")
            : new JsonObject();
        var defaults = ctx.LoadBase();
        foreach (var (field, value) in normalized)
        {
            var parts = field.Path.Split('.');
            if (SettingsCatalog.Same(value, SettingsCatalog.Get(defaults, field))) Remove(root, parts);
            else SetValue(root, parts, value);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ctx.UserFile)!);
        var temp = ctx.UserFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (File.Exists(ctx.UserFile)) File.Replace(temp, ctx.UserFile, ctx.UserFile + ".bak");
        else File.Move(temp, ctx.UserFile);
    }

    private static void SetValue(JsonObject root, string[] parts, JsonNode? value)
    {
        var node = root;
        foreach (var name in parts[..^1])
        {
            var key = FindKey(node, name) ?? name;
            if (node[key] is not JsonObject child) node[key] = child = new JsonObject();
            node = child;
        }
        var leaf = FindKey(node, parts[^1]) ?? parts[^1];
        node[leaf] = value?.DeepClone();
    }

    private static void Remove(JsonObject root, string[] parts)
    {
        var chain = new List<(JsonObject Parent, string Key)>();
        var node = root;
        foreach (var name in parts[..^1])
        {
            var key = FindKey(node, name);
            if (key is null || node[key] is not JsonObject child) return;
            chain.Add((node, key));
            node = child;
        }
        if (FindKey(node, parts[^1]) is { } leaf) node.Remove(leaf);
        // Drop sections left empty, so the file stays short.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var (parent, key) = chain[i];
            if (parent[key] is JsonObject { Count: 0 }) parent.Remove(key);
        }
    }

    /// <summary>Configuration keys are case-insensitive: reuse the spelling already in the file.</summary>
    private static string? FindKey(JsonObject node, string name) =>
        node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
