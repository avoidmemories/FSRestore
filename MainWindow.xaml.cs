using FSRestore.Models;
using FSRestore.Services;
using System.Runtime.InteropServices;
using IoPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;

namespace FSRestore;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly WindowGuardianService _guardian = new();
    private readonly AppSettings _settings;
    private TrayIconService? _tray;
    private bool _ready;
    private bool _exiting;
    private bool _dialogOpen;

    // Which list the picker is filling right now
    private List<string>? _pickerTarget;
    private string? _pickerSelected;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        StartupToggle.IsChecked = StartupService.IsEnabled();
        InitTray();

        // Seed the process-path cache so icons/friendly names survive restarts.
        foreach (var app in _guardian.ListOpenApps())
        {
            if (app.ProcessPath is not null && !_settings.KnownProcessPaths.ContainsKey(app.ProcessName))
            {
                _settings.KnownProcessPaths[app.ProcessName] = app.ProcessPath;
            }
        }
        _store.Save(_settings);

        // Start the guardian immediately — the tray-start path never fires
        // Loaded, so Configure must not depend on the window being shown.
        Apply();

        Loaded += (_, _) =>
        {
            ApplyDwmRounding();
            UpdateStatus();
            RenderLists();
            _ready = true;
        };

        Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
                GC.Collect(); // release managed heap while idle in tray
                _tray?.ShowBalloon("FSRestore", "Running in tray — right-click to open.");
            }
        };

        Closed += (_, _) =>
        {
            _store.Save(_settings);
            _guardian.Dispose();
            _tray?.Dispose();
        };
    }

    private void InitTray()
    {
        _tray = new TrayIconService("FSRestore", ShowWindow, ExitApp);
    }

    // Optional DWM call: rounded window corners on Windows 11.
    private void ApplyDwmRounding()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApp()
    {
        _exiting = true;
        _store.Save(_settings);
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void Apply()
    {
        _guardian.Configure(_settings.GameProcesses, _settings.TargetProcesses, _settings.Enabled);
    }

    private void UpdateStatus()
    {
        // Sync the ToggleButton so its template triggers play the slide/color
        // animations; the FSRestore label color transition runs below.
        GuardianToggle.IsChecked = _settings.Enabled;

        var targetColor = ((SolidColorBrush)FindResource(_settings.Enabled ? "Success" : "Danger")).Color;
        var current = GuardianToggle.Foreground as SolidColorBrush;
        if (current is null || current.IsFrozen)
        {
            GuardianToggle.Foreground = new SolidColorBrush(targetColor);
        }
        else
        {
            current.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private void ToggleGuardian()
    {
        if (!_ready)
        {
            return;
        }

        _settings.Enabled = !_settings.Enabled;
        _store.Save(_settings);
        Apply();
        UpdateStatus();
    }

    private void GuardianToggle_Click(object sender, RoutedEventArgs e) => ToggleGuardian();

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _settings.RunAtStartup = StartupToggle.IsChecked == true;
        StartupService.SetEnabled(_settings.RunAtStartup);
        _store.Save(_settings);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Hide(); // hides to tray, doesn't close

    // Drag the window only when the title bar background itself is hit,
    // not when a button inside it gets the click.
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            // Walk up the visual tree: if the click landed on a Button or
            // ToggleButton, don't drag — the control handles it.
            var src = e.OriginalSource as DependencyObject;
            while (src is not null)
            {
                if (src is System.Windows.Controls.Primitives.ButtonBase)
                {
                    return;
                }
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            DragMove();
        }
    }

    // ---------- dialog (in-window overlay) ----------

    private void AddGame_Click(object sender, RoutedEventArgs e) => OpenDialog("Add game", _settings.GameProcesses);
    private void AddTarget_Click(object sender, RoutedEventArgs e) => OpenDialog("Add app", _settings.TargetProcesses);

    private void OpenDialog(string title, List<string> target)
    {
        _pickerTarget = target;
        _pickerSelected = null;
        DialogTitle.Text = title;
        SearchBox.Clear();
        RenderPickerList();
        ShowDialog();
        SearchBox.Focus();
    }

    private new void ShowDialog() // hides Window.ShowDialog for the in-window overlay
    {
        _dialogOpen = true;
        ModalHost.Visibility = Visibility.Visible;
        Dialog.Opacity = 0;
        DialogShift.Y = 12;
        ModalHost.Opacity = 0;

        var overlayFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ModalHost.BeginAnimation(OpacityProperty, overlayFade);

        var cardFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Dialog.BeginAnimation(OpacityProperty, cardFade);

        var cardSlide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DialogShift.BeginAnimation(TranslateTransform.YProperty, cardSlide);
    }

    private void CloseDialog(object sender, RoutedEventArgs e) => HideDialog();

    private void ConfirmDialog(object sender, RoutedEventArgs e)
    {
        if (_pickerTarget is not null && _pickerSelected is not null)
        {
            if (!_pickerTarget.Contains(_pickerSelected, StringComparer.OrdinalIgnoreCase))
            {
                _pickerTarget.Add(_pickerSelected);
                // Cache the exe path so the icon still shows after the app closes.
                var path = FindProcessPath(_pickerSelected);
                if (path is not null)
                {
                    _settings.KnownProcessPaths[_pickerSelected] = path;
                }
                _store.Save(_settings);
                RenderLists();
                Apply();
            }
        }

        HideDialog();
    }

    private void HideDialog()
    {
        if (!_dialogOpen)
        {
            return;
        }

        _dialogOpen = false;

        var overlayFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        overlayFade.Completed += (_, _) => ModalHost.Visibility = Visibility.Collapsed;
        ModalHost.BeginAnimation(OpacityProperty, overlayFade);

        var cardFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Dialog.BeginAnimation(OpacityProperty, cardFade);

        var cardSlide = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        DialogShift.BeginAnimation(TranslateTransform.YProperty, cardSlide);
    }

    private void ModalHost_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == ModalHost)
        {
            HideDialog();
        }
    }

    private void Dialog_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dialogOpen)
        {
            HideDialog();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderPickerList();

    private void RenderPickerList()
    {
        PickerList.Items.Clear();
        var filter = SearchBox.Text?.Trim() ?? string.Empty;
        var apps = _guardian.ListOpenApps();

        foreach (var app in apps.Where(a => string.IsNullOrEmpty(filter) || MatchesSearch(a, filter)))
        {
            var item = new ListBoxItem
            {
                Content = MakePickerRow(app),
                Tag = app.ProcessName
            };
            item.MouseDoubleClick += (_, _) =>
            {
                _pickerSelected = app.ProcessName;
                ConfirmDialog(null!, null!);
            };
            item.PreviewMouseLeftButtonDown += (_, _) =>
            {
                PickerList.SelectedItem = item;
                _pickerSelected = app.ProcessName;
            };
            PickerList.Items.Add(item);
        }

        if (PickerList.Items.Count == 0)
        {
            PickerList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = string.IsNullOrEmpty(filter) ? "No windows detected" : "Nothing found",
                    Foreground = (Brush)FindResource("TextSecondary"),
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 16, 0, 16)
                },
                IsEnabled = false
            });
        }
    }

    // Well-known process names mapped to game titles, so searching by the
    // game's store name finds them too (e.g. "pubg" finds TslGame).
    private static readonly Dictionary<string, string> KnownGameTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TslGame"] = "PUBG: BATTLEGROUNDS",
        ["cs2"] = "Counter-Strike 2",
        ["csgo"] = "Counter-Strike",
        ["GTA5"] = "Grand Theft Auto V",
        ["r5apex"] = "Apex Legends",
        ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["VALORANT-Win64-Shipping"] = "VALORANT",
        ["Overwatch"] = "Overwatch",
        ["javaw"] = "Minecraft",
        ["RocketLeague"] = "Rocket League",
        ["RainbowSix"] = "Rainbow Six Siege",
        ["dota2"] = "Dota 2",
        ["RustClient"] = "Rust",
        ["EscapeFromTarkov"] = "Escape from Tarkov",
        ["Warface"] = "Warface",
        ["DeadByDaylight-Win64-Shipping"] = "Dead by Daylight",
        ["ApexLegends"] = "Apex Legends",
        ["AyuGram"] = "Ayugram Desktop",
        ["Telegram"] = "Telegram",
        ["Discord"] = "Discord",
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["steam"] = "Steam",
        ["Code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["explorer"] = "Проводник (Explorer)",
    };

    private static bool MatchesSearch(OpenAppInfo app, string filter)
    {
        if (app.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || app.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (KnownGameTitles.TryGetValue(app.ProcessName, out var title)
            && title.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (app.ProcessPath is not null
            && IoPath.GetFileNameWithoutExtension(app.ProcessPath).Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private UIElement MakePickerRow(OpenAppInfo app)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Source = IconHelper.GetIcon(app.ProcessPath),
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = app.FriendlyName,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);

        return grid;
    }

    // ---------- lists ----------

    private void RenderLists()
    {
        RenderList(GamesList, GamesEmpty, _settings.GameProcesses);
        RenderList(AppsList, AppsEmpty, _settings.TargetProcesses);
    }

    private void RenderList(ListBox list, StackPanel emptyLabel, List<string> selected)
    {
        list.Items.Clear();
        emptyLabel.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var name in selected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            // MakeRow returns a fully-formed ListBoxItem (with hover handlers).
            list.Items.Add(MakeRow(name, selected));
        }
    }

    private ListBoxItem MakeRow(string name, List<string> selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var icon = new Image
        {
            Source = IconHelper.GetIcon(FindProcessPath(name)),
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = GetFriendlyNameFor(name),
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Opacity = 0,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new ShapePath
            {
                Data = (StreamGeometry)FindResource("IconClose"),
                Style = (Style)FindResource("IconGlyph"),
                Width = 12,
                Height = 12
            }
        };
        var captured = name;
        remove.Click += (_, _) =>
        {
            selected.RemoveAll(x => x.Equals(captured, StringComparison.OrdinalIgnoreCase));
            _store.Save(_settings);
            RenderLists();
            Apply();
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        // Fade in on item hover, fade out on leave — hover handlers live on the
        // ListBoxItem itself so they fire reliably regardless of which child
        // element the pointer is over.
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var item = new ListBoxItem { Content = grid };
        item.MouseEnter += (_, _) =>
        {
            remove.BeginAnimation(OpacityProperty, fadeIn);
            remove.IsHitTestVisible = true;
        };
        item.MouseLeave += (_, _) =>
        {
            remove.BeginAnimation(OpacityProperty, fadeOut);
            remove.IsHitTestVisible = false;
        };

        return item;
    }

    private string? FindProcessPath(string processName)
    {
        // Live process first, then the cache from when it was running.
        try
        {
            var live = System.Diagnostics.Process.GetProcessesByName(processName)
                .FirstOrDefault()?.MainModule?.FileName;
            if (live is not null)
            {
                _settings.KnownProcessPaths[processName] = live;
                return live;
            }
        }
        catch
        {
        }

        return _settings.KnownProcessPaths.TryGetValue(processName, out var cached) ? cached : null;
    }

    // Shows the nice display name (e.g. "Counter-Strike 2") instead of "cs2".
    private string GetFriendlyNameFor(string processName)
    {
        if (KnownGameTitles.TryGetValue(processName, out var known))
        {
            return known;
        }

        var path = FindProcessPath(processName);
        if (path is null)
        {
            return processName;
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var desc = info.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc;
            }

            var product = info.ProductName;
            if (!string.IsNullOrWhiteSpace(product))
            {
                return product;
            }
        }
        catch
        {
        }

        return processName;
    }
}
