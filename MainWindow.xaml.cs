using System.Windows;
using System.Windows.Data;

namespace OptimizeProgram;

public partial class MainWindow : Window
{
    readonly List<Tweak> tweaks = Catalog.All();
    bool backupDone;

    const string WhyBackup =
        "Твики меняют реестр, службы и план питания. Если после изменений что-то поведёт себя не так, " +
        "точка восстановления Windows позволит вернуть систему целиком.\n\n" +
        "Кроме неё программа всегда сама сохраняет исходное значение каждого изменяемого параметра, " +
        "поэтому любой твик можно откатить кнопкой «Откатить выбранные».\n\n";

    public MainWindow()
    {
        InitializeComponent();
        var view = CollectionViewSource.GetDefaultView(tweaks);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
        List.ItemsSource = view;
        Status.Text = "Готово. Выбери твики и нажми «Применить».";
    }

    async Task<bool> EnsureBackup()
    {
        if (backupDone) return true;
        var r = MessageBox.Show(WhyBackup + "Создать точку восстановления сейчас? (рекомендуется)",
            "Резервная копия", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes)
        {
            Status.Text = "Создаю точку восстановления...";
            var (ok, msg) = await Task.Run(BackupService.CreateRestorePoint);
            MessageBox.Show(msg, ok ? "Готово" : "Не удалось", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (!ok && MessageBox.Show("Продолжить без точки восстановления? Откат отдельных твиков всё равно будет работать.",
                    "Продолжить?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
        }
        else if (MessageBox.Show("Продолжить без точки восстановления? Откат отдельных твиков всё равно будет работать.",
                     "Продолжить?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        backupDone = true;
        return true;
    }

    async void Backup_Click(object s, RoutedEventArgs e)
    {
        backupDone = false;
        await EnsureBackup();
    }

    async void Apply_Click(object s, RoutedEventArgs e) => await Run(true);
    async void Revert_Click(object s, RoutedEventArgs e) => await Run(false);

    async Task Run(bool apply)
    {
        var sel = tweaks.Where(t => t.Selected).ToList();
        if (sel.Count == 0) { Status.Text = "Ничего не выбрано."; return; }
        if (apply && !await EnsureBackup()) return;

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var t in sel)
            {
                try { if (apply) t.Apply(); else t.Revert(); }
                catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); }
            }
        });
        foreach (var t in tweaks) t.Refresh();
        Status.Text = errors.Count == 0
            ? $"{(apply ? "Применено" : "Откачено")}: {sel.Count}. Для части твиков нужна перезагрузка."
            : "Ошибки:\n" + string.Join("\n", errors);
    }

    async void Clean_Click(object s, RoutedEventArgs e)
    {
        Status.Text = "Очистка...";
        long bytes = await Task.Run(Cleaner.CleanTemp);
        Status.Text = $"Освобождено: {bytes / 1024 / 1024} МБ.";
    }
}
