using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Paddock.Core;
using Paddock.Interop;
using Paddock.Sdk;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Views;

/// <summary>
/// Ein Bereich auf dem Desktop. Liegt immer unter normalen Fenstern, nimmt nie den Fokus
/// (außer beim Umbenennen) und zeigt den Inhalt eines Ordners.
/// </summary>
public partial class FenceWindow : Window, IFenceInfo, INotifyPropertyChanged
{
    public FenceConfig Config { get; }
    readonly ObservableCollection<ItemViewModel> _items = [];
    readonly DispatcherTimer _refresh, _peekIn, _peekOut;
    FileSystemWatcher? _watcher;
    IntPtr _hwnd;
    string _currentFolder;
    double _scale = 1;
    bool _allowActivate;

    // Verschieben über die Titelleiste
    bool _titlePressed, _moving;
    POINT _dragCursor;
    RECT _dragRect;

    // Einklappen / Animation
    bool _peeking, _animating;
    int _animFrom, _animTo;
    DateTime _animStart;

    // Auswahl & Ziehen
    ItemViewModel? _anchor, _pressedItem;
    Point _pressPoint;
    bool _pressed;
    static (FenceWindow Source, string[] Paths)? _internalDrag;
    int _insertIndex = -1;

    public FenceWindow(FenceConfig config)
    {
        Config = config;
        _currentFolder = config.FolderPath;
        InitializeComponent();
        ItemsHost.ItemsSource = _items;

        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Reload(); };
        _peekIn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _peekIn.Tick += (_, _) => { _peekIn.Stop(); Peek(true); };
        _peekOut = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _peekOut.Tick += (_, _) => { _peekOut.Stop(); Peek(false); };

        TitleBar.MouseLeftButtonDown += Title_Down;
        TitleBar.MouseMove += Title_Move;
        TitleBar.MouseLeftButtonUp += Title_Up;
        TitleBar.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowFenceMenu(); };

        Body.PreviewMouseLeftButtonDown += Body_Down;
        Body.PreviewMouseMove += Body_Move;
        Body.PreviewMouseLeftButtonUp += Body_Up;
        Body.MouseRightButtonUp += Body_RightUp;

        DragEnter += OnDragOver; DragOver += OnDragOver; DragLeave += OnDragLeave; Drop += OnDrop;
        MouseEnter += (_, _) => { _peekOut.Stop(); if (Config.Collapsed && !_peeking && App.Host.Settings.PeekCollapsed) _peekIn.Start(); };
        MouseLeave += (_, _) => { _peekIn.Stop(); if (_peeking && !IsMenuOpen) _peekOut.Start(); };
        SizeChanged += (_, _) => Raise(nameof(ListItemWidth));
        DpiChanged += (_, e) => _scale = e.NewDpi.DpiScaleX;

        ThemeManager.Instance.Changed += OnThemeChanged;
        UpdateTitle();
        UpdateViewMode();
    }

    // ---------------------------------------------------------------- IFenceInfo
    public Guid Id => Config.Id;
    string IFenceInfo.Title => Config.Title;
    public string FolderPath => Config.FolderPath;
    public bool IsPortal => Config.Kind == FenceKind.Portal;

    // ---------------------------------------------------------------- Bindungs-Eigenschaften
    public double IconSize => Config.IconSize;
    public double TileWidth => Math.Max(74, Config.IconSize + 34);
    public double ListItemWidth => Math.Max(80, Scroller.ActualWidth - 14);
    public double LabelLineHeight => Math.Round(ItemFontSize * 1.36);
    public double LabelMaxHeight => LabelLineHeight * 2 + 1;
    double ItemFontSize => Application.Current.Resources["P.Item.FontSize"] is double d ? d : 12;
    double TitleHeightDip => Application.Current.Resources["P.Title.Height"] is double d ? d : 34;
    bool IsMenuOpen => ContextMenu?.IsOpen == true;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ---------------------------------------------------------------- Native Einrichtung
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);
        _scale = DpiFor(_hwnd) / 96.0;

        SetExStyle(_hwnd, (GetExStyle(_hwnd) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW);
        AttachToDesktop();
        ApplyBackdrop();

        var work = GetWorkAreas();
        var rect = Snapper.EnsureVisible(new RECT(Config.X, Config.Y, Config.X + Config.Width, Config.Y + Config.Height), work);
        int h = Config.Collapsed ? TitlePx : rect.Height;
        SetWindowPos(_hwnd, App.Showcase ? HWND_TOPMOST : HWND_BOTTOM, rect.Left, rect.Top, rect.Width, h, SWP_NOACTIVATE);
        UpdateChrome();
        Reload();
        StartWatching();
    }

    /// <summary>Besitzer = Desktop: so bleibt der Bereich bei „Desktop anzeigen“ (Win+D) sichtbar.</summary>
    public void AttachToDesktop()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (App.Showcase) { SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); return; }
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, DesktopShell.Progman);
        SendToBottom();
    }

    public void SendToBottom()
    {
        if (App.Showcase) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }


    void ApplyBackdrop()
    {
        var th = ThemeManager.Instance.Current;
        var tint = ThemeManager.ParseColor(Config.Tint ?? th.Tint, Colors.Black);
        Backdrop.Apply(_hwnd, th.Backdrop, tint, th.Dark, th.Corners, ThemeManager.ParseColor(th.Border, Colors.Transparent));
        if (Config.Tint is not null)
        {
            var brush = new SolidColorBrush(ThemeManager.WithOpacity(tint, Math.Max(th.TintOpacity, 0.35)));
            brush.Freeze();
            TintLayer.Background = brush;
        }
        else TintLayer.SetResourceReference(Border.BackgroundProperty, "P.Tint");
    }

    void OnThemeChanged()
    {
        if (_hwnd == IntPtr.Zero) return;
        ApplyBackdrop();
        UpdateTitle();
        Raise(nameof(LabelLineHeight)); Raise(nameof(LabelMaxHeight));
        if (Config.Collapsed && !_peeking) SetHeightPx(TitlePx);
    }

    int TitlePx => (int)Math.Round(TitleHeightDip * _scale);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE when !_allowActivate:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);

            case WM_WINDOWPOSCHANGING when !App.Showcase:
                // Niemals über normale Fenster rutschen
                var pos = Marshal.PtrToStructure<WINDOWPOS>(l);
                if ((pos.flags & SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, l, false);
                }
                break;

            case WM_SIZING when App.Host.Settings.Snap && !Keyboard.IsKeyDown(Key.LeftAlt):
                var r = Marshal.PtrToStructure<RECT>(l);
                r = Snapper.SnapSize(r, w.ToInt32(), App.Host.Fences.OtherRects(this), GetWorkAreas(), SnapThreshold, SnapGap);
                Marshal.StructureToPtr(r, l, false);
                handled = true;
                return new IntPtr(1);

            case WM_EXITSIZEMOVE:
                SaveBounds();
                break;
        }
        return IntPtr.Zero;
    }

    int SnapThreshold => (int)(14 * _scale);
    int SnapGap => (int)(10 * _scale);

    public RECT ScreenRect { get { GetWindowRect(_hwnd, out var r); return r; } }

    void SaveBounds()
    {
        var r = ScreenRect;
        Config.X = r.Left; Config.Y = r.Top; Config.Width = r.Width;
        if (!Config.Collapsed && !_peeking && !_animating) Config.Height = r.Height;
        App.Host.Save();
    }

    void UpdateChrome()
    {
        double b = Config.Locked ? 0 : 6;
        Chrome.ResizeBorderThickness = Config.Collapsed && !_peeking ? new Thickness(b, 0, b, 0) : new Thickness(b);
        LockIcon.Visibility = Config.Locked ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- Titel
    void UpdateTitle()
    {
        var title = Config.Title;
        if (!PathEquals(_currentFolder, Config.FolderPath))
            title += "  ›  " + Path.GetFileName(_currentFolder.TrimEnd('\\'));
        TitleText.Text = ThemeManager.Instance.Current.Title.Uppercase ? title.ToUpperInvariant() : title;
        BackButton.Visibility = PathEquals(_currentFolder, Config.FolderPath) ? Visibility.Collapsed : Visibility.Visible;
        Chevron.Text = "";
        ChevronRotate.Angle = Config.Collapsed ? 180 : 0;
        CollapseButton.ToolTip = Config.Collapsed ? "Ausklappen" : "Einklappen";
    }

    void Title_Down(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<Button>(e.OriginalSource) || TitleEdit.IsVisible) return;
        GetCursorPos(out _dragCursor);
        _dragRect = ScreenRect;
        _titlePressed = true;
        _moving = false;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    void Title_Move(object sender, MouseEventArgs e)
    {
        if (!_titlePressed) return;
        GetCursorPos(out var p);
        int dx = p.X - _dragCursor.X, dy = p.Y - _dragCursor.Y;
        if (!_moving)
        {
            if (Math.Abs(dx) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(dy) < GetSystemMetrics(SM_CYDRAG)) return;
            if (Config.Locked) return;
            _moving = true;
            _peekIn.Stop();
        }
        var r = new RECT(_dragRect.Left + dx, _dragRect.Top + dy, _dragRect.Right + dx, _dragRect.Bottom + dy);
        if (App.Host.Settings.Snap && !Keyboard.IsKeyDown(Key.LeftAlt) && !Keyboard.IsKeyDown(Key.RightAlt))
        {
            // Beim Einrasten zählt die ausgeklappte Höhe, damit eingeklappte Bereiche sauber stapeln
            r = Snapper.SnapMove(r, App.Host.Fences.OtherRects(this), GetWorkAreas(), SnapThreshold, SnapGap);
        }
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void Title_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_titlePressed) return;
        _titlePressed = false;
        TitleBar.ReleaseMouseCapture();
        if (_moving) { _moving = false; SaveBounds(); }
        else ToggleCollapsed();
        e.Handled = true;
    }

    void Collapse_Click(object sender, RoutedEventArgs e) => ToggleCollapsed();
    void MenuButton_Click(object sender, RoutedEventArgs e) => ShowFenceMenu();
    void Back_Click(object sender, RoutedEventArgs e) => Navigate(Path.GetDirectoryName(_currentFolder.TrimEnd('\\')) ?? Config.FolderPath);

    // ---------------------------------------------------------------- Einklappen
    public void ToggleCollapsed()
    {
        _peekIn.Stop(); _peekOut.Stop();
        if (_peeking) { _peeking = false; Config.Collapsed = false; }
        else
        {
            if (!Config.Collapsed) SaveBounds();
            Config.Collapsed = !Config.Collapsed;
            AnimateHeight(Config.Collapsed ? TitlePx : Config.Height);
        }
        UpdateTitle();
        UpdateChrome();
        App.Host.Save();
    }

    void Peek(bool open)
    {
        if (!Config.Collapsed) return;
        if (open && !_peeking && !_titlePressed) { _peeking = true; AnimateHeight(Config.Height); }
        else if (!open && _peeking) { _peeking = false; AnimateHeight(TitlePx); }
        UpdateChrome();
    }

    void AnimateHeight(int target)
    {
        _animFrom = ScreenRect.Height;
        _animTo = Math.Max(target, TitlePx);
        _animStart = DateTime.UtcNow;
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += AnimFrame;
    }

    void AnimFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / 170.0);
        double eased = 1 - Math.Pow(1 - t, 3);
        SetHeightPx((int)Math.Round(_animFrom + (_animTo - _animFrom) * eased));
        if (t >= 1)
        {
            CompositionTarget.Rendering -= AnimFrame;
            _animating = false;
        }
    }

    void SetHeightPx(int h)
    {
        var r = ScreenRect;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, r.Width, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ---------------------------------------------------------------- Inhalt
    void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(_currentFolder)) return;
        try
        {
            _watcher = new FileSystemWatcher(_currentFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                InternalBufferSize = 8192,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
            _watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { StartWatching(); ScheduleRefresh(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error($"Überwachung {_currentFolder}", ex); }
    }

    public void ScheduleRefresh() { _refresh.Stop(); _refresh.Start(); }

    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", ".DS_Store" };

    public void Reload()
    {
        bool exists = Directory.Exists(_currentFolder);
        var settings = App.Host.Settings;
        List<FileSystemInfo> entries = [];
        if (exists)
        {
            try
            {
                foreach (var fsi in new DirectoryInfo(_currentFolder).EnumerateFileSystemInfos())
                {
                    if (Ignored.Contains(fsi.Name)) continue;
                    var a = fsi.Attributes;
                    if (!settings.ShowHiddenFiles && (a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System))) continue;
                    entries.Add(fsi);
                }
            }
            catch (Exception ex) { Log.Error($"Lesen {_currentFolder}", ex); }
        }

        bool atRoot = PathEquals(_currentFolder, Config.FolderPath);
        IEnumerable<FileSystemInfo> sorted = Config.Sort switch
        {
            FenceSort.Name => entries.OrderBy(f => f is FileInfo).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            FenceSort.Type => entries.OrderBy(f => f is FileInfo).ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            FenceSort.Modified => entries.OrderByDescending(f => f.LastWriteTime),
            _ when atRoot => ManualOrder(entries),
            _ => entries.OrderBy(f => f is FileInfo).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var old = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var next = new List<ItemViewModel>();
        foreach (var fsi in sorted)
        {
            if (old.TryGetValue(fsi.FullName, out var vm) && vm.Modified == fsi.LastWriteTime) next.Add(vm);
            else next.Add(new ItemViewModel(fsi) { IsSelected = vm?.IsSelected ?? false });
        }

        // Nur ändern, was sich geändert hat – vermeidet Flackern und unnötiges Layout
        if (!next.SequenceEqual(_items))
        {
            var remaining = new HashSet<string>(next.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
            var left = _items.Where(i => !remaining.Contains(i.Path)).Select(i => i.FileName).ToList();
            if (left.Count > 0) App.Host.Fences.NoteLeft(left);
            _items.Clear();
            foreach (var vm in next) _items.Add(vm);
        }

        if (atRoot && Config.Sort == FenceSort.Manual)
            Config.Order = _items.Select(i => i.FileName).ToList();

        CountText.Text = _items.Count == 1 ? "1 Element" : $"{_items.Count} Elemente";
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = exists ? (IsPortal ? "Ordner ist leer" : "Dateien hierher ziehen") : "Ordner nicht gefunden";
        LoadIcons();
    }

    IEnumerable<FileSystemInfo> ManualOrder(List<FileSystemInfo> entries)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Config.Order.Count; i++) index.TryAdd(Config.Order[i], i);
        return entries.OrderBy(f => index.TryGetValue(f.Name, out var i) ? i : int.MaxValue).ThenBy(f => f.CreationTime);
    }

    void LoadIcons()
    {
        int px = (int)Math.Round((Config.ViewMode == FenceViewMode.List ? 20 : Config.IconSize) * _scale);
        bool thumbs = App.Host.Settings.ShowThumbnails && Config.ViewMode == FenceViewMode.Tiles;
        foreach (var vm in _items)
        {
            if (vm.Icon is not null && vm.IconPx == px) continue;
            vm.IconPx = px;
            var item = vm;
            var cached = ShellIcons.Instance.Get(vm.Path, px, thumbs, img => { if (item.IconPx == px) item.Icon = img; });
            if (cached is not null) vm.Icon = cached;
        }
    }

    public void Navigate(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // Nie oberhalb des Portal-Ordners
        if (!folder.StartsWith(Config.FolderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) folder = Config.FolderPath;
        _currentFolder = folder;
        _items.Clear();
        UpdateTitle();
        StartWatching();
        Reload();
        Scroller.ScrollToTop();
    }

    public void ChangeFolder(string folder)
    {
        Config.FolderPath = folder;
        Navigate(folder);
        App.Host.Save();
    }

    void UpdateViewMode()
    {
        ItemsHost.ItemTemplate = (DataTemplate)Resources[Config.ViewMode == FenceViewMode.List ? "ListTemplate" : "TileTemplate"];
        Raise(nameof(IconSize)); Raise(nameof(TileWidth));
        foreach (var vm in _items) vm.IconPx = 0;
        if (_hwnd != IntPtr.Zero) LoadIcons();
    }

    // ---------------------------------------------------------------- Auswahl & Öffnen
    static ItemViewModel? ItemFrom(object source) =>
        (source as FrameworkElement)?.DataContext as ItemViewModel
        ?? (source as FrameworkContentElement)?.DataContext as ItemViewModel;

    IReadOnlyList<ItemViewModel> Selected => _items.Where(i => i.IsSelected).ToList();

    void Body_Down(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource)) return;
        TakeKeyboard();
        var item = ItemFrom(e.OriginalSource);
        if (item is null)
        {
            foreach (var i in _items) i.IsSelected = false;
            return;
        }

        if (e.ClickCount == 2) { e.Handled = true; OpenItem(item); _pressed = false; return; }

        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else if (mods.HasFlag(ModifierKeys.Shift) && _anchor is not null)
        {
            int a = _items.IndexOf(_anchor), b = _items.IndexOf(item);
            for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = i >= Math.Min(a, b) && i <= Math.Max(a, b);
        }
        else if (!item.IsSelected)
        {
            foreach (var i in _items) i.IsSelected = i == item;
        }
        if (!mods.HasFlag(ModifierKeys.Shift)) _anchor = item;

        _pressed = true;
        _pressedItem = item;
        _pressPoint = e.GetPosition(this);
        e.Handled = true;
    }

    void Body_Move(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed || _pressedItem is null) return;
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = false;
        var paths = Selected.Select(i => i.Path).ToArray();
        if (paths.Length == 0) return;
        _internalDrag = (this, paths);
        try { ShellItems.DoDragDrop(_hwnd, paths); }
        finally { _internalDrag = null; HideDropFeedback(); ScheduleRefresh(); }
    }

    void Body_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        var item = ItemFrom(e.OriginalSource);
        if (item is null) return;
        if (App.Host.Settings.SingleClickOpen && Keyboard.Modifiers == ModifierKeys.None) { OpenItem(item); return; }
        if (Keyboard.Modifiers == ModifierKeys.None)
            foreach (var i in _items) i.IsSelected = i == item;
    }

    void OpenItem(ItemViewModel item)
    {
        bool navigate = item.IsDirectory && Config.NavigateFolders && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (navigate) Navigate(item.Path);
        else ShellItems.Open(item.Path);
    }

    void Body_RightUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFrom(e.OriginalSource);
        e.Handled = true;
        if (item is null) { ShowFenceMenu(); return; }
        TakeKeyboard();
        if (!item.IsSelected) foreach (var i in _items) i.IsSelected = i == item;
        var selection = Selected;
        var paths = selection.Select(i => i.Path).ToList();
        var infos = selection.Select(i => i.Info).ToList();

        var extra = new List<ShellItems.MenuEntry>();
        if (item.IsDirectory && selection.Count == 1)
            extra.Add(new("Im Bereich öffnen", () => Navigate(item.Path)));
        extra.Add(new("Im Explorer anzeigen", () => ShellItems.ShowInExplorer(item.Path)));
        if (!IsPortal)
            extra.Add(new("Zurück auf den Desktop legen", () => ShellItems.Move(_hwnd, paths, DesktopShell.UserDesktop)));
        foreach (var cmd in App.Host.Registry.ItemCommands.Where(c => SafeCan(() => c.CanExecute(infos))))
            extra.Add(new(cmd.DisplayName, () => App.Host.RunSafe(cmd.DisplayName, () => cmd.Execute(this, infos))));

        GetCursorPos(out var pt);
        ShellItems.ShowContextMenu(App.Host.Helper, paths, pt, extra, RenameItem, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        ScheduleRefresh();
    }

    static bool SafeCan(Func<bool> f) { try { return f(); } catch { return false; } }

    void RenameItem(string path)
    {
        var name = Path.GetFileName(path);
        bool hideExt = Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url";
        var current = hideExt ? Path.GetFileNameWithoutExtension(path) : name;
        var input = Dialog.Prompt("Umbenennen", "Neuer Name:", current, selectBaseName: !hideExt && File.Exists(path));
        if (string.IsNullOrWhiteSpace(input) || input == current) return;
        var newName = hideExt ? input + Path.GetExtension(path) : input;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Dialog.Info("Ungültiger Name", "Der Name enthält Zeichen, die Windows nicht erlaubt."); return; }
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (ShellItems.Rename(_hwnd, path, target))
        {
            int i = Config.Order.FindIndex(o => o.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Config.Order[i] = newName;
            ScheduleRefresh();
        }
    }

    // ---------------------------------------------------------------- Ablegen
    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    DragDropEffects EffectFor(DragEventArgs e, string[] paths)
    {
        var keys = e.KeyStates;
        bool ctrl = keys.HasFlag(DragDropKeyStates.ControlKey), shift = keys.HasFlag(DragDropKeyStates.ShiftKey), alt = keys.HasFlag(DragDropKeyStates.AltKey);
        DragDropEffects wanted =
            alt || (ctrl && shift) ? DragDropEffects.Link :
            ctrl ? DragDropEffects.Copy :
            shift ? DragDropEffects.Move :
            paths.All(p => SameVolume(p, _currentFolder)) ? DragDropEffects.Move : DragDropEffects.Copy;
        return (e.AllowedEffects & wanted) != 0 ? wanted : e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || !Directory.Exists(_currentFolder))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        // Ordner nicht in sich selbst ablegen
        if (paths.Any(p => _currentFolder.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || PathEquals(p, _currentFolder)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        if (Config.Collapsed && !_peeking) { _peekOut.Stop(); Peek(true); }
        _peekOut.Stop();

        bool reorder = IsReorder(paths);
        e.Effects = reorder ? DragDropEffects.Move : EffectFor(e, paths);
        DropGlow.Opacity = reorder ? 0 : 1;
        UpdateInsertMarker(e.GetPosition(ItemsHost), reorder || Config.Sort == FenceSort.Manual);
    }

    bool IsReorder(string[] paths) =>
        _internalDrag is { } d && d.Source == this && paths.All(p => PathEquals(Path.GetDirectoryName(p) ?? "", _currentFolder));

    void OnDragLeave(object sender, DragEventArgs e)
    {
        HideDropFeedback();
        if (_peeking) _peekOut.Start();
    }

    void HideDropFeedback()
    {
        DropGlow.Opacity = 0;
        InsertMarker.Visibility = Visibility.Collapsed;
    }

    void UpdateInsertMarker(Point p, bool show)
    {
        _insertIndex = -1;
        if (!show || _items.Count == 0 || !PathEquals(_currentFolder, Config.FolderPath)) { InsertMarker.Visibility = Visibility.Collapsed; return; }

        int best = -1; double bestDist = double.MaxValue; Rect bestRect = default;
        for (int i = 0; i < _items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            var r = new Rect(c.TranslatePoint(new Point(), ItemsHost), c.RenderSize);
            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            double dist = Math.Pow(center.X - p.X, 2) + Math.Pow((center.Y - p.Y) * 1.6, 2);
            if (dist < bestDist) { bestDist = dist; best = i; bestRect = r; }
        }
        if (best < 0) { InsertMarker.Visibility = Visibility.Collapsed; return; }

        bool list = Config.ViewMode == FenceViewMode.List;
        bool after = list ? p.Y > bestRect.Y + bestRect.Height / 2 : p.X > bestRect.X + bestRect.Width / 2;
        _insertIndex = best + (after ? 1 : 0);

        var origin = ItemsHost.TranslatePoint(new Point(), Overlay);
        if (list)
        {
            InsertMarker.Width = bestRect.Width; InsertMarker.Height = 3;
            Canvas.SetLeft(InsertMarker, origin.X + bestRect.X);
            Canvas.SetTop(InsertMarker, origin.Y + (after ? bestRect.Bottom : bestRect.Top) - 1.5);
        }
        else
        {
            InsertMarker.Width = 3; InsertMarker.Height = bestRect.Height - 8;
            Canvas.SetLeft(InsertMarker, origin.X + (after ? bestRect.Right : bestRect.Left) - 1.5);
            Canvas.SetTop(InsertMarker, origin.Y + bestRect.Y + 4);
        }
        InsertMarker.Visibility = Visibility.Visible;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var index = _insertIndex;
        HideDropFeedback();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) { e.Effects = DragDropEffects.None; return; }
        bool reorder = IsReorder(paths);
        var effect = reorder ? DragDropEffects.Move : EffectFor(e, paths);
        // Wir führen die Operation selbst aus → der Quelle "nichts passiert" melden, damit sie nichts löscht.
        e.Effects = DragDropEffects.None;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => PerformDrop(paths, effect, index, reorder));
    }

    void PerformDrop(string[] paths, DragDropEffects effect, int index, bool reorder)
    {
        var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))).ToList();
        bool atRoot = PathEquals(_currentFolder, Config.FolderPath);

        if (!reorder)
        {
            bool ok = effect switch
            {
                DragDropEffects.Copy => ShellItems.Copy(_hwnd, paths, _currentFolder),
                DragDropEffects.Link => CreateLinks(paths),
                _ => ShellItems.Move(_hwnd, paths, _currentFolder),
            };
            if (!ok) { ScheduleRefresh(); return; }
            if (effect == DragDropEffects.Link) names = [];
        }

        if (atRoot && index >= 0 && names.Count > 0 && (reorder || Config.Sort == FenceSort.Manual))
        {
            var order = _items.Select(i => i.FileName).ToList();
            int insertAt = index;
            foreach (var n in names)
            {
                int existing = order.FindIndex(o => o.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) { order.RemoveAt(existing); if (existing < insertAt) insertAt--; }
            }
            order.InsertRange(Math.Clamp(insertAt, 0, order.Count), names);
            Config.Order = order;
            Config.Sort = FenceSort.Manual;
            App.Host.Save();
        }
        Reload();
        ScheduleRefresh(); // Dateisystem-Ereignisse nachziehen
    }

    bool CreateLinks(string[] paths)
    {
        foreach (var p in paths) ShellItems.CreateShortcut(p, _currentFolder);
        return true;
    }

    // ---------------------------------------------------------------- Tastatur
    /// <summary>
    /// Nach einem Klick in den Inhalt bekommt der Bereich die Tastatur (Entf, F2, Strg+C …),
    /// bleibt dabei aber unter allen Fenstern (WM_WINDOWPOSCHANGING hält ihn unten).
    /// </summary>
    void TakeKeyboard()
    {
        if (TitleEdit.IsVisible) return;
        if (!IsActive)
        {
            _allowActivate = true;
            SetExStyle(_hwnd, GetExStyle(_hwnd) & ~WS_EX_NOACTIVATE);
            ForceForeground(_hwnd);
            base.Activate();
        }
        Keyboard.Focus(this);
    }

    void ReleaseKeyboard()
    {
        if (TitleEdit.IsVisible || _hwnd == IntPtr.Zero) return;
        _allowActivate = false;
        SetExStyle(_hwnd, GetExStyle(_hwnd) | WS_EX_NOACTIVATE);
        SendToBottom();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (TitleEdit.IsVisible || e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var selection = Selected;
        var paths = selection.Select(i => i.Path).ToList();
        e.Handled = true;

        switch (key)
        {
            case Key.Delete when paths.Count > 0:
                if (mods.HasFlag(ModifierKeys.Shift)) ShellItems.DeletePermanently(_hwnd, paths);
                else ShellItems.Recycle(_hwnd, paths);
                ScheduleRefresh();
                break;
            case Key.F2 when paths.Count == 1:
                RenameItem(paths[0]);
                break;
            case Key.Enter when selection.Count > 0:
                foreach (var item in selection.Take(15)) OpenItem(item);
                break;
            case Key.A when mods == ModifierKeys.Control:
                foreach (var i in _items) i.IsSelected = true;
                break;
            case Key.C when mods == ModifierKeys.Control && paths.Count > 0:
                ShellItems.SetClipboard(paths, cut: false);
                break;
            case Key.X when mods == ModifierKeys.Control && paths.Count > 0:
                ShellItems.SetClipboard(paths, cut: true);
                break;
            case Key.V when mods == ModifierKeys.Control:
                if (ShellItems.PasteInto(_hwnd, _currentFolder)) ScheduleRefresh();
                break;
            case Key.F5:
                RefreshAppearance();
                break;
            case Key.Back when !PathEquals(_currentFolder, Config.FolderPath):
                Back_Click(this, new RoutedEventArgs());
                break;
            case Key.Escape:
                foreach (var i in _items) i.IsSelected = false;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                MoveSelection(key, mods.HasFlag(ModifierKeys.Shift));
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Pfeiltasten: in der Kachelansicht zeilenweise, in der Liste nur hoch/runter.</summary>
    void MoveSelection(Key key, bool extend)
    {
        if (_items.Count == 0) return;
        int current = _anchor is not null ? _items.IndexOf(_anchor) : -1;
        int perRow = Config.ViewMode == FenceViewMode.List ? 1 : Math.Max(1, (int)((ItemsHost.ActualWidth) / (TileWidth + 4)));
        int next = current < 0 ? 0 : key switch
        {
            Key.Left => current - (perRow == 1 ? 0 : 1),
            Key.Right => current + (perRow == 1 ? 0 : 1),
            Key.Up => current - perRow,
            _ => current + perRow,
        };
        next = Math.Clamp(next, 0, _items.Count - 1);
        var target = _items[next];
        if (!extend) foreach (var i in _items) i.IsSelected = i == target;
        else target.IsSelected = true;
        _anchor = target;
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement fe) fe.BringIntoView();
    }

    // ---------------------------------------------------------------- Umbenennen des Bereichs
    public void BeginRename()
    {
        if (Config.Collapsed && !_peeking) Peek(true);
        TitleEdit.Text = Config.Title;
        TitleText.Visibility = Visibility.Hidden;
        TitleEdit.Visibility = Visibility.Visible;
        _renameActivated = false;
        _allowActivate = true;
        SetExStyle(_hwnd, GetExStyle(_hwnd) & ~WS_EX_NOACTIVATE);
        ForceForeground(_hwnd);
        base.Activate();
        TitleEdit.Focus();
        Keyboard.Focus(TitleEdit);
        TitleEdit.SelectAll();
    }

    void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
    }

    void TitleEdit_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Fokus innerhalb des Fensters verloren (z. B. Klick auf ein Symbol) → übernehmen.
        // Fensterwechsel behandelt OnDeactivated.
        if (TitleEdit.IsVisible && IsActive && e.NewFocus is not null && !TitleEdit.IsAncestorOf((DependencyObject)e.NewFocus)) EndRename(true);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (TitleEdit.IsVisible && _renameActivated) EndRename(true);
        else ReleaseKeyboard();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (TitleEdit.IsVisible) { _renameActivated = true; Keyboard.Focus(TitleEdit); }
    }

    bool _renameActivated;

    void EndRename(bool commit)
    {
        if (!TitleEdit.IsVisible) return;
        var text = TitleEdit.Text.Trim();
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _allowActivate = false;
        _renameActivated = false;
        SetExStyle(_hwnd, GetExStyle(_hwnd) | WS_EX_NOACTIVATE);
        SendToBottom();
        if (commit && text.Length > 0 && text != Config.Title) App.Host.Fences.Rename(this, text);
        UpdateTitle();
    }

    // ---------------------------------------------------------------- Menü
    void ShowFenceMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        MenuItem Item(string header, string glyph, Action click, bool? check = null)
        {
            var mi = new MenuItem { Header = header, Icon = glyph };
            if (check is { } c) { mi.IsCheckable = true; mi.IsChecked = c; }
            mi.Click += (_, _) => click();
            return mi;
        }

        menu.Items.Add(Item("Umbenennen", "\uE8AC", () => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, BeginRename)));

        var view = new MenuItem { Header = "Ansicht", Icon = "" };
        foreach (var (label, size) in new[] { ("Große Symbole", 64), ("Mittlere Symbole", 48), ("Kleine Symbole", 32) })
            view.Items.Add(Item(label, "", () => SetView(FenceViewMode.Tiles, size), Config.ViewMode == FenceViewMode.Tiles && Config.IconSize == size));
        view.Items.Add(Item("Liste", "", () => SetView(FenceViewMode.List, Config.IconSize), Config.ViewMode == FenceViewMode.List));
        menu.Items.Add(view);

        var sort = new MenuItem { Header = "Sortieren nach", Icon = "" };
        foreach (var (label, mode) in new[] { ("Eigene Reihenfolge", FenceSort.Manual), ("Name", FenceSort.Name), ("Typ", FenceSort.Type), ("Änderungsdatum", FenceSort.Modified) })
            sort.Items.Add(Item(label, "", () => { Config.Sort = mode; Reload(); App.Host.Save(); }, Config.Sort == mode));
        menu.Items.Add(sort);

        var color = new MenuItem { Header = "Farbe", Icon = "" };
        color.Items.Add(Item("Theme-Farbe", "", () => SetTint(null), Config.Tint is null));
        foreach (var (label, hex) in Palette)
        {
            var mi = Item(label, "", () => SetTint(hex), string.Equals(Config.Tint, hex, StringComparison.OrdinalIgnoreCase));
            mi.Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(ThemeManager.ParseColor(hex, Colors.Gray)) };
            color.Items.Add(mi);
        }
        menu.Items.Add(color);

        menu.Items.Add(Item(Config.Collapsed ? "Ausklappen" : "Einklappen", Config.Collapsed ? "" : "", ToggleCollapsed));
        menu.Items.Add(Item("Position sperren", "", () => { Config.Locked = !Config.Locked; UpdateChrome(); App.Host.Save(); }, Config.Locked));
        menu.Items.Add(Item("Ordner im Bereich durchsuchen", "", () => { Config.NavigateFolders = !Config.NavigateFolders; App.Host.Save(); }, Config.NavigateFolders));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Im Explorer öffnen", "", () => ShellItems.Open(_currentFolder)));
        if (IsPortal)
            menu.Items.Add(Item("Anderen Ordner zeigen …", "", () => { if (Dialog.PickFolder(Config.FolderPath) is { } f) ChangeFolder(f); }));

        var selection = Selected.Select(i => i.Info).ToList();
        var pluginCommands = App.Host.Registry.FenceCommands.Where(c => SafeCan(() => c.CanExecute(this))).ToList();
        if (pluginCommands.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var c in pluginCommands)
                menu.Items.Add(Item(c.DisplayName, "", () => App.Host.RunSafe(c.DisplayName, () => c.Execute(this, selection))));
        }

        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Neu", Icon = "" };
        add.Items.Add(Item("Bereich", "", () => App.Host.Fences.CreateBeside(this, FenceKind.Collection)));
        add.Items.Add(Item("Ordner-Portal …", "", () => App.Host.Fences.CreateBeside(this, FenceKind.Portal)));
        menu.Items.Add(add);
        menu.Items.Add(Item("Einstellungen …", "", App.Host.ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Bereich entfernen …", "", () => App.Host.Fences.Remove(this)));

        menu.Closed += (_, _) => { if (_peeking && !IsMouseOver) _peekOut.Start(); };
        ContextMenu = menu;
        menu.IsOpen = true;
    }

    public static readonly (string Label, string Hex)[] Palette =
    [
        ("Ozean", "#1E4E8C"), ("Petrol", "#0F6E6E"), ("Wald", "#245C36"), ("Bernstein", "#8C5A12"),
        ("Koralle", "#9A3B32"), ("Beere", "#7A2457"), ("Lavendel", "#4E3A8C"), ("Schiefer", "#3B4252"),
    ];

    void SetView(FenceViewMode mode, int size)
    {
        Config.ViewMode = mode;
        Config.IconSize = size;
        UpdateViewMode();
        App.Host.Save();
    }

    void SetTint(string? hex)
    {
        Config.Tint = hex;
        ApplyBackdrop();
        App.Host.Save();
    }

    // ---------------------------------------------------------------- Schnell-Ausblenden
    public void FadeOut()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => { if (Root.Opacity < 0.01) Hide(); };
        Root.BeginAnimation(OpacityProperty, a);
    }

    public void FadeIn()
    {
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Show();
        SendToBottom();
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    // ---------------------------------------------------------------- Aufräumen
    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.Instance.Changed -= OnThemeChanged;
        _watcher?.Dispose();
        if (_animating) CompositionTarget.Rendering -= AnimFrame;
        base.OnClosed(e);
    }

    public void RefreshAppearance()
    {
        foreach (var vm in _items) vm.IconPx = 0;
        Reload();
    }

    static bool IsInside<T>(object source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    public static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
