using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OptimizeProgram;

public static class Sys
{
    public static (string output, int code) Run(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        })!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (o, p.ExitCode);
    }
}

// Хранилище исходных значений: %AppData%\OptimizeProgram\backup.json
public static class BackupService
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OptimizeProgram");
    static readonly string FilePath = Path.Combine(Dir, "backup.json");
    static readonly Dictionary<string, string?> data = Load();
    static readonly object gate = new();

    static Dictionary<string, string?> Load()
    {
        try { return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(FilePath)) ?? new()
                : new(); }
        catch { return new(); }
    }

    static void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool Has(string id) { lock (gate) return data.ContainsKey(id); }
    public static string? Get(string id) { lock (gate) return data.TryGetValue(id, out var v) ? v : null; }
    // Сохраняем только первое (настоящее исходное) значение
    public static void SetOnce(string id, string? v) { lock (gate) { if (!data.ContainsKey(id)) { data[id] = v; Save(); } } }
    public static void Remove(string id) { lock (gate) { if (data.Remove(id)) Save(); } }

    public static (bool ok, string msg) CreateRestorePoint()
    {
        try
        {
            Sys.Run("powershell", "-NoProfile -Command \"Enable-ComputerRestore -Drive 'C:\\'\"");
            var (o, code) = Sys.Run("powershell",
                "-NoProfile -Command \"Checkpoint-Computer -Description 'OptimizeProgram' -RestorePointType MODIFY_SETTINGS\"");
            return code == 0
                ? (true, "Точка восстановления создана.")
                : (false, "Windows не создала точку (частая причина: она уже создавалась за последние 24 часа).\n\n" + o.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

public abstract class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Selected { get; set; } = true;
    public string StatusText => IsApplied() ? "✓ применено" : "";

    public abstract void Apply();
    public abstract void Revert();
    public abstract bool IsApplied();

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
}

public class RegTweak : Tweak
{
    readonly string hive, sub, name;
    readonly object value;
    readonly RegistryValueKind kind;

    public RegTweak(string category, string title, string desc, string hive, string sub, string name,
                    object value, RegistryValueKind kind = RegistryValueKind.DWord)
    {
        (this.hive, this.sub, this.name, this.value, this.kind) = (hive, sub, name, value, kind);
        Category = category; Name = title; Description = desc;
        Id = $"{hive}\\{sub}\\{name}";
    }

    RegistryKey Base() => RegistryKey.OpenBaseKey(
        hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public override void Apply()
    {
        using var b = Base();
        using var k = b.CreateSubKey(sub, true);
        BackupService.SetOnce(Id, k.GetValue(name)?.ToString());
        k.SetValue(name, value, kind);
    }

    public override void Revert()
    {
        if (!BackupService.Has(Id)) return;
        using var b = Base();
        using var k = b.CreateSubKey(sub, true);
        var old = BackupService.Get(Id);
        if (old == null) k.DeleteValue(name, false);
        else k.SetValue(name, kind == RegistryValueKind.DWord ? int.Parse(old) : old, kind);
        BackupService.Remove(Id);
    }

    public override bool IsApplied()
    {
        using var b = Base();
        using var k = b.OpenSubKey(sub);
        return k?.GetValue(name)?.ToString() == value.ToString();
    }
}

// Несколько связанных значений реестра как один твик
public class GroupTweak : Tweak
{
    readonly RegTweak[] parts;
    public GroupTweak(string category, string title, string desc, params RegTweak[] parts)
    { Category = category; Name = title; Description = desc; Id = title; this.parts = parts; }
    public override void Apply() { foreach (var p in parts) p.Apply(); }
    public override void Revert() { foreach (var p in parts) p.Revert(); }
    public override bool IsApplied() => parts.All(p => p.IsApplied());
}

public class PowerTweak : Tweak
{
    const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    static readonly Regex Guid = new(@"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");

    public PowerTweak()
    {
        Id = "power.ultimate"; Category = "План питания";
        Name = "Максимальная производительность";
        Description = "Включает схему Ultimate Performance (CPU не снижает частоту в простое). Выше нагрев и потребление, на ноутбуках уменьшит время работы от батареи.";
    }

    static string? Active() => Guid.Match(Sys.Run("powercfg", "/getactivescheme").output).Value is { Length: > 0 } g ? g : null;

    public override void Apply()
    {
        BackupService.SetOnce(Id, Active());
        var created = Guid.Match(Sys.Run("powercfg", $"-duplicatescheme {Ultimate}").output).Value;
        if (string.IsNullOrEmpty(created)) throw new Exception("Схема Ultimate Performance недоступна на этой системе.");
        Sys.Run("powercfg", $"/setactive {created}");
    }

    public override void Revert()
    {
        var old = BackupService.Get(Id);
        if (!string.IsNullOrEmpty(old)) Sys.Run("powercfg", $"/setactive {old}");
        BackupService.Remove(Id);
    }

    public override bool IsApplied() => Sys.Run("powercfg", "/getactivescheme").output.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)
                                        || Sys.Run("powercfg", "/getactivescheme").output.Contains("Максимальная", StringComparison.OrdinalIgnoreCase);
}

public static class Cleaner
{
    public static long CleanTemp()
    {
        long freed = 0;
        foreach (var dir in new[] { Path.GetTempPath(), @"C:\Windows\Temp" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { var len = new FileInfo(f).Length; File.Delete(f); freed += len; } catch { }
        }
        return freed;
    }
}

public static class Catalog
{
    const string Sp = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    public static List<Tweak> All() => new()
    {
        new PowerTweak(),

        new RegTweak("Игры", "Game Mode включён", "Windows отдаёт приоритет игре и ограничивает фоновые задачи.",
            "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
        new RegTweak("Игры", "Отключить Game DVR (фоновая запись)", "Убирает фоновую запись Xbox Game Bar, которая нагружает GPU и диск.",
            "HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0),
        new RegTweak("Игры", "Запретить Game DVR политикой", "Закрепляет отключение записи на уровне системы.",
            "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0),
        new RegTweak("Игры", "Отзывчивость мультимедиа (MMCSS)", "SystemResponsiveness = 0: больше CPU-времени приоритетным задачам (играм).",
            "HKLM", Sp, "SystemResponsiveness", 0),
        new RegTweak("Игры", "Приоритет игровых задач", "Повышает приоритет планировщика для задач категории Games.",
            "HKLM", Sp + @"\Tasks\Games", "Priority", 6),
        new RegTweak("Игры", "Отключить сетевое ограничение мультимедиа", "NetworkThrottlingIndex = off: убирает искусственное ограничение сетевых пакетов.",
            "HKLM", Sp, "NetworkThrottlingIndex", -1),
        new GroupTweak("Игры", "Отключить ускорение мыши", "Мышь двигается линейно: важно для стабильного прицела в шутерах.",
            new RegTweak("", "", "", "HKCU", @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String),
            new RegTweak("", "", "", "HKCU", @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String),
            new RegTweak("", "", "", "HKCU", @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String)),

        new RegTweak("Телеметрия и приватность", "Минимальная телеметрия", "Ограничивает отправку диагностических данных в Microsoft.",
            "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
        new RegTweak("Телеметрия и приватность", "Отключить службу DiagTrack", "Останавливает фоновую службу сбора телеметрии (вступит в силу после перезагрузки).",
            "HKLM", @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4),
        new RegTweak("Телеметрия и приватность", "Отключить рекламный ID", "Приложения больше не получают персональный рекламный идентификатор.",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
        new RegTweak("Телеметрия и приватность", "Отключить историю действий", "Windows не публикует историю ваших действий.",
            "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),

        new RegTweak("Фоновые процессы", "Запретить фоновые приложения", "Приложения Microsoft Store не работают в фоне, экономит CPU и RAM.",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
        new RegTweak("Фоновые процессы", "Убрать задержку автозагрузки", "Программы автозагрузки стартуют сразу, а не через несколько секунд.",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),

        new RegTweak("Интерфейс", "Отключить прозрачность", "Меньше нагрузка на GPU при работе в системе.",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
    };
}
