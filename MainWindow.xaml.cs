using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AutoText
{
    public partial class MainWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf(typeof(INPUT));
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_UNICODE = 0x0004;
        const uint KEYEVENTF_KEYUP = 0x0002;

        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int HOTKEY_ID = 9000;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_T = 0x54;
        const int WM_HOTKEY = 0x0312;

        // nullable fields to silence CS8618
        private HwndSource? _hwndSource;
        private CancellationTokenSource? _cts;
        private bool _isSending = false;
        private ManualResetEventSlim? _pauseEvent;
        private bool _isPaused = false;

        // 当处于暂停时，若为 true 则暂停按钮变为“停止”并执行终止发送
        private bool _pauseCanStop = false;

        private IntPtr _lockedWindowHandle = IntPtr.Zero;
        private string _lockedWindowTitle = "";

        private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.ini");

        private IntPtr _currentTargetHwnd = IntPtr.Zero;
        private bool _didClickToFocus = false;

        // track whether settings were modified and need saving
        private bool _settingsDirty = false;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        // Random helpers for typing delays
        private static readonly Random _globalRandom = new Random();
        private static readonly ThreadLocal<Random> _threadLocalRandom = new ThreadLocal<Random>(() => new Random(_globalRandom.Next()));

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            BtnRefreshFiles_Click(null, null);
            EnsureLangFolderAndDefault();
            // Load settings and then hook change handlers
            LoadSettingsToUI();
            HookSettingsChangeHandlers();

            ChkTopMost.Checked += ChkTopMost_Checked;
            ChkTopMost.Unchecked += ChkTopMost_Unchecked;
        }

        // 语言文件信息结构
        private class LangInfo
        {
            public string FilePath { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Author { get; set; } = "";
            public override string ToString() => Name;
        }

        // 语言文件夹和默认文件名
        private readonly string _langDir = Path.Combine(AppContext.BaseDirectory, "lang");
        private readonly string _defaultLangFile = "zh-CN.lang";

        // 自动创建语言文件夹和默认中文
        private void EnsureLangFolderAndDefault()
        {
            if (!Directory.Exists(_langDir))
                Directory.CreateDirectory(_langDir);

            string defaultLangPath = Path.Combine(_langDir, _defaultLangFile);
            if (!File.Exists(defaultLangPath))
            {
                File.WriteAllText(defaultLangPath,
@"name=简体中文
description=默认中文语言
author=AutoText 官方
", Encoding.UTF8);
            }
        }

        // 语言文件列表刷新
        private void BtnRefreshLang_Click(object? sender, RoutedEventArgs? e)
        {
            if (!Directory.Exists(_langDir))
                Directory.CreateDirectory(_langDir);

            var files = Directory.GetFiles(_langDir, "*.lang");
            var list = new List<LangInfo>();
            foreach (var file in files)
            {
                var info = ParseLangFile(file);
                info.FilePath = file;
                list.Add(info);
            }
            if (CbLangFiles != null)
            {
                CbLangFiles.ItemsSource = list;
                CbLangFiles.DisplayMemberPath = "Name";
                // 默认选中第一个或上次选中的
                var last = ReadIni("Lang", "LastLangFile", _defaultLangFile);
                var sel = list.FirstOrDefault(l => Path.GetFileName(l.FilePath).Equals(last, StringComparison.OrdinalIgnoreCase))
                          ?? list.FirstOrDefault();
                CbLangFiles.SelectedItem = sel;
            }
        }

        // 解析语言文件
        private LangInfo ParseLangFile(string file)
        {
            var info = new LangInfo { FilePath = file, Name = Path.GetFileNameWithoutExtension(file) };
            try
            {
                string content = File.ReadAllText(file, Encoding.UTF8);
                if (!string.IsNullOrEmpty(content) && content[0] == '\uFEFF')
                    content = content.Substring(1);

                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    // 跳过空行和注释行
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#") || trimmedLine.StartsWith(";"))
                        continue;

                    var idx = trimmedLine.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = trimmedLine.Substring(0, idx).Trim().ToLowerInvariant();
                        var val = trimmedLine.Substring(idx + 1).Trim();
                        switch (key)
                        {
                            case "name": info.Name = val; break;
                            case "description": info.Description = val; break;
                            case "author": info.Author = val; break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ParseLangFile error: {ex.Message}");
            }
            return info;
        }

        private void LoadLangDict(LangInfo lang)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string content = File.ReadAllText(lang.FilePath, Encoding.UTF8);
                if (!string.IsNullOrEmpty(content) && content[0] == '\uFEFF')
                    content = content.Substring(1);

                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    // 跳过空行和注释行
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#") || trimmedLine.StartsWith(";"))
                        continue;

                    var idx = trimmedLine.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = trimmedLine.Substring(0, idx).Trim();
                        var val = trimmedLine.Substring(idx + 1).Trim();
                        if (!string.IsNullOrEmpty(key))
                            dict[key] = val;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadLangDict error: {ex.Message}");
            }
            _langDict = dict;
            _currentLang = lang;
        }

        // 语言选择变更
        private void CbLangFiles_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
        {
            if (CbLangFiles?.SelectedItem is LangInfo info)
            {
                LoadLangDict(info);
                // 先应用语言到 UI（不会覆盖后面手动设置的描述/作者）
                ApplyLangToUI();

                if (TxtLangDesc != null) TxtLangDesc.Text = $"{L("ui.lbl.langdesc", "描述：")}{info.Description}";
                if (TxtLangAuthor != null) TxtLangAuthor.Text = $"{L("ui.lbl.langauthor", "作者：")}{info.Author}";

                WriteIni("Lang", "LastLangFile", Path.GetFileName(info.FilePath));
            }
            else
            {
                if (TxtLangDesc != null) TxtLangDesc.Text = L("ui.lbl.langdesc", "描述：");
                if (TxtLangAuthor != null) TxtLangAuthor.Text = L("ui.lbl.langauthor", "作者：");
            }
        }

        private void HookSettingsChangeHandlers()
        {
            // More.* fields
            if (TbPreKeys != null) TbPreKeys.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbPrefix != null) TbPrefix.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbPostKeys != null) TbPostKeys.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbSuffix != null) TbSuffix.TextChanged += (s, e) => MarkSettingsDirty();

            // Timing / behavior / sending / hotkey fields
            if (TbDelayMin != null) TbDelayMin.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbDelayMax != null) TbDelayMax.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbLinePause != null) TbLinePause.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbLoopInterval != null) TbLoopInterval.TextChanged += (s, e) => MarkSettingsDirty();
            if (TbCountdown != null) TbCountdown.TextChanged += (s, e) => MarkSettingsDirty();

            if (CbSendOrder != null) CbSendOrder.SelectionChanged += (s, e) => MarkSettingsDirty();
            if (ChkLoop != null) { ChkLoop.Checked += (s, e) => MarkSettingsDirty(); ChkLoop.Unchecked += (s, e) => MarkSettingsDirty(); }
            if (ChkSimulateTypos != null) { ChkSimulateTypos.Checked += (s, e) => MarkSettingsDirty(); ChkSimulateTypos.Unchecked += (s, e) => MarkSettingsDirty(); }

            if (RbSendInput != null) { RbSendInput.Checked += (s, e) => MarkSettingsDirty(); RbClipboard.Checked += (s, e) => MarkSettingsDirty(); }
            if (RbEnter != null) { RbEnter.Checked += (s, e) => MarkSettingsDirty(); RbCtrlEnter.Checked += (s, e) => MarkSettingsDirty(); }

            if (ChkGlobalHotkey != null) { ChkGlobalHotkey.Checked += (s, e) => MarkSettingsDirty(); ChkGlobalHotkey.Unchecked += (s, e) => MarkSettingsDirty(); }
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => { SafeSetStatus(L("ui.status.modified", "设置已修改（未保存）")); });
            }
            else
            {
                SafeSetStatus(L("ui.status.modified", "设置已修改（未保存）"));
            }
        }

        private void SafeSetStatus(string text)
        {
            if (TxtStatus != null) TxtStatus.Text = text;
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource?.AddHook(HwndHook);
            try
            {
                if (ChkGlobalHotkey != null && ChkGlobalHotkey.IsChecked == true)
                    RegisterGlobalHotkey();
            }
            catch { }
            if (ChkTopMost != null) Topmost = ChkTopMost.IsChecked == true;

            // 确保语言列表在 UI 完成后刷新一次（解决每次打开需手动刷新问题）
            BtnRefreshLang_Click(null, null);

            // 加载上次选择的语言
            var lastLangFile = ReadIni("Lang", "LastLangFile", _defaultLangFile);
            var langList = (CbLangFiles?.ItemsSource as IEnumerable<LangInfo>)?.ToList() ?? new List<LangInfo>();
            var lang = langList.FirstOrDefault(l => Path.GetFileName(l.FilePath).Equals(lastLangFile, StringComparison.OrdinalIgnoreCase))
                       ?? langList.FirstOrDefault();
            if (lang != null)
            {
                CbLangFiles.SelectedItem = lang;
                LoadLangDict(lang);
                ApplyLangToUI();
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // prompt to save if dirty
            if (_settingsDirty)
            {
                var res = MessageBox.Show(L("ui.msg.saveask", "设置已修改，是否保存更改？"), L("ui.msg.saveask.title", "保存设置"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (res == MessageBoxResult.Yes)
                {
                    try
                    {
                        SaveSettingsToIni();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(L("ui.msg.savefail", "保存设置失败") + ": " + ex.Message, L("ui.msg.savefail.title", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                        e.Cancel = true;
                        return;
                    }
                }
            }

            UnregisterGlobalHotkey();
            _hwndSource?.RemoveHook(HwndHook);
            try { _cts?.Cancel(); } catch { }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    _ = Dispatcher.InvokeAsync(() => ToggleStartByHotkey());
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void RegisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_T);
        }

        private void UnregisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
        }

        private void ToggleStartByHotkey()
        {
            if (_isSending) StopSending();
            else _ = StartSendingWithCountdownAsync();
        }

        private void BtnRefreshFiles_Click(object? sender, RoutedEventArgs? e)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            var files = Directory.GetFiles(dataDir, "*.txt").Select(f => new FileInfo(f)).OrderBy(f => f.Name.ToLowerInvariant()).ToList();
            if (LstFiles != null) LstFiles.ItemsSource = files;
            if (CbFiles != null) CbFiles.ItemsSource = files;
            if (files.Any())
            {
                string last = ReadIni("General", "LastFile", "");
                FileInfo lastFi = files.FirstOrDefault(f => string.Equals(f.Name, last, StringComparison.OrdinalIgnoreCase));
                if (lastFi != null && LstFiles != null && CbFiles != null)
                {
                    LstFiles.SelectedItem = lastFi;
                    CbFiles.SelectedItem = lastFi;
                }
                else if (LstFiles != null && CbFiles != null)
                {
                    LstFiles.SelectedIndex = 0;
                    CbFiles.SelectedIndex = 0;
                }
            }
        }

        // Click handler for "锁定当前窗口" 按钮 (defined in XAML)
        private void BtnLockWindow_Click(object? sender, RoutedEventArgs e)
        {
            IntPtr hwnd = GetForegroundWindow();
            var helper = new WindowInteropHelper(this);
            if (hwnd == helper.Handle)
            {
                MessageBox.Show(L("ui.msg.locktip", "请先将目标窗口激活，然后点击锁定。"), L("ui.msg.locktip.title", "请激活目标窗口"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _lockedWindowHandle = hwnd;
            _lockedWindowTitle = GetWindowTitle(hwnd);
            if (TxtLockedWindow != null) TxtLockedWindow.Text = $"{L("ui.tab.settings", "设置")}{_lockedWindowTitle}";
            if (RbLockedWindow != null) RbLockedWindow.IsChecked = true;

            // persist locked title (句柄不可跨会话)
            WriteIni("Target", "LockedWindowTitle", _lockedWindowTitle);
            WriteIni("Target", "LockEnabled", "true");
            WriteIni("Target", "TargetMode", "Locked");

            MarkSettingsDirty();
        }

        // Click handler for "保存设置" 按钮 (defined in XAML)
        private void BtnSaveSettings_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettingsToIni();
                MessageBox.Show(L("ui.msg.saveok", "设置已保存到 settings.ini（会话与下次启动生效）。"), L("ui.msg.saveok.title", "已保存"), MessageBoxButton.OK, MessageBoxImage.Information);

                // Re-register hotkey if changed
                UnregisterGlobalHotkey();
                if (ChkGlobalHotkey != null && ChkGlobalHotkey.IsChecked == true)
                    RegisterGlobalHotkey();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("ui.msg.savefail", "保存设置失败") + ": " + ex.Message, L("ui.msg.savefail.title", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LstFiles_SelectionChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var fi = LstFiles?.SelectedItem as FileInfo;
            if (fi == null)
            {
                if (TbPreview != null) TbPreview.Text = "";
                if (TxtFileEncoding != null) TxtFileEncoding.Text = "";
                return;
            }

            try
            {
                var (text, encodingName) = ReadFileWithAutoEncoding(fi.FullName);
                if (TbPreview != null) TbPreview.Text = text;
                if (TxtFileEncoding != null) TxtFileEncoding.Text = $"{L("ui.lbl.encoding", "检测编码：")}{encodingName}";
                if (CbFiles != null) CbFiles.SelectedItem = fi;
            }
            catch (UnauthorizedAccessException)
            {
                if (TbPreview != null) TbPreview.Text = L("ui.msg.nofileaccess", "无权访问此文件");
                if (TxtFileEncoding != null) TxtFileEncoding.Text = L("ui.msg.nofileaccess.title", "访问被拒绝");
            }
            catch (Exception ex)
            {
                if (TbPreview != null) TbPreview.Text = L("ui.msg.filereadfail", "读取文件失败") + ": " + ex.Message;
                if (TxtFileEncoding != null) TxtFileEncoding.Text = L("ui.msg.filereadfail.title", "读取错误");
            }
        }

        private (string text, string encodingName) ReadFileWithAutoEncoding(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.GetDirectoryName(path) == null)
                throw new ArgumentException("Invalid file path");

            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                return (Encoding.UTF8.GetString(raw, 3, raw.Length - 3), "UTF-8 w/ BOM");
            }
            try
            {
                var utf8 = new UTF8Encoding(false, true);
                string s = utf8.GetString(raw);
                return (s, "UTF-8");
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    var gb = Encoding.GetEncoding("GB18030");
                    string s = gb.GetString(raw);
                    return (s, "GB18030/GBK");
                }
                catch (DecoderFallbackException)
                {
                    string s = Encoding.Default.GetString(raw);
                    return (s, "Default");
                }
            }
        }

        private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending)
            {
                // 当前正在发送 -> 停止
                StopSending();
            }
            else
            {
                // 当前未发送 -> 开始
                await StartSendingWithCountdownAsync();
            }
        }

        // 暂停功能已移除：保留占位以免引用错误
        private void PauseSending() { }
        private Task ResumeSendingAsync() => Task.CompletedTask;

        private void ChkTopMost_Checked(object? sender, RoutedEventArgs e) { Topmost = true; }
        private void ChkTopMost_Unchecked(object? sender, RoutedEventArgs e) { Topmost = false; }

        private void LoadSettingsToUI()
        {
            // Try to load and populate UI; swallow errors to avoid startup crash
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    // set defaults
                    SafeSetText(TbDelayMin, "50");
                    SafeSetText(TbDelayMax, "150");
                    SafeSetText(TbLinePause, "200");
                    SafeSetText(TbLoopInterval, "1000");
                    SafeSetText(TbCountdown, "3");
                    return;
                }

                var topMost = ReadIni("General", "TopMost", "false");
                if (ChkTopMost != null) ChkTopMost.IsChecked = string.Equals(topMost, "true", StringComparison.OrdinalIgnoreCase);
                if (ChkTopMost != null) Topmost = ChkTopMost.IsChecked == true;

                var lockEnabled = ReadIni("Target", "LockEnabled", "false");
                var lockedTitle = ReadIni("Target", "LockedWindowTitle", "");
                var targetMode = ReadIni("Target", "TargetMode", "Active");
                if (string.Equals(lockEnabled, "true", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(lockedTitle))
                {
                    if (TxtLockedWindow != null) TxtLockedWindow.Text = $"已锁定：{lockedTitle}";
                    _lockedWindowTitle = lockedTitle;
                    if (RbLockedWindow != null) RbLockedWindow.IsChecked = true;
                    if (targetMode.Equals("Locked", StringComparison.OrdinalIgnoreCase)) if (RbLockedWindow != null) RbLockedWindow.IsChecked = true;
                    else if (RbActiveWindow != null) RbActiveWindow.IsChecked = true;
                }
                else if (RbActiveWindow != null) RbActiveWindow.IsChecked = true;

                var sendMethod = ReadIni("Sending", "Method", "SendInput");
                if (RbSendInput != null) RbSendInput.IsChecked = string.Equals(sendMethod, "SendInput", StringComparison.OrdinalIgnoreCase);
                if (RbClipboard != null) RbClipboard.IsChecked = string.Equals(sendMethod, "Clipboard", StringComparison.OrdinalIgnoreCase);

                var enterMode = ReadIni("Sending", "EnterMode", "Enter");
                if (enterMode.Equals("CtrlEnter", StringComparison.OrdinalIgnoreCase))
                {
                    if (RbCtrlEnter != null) RbCtrlEnter.IsChecked = true;
                    if (RbEnter != null) RbEnter.IsChecked = false;
                }
                else
                {
                    if (RbEnter != null) RbEnter.IsChecked = true;
                    if (RbCtrlEnter != null) RbCtrlEnter.IsChecked = false;
                }

                // More.* fields
                SafeSetText(TbPreKeys, ReadIni("More", "PreKeys", ""));
                SafeSetText(TbPrefix, ReadIni("More", "Prefix", ""));
                SafeSetText(TbPostKeys, ReadIni("More", "PostKeys", ""));
                SafeSetText(TbSuffix, ReadIni("More", "Suffix", ""));

                SafeSetText(TbDelayMin, ReadIni("Timing", "DelayMin", SafeGetText(TbDelayMin, "50")));
                SafeSetText(TbDelayMax, ReadIni("Timing", "DelayMax", SafeGetText(TbDelayMax, "150")));
                SafeSetText(TbLinePause, ReadIni("Timing", "LinePause", SafeGetText(TbLinePause, "200")));
                if (ChkSimulateTypos != null) ChkSimulateTypos.IsChecked = string.Equals(ReadIni("Timing", "SimulateTypos", "false"), "true", StringComparison.OrdinalIgnoreCase);

                var order = ReadIni("Behavior", "Order", "Sequential");
                if (CbSendOrder != null) CbSendOrder.SelectedIndex = order == "Random" ? 1 : 0;

                if (ChkLoop != null) ChkLoop.IsChecked = string.Equals(ReadIni("Behavior", "Loop", "false"), "true", StringComparison.OrdinalIgnoreCase);
                SafeSetText(TbLoopInterval, ReadIni("Behavior", "LoopInterval", SafeGetText(TbLoopInterval, "1000")));

                if (ChkGlobalHotkey != null) ChkGlobalHotkey.IsChecked = string.Equals(ReadIni("Hotkey", "Enabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                SafeSetText(TbCountdown, ReadIni("Hotkey", "Countdown", SafeGetText(TbCountdown, "3")));

                // loaded settings -> clear dirty flag
                _settingsDirty = false;
                SafeSetStatus(L("ui.status.loaded", "设置加载完成"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadSettingsToUI error: {ex.Message}");
            }
        }

        // 保留唯一的SafeGetText和SafeSetText定义
        private string SafeGetText(TextBox? tb, string defaultValue)
        {
            return tb?.Text ?? defaultValue;
        }
        private void SafeSetText(TextBox? tb, string value)
        {
            if (tb != null) tb.Text = value;
        }

        private async Task<bool> EnsureTargetForegroundAsync(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return false;

            bool wasTop = Topmost;
            try
            {
                if (wasTop) Topmost = false;

                uint targetTid = GetWindowThreadProcessId(targetHwnd, out _);
                uint currentTid = GetCurrentThreadId();
                AttachThreadInput(currentTid, targetTid, true);
                if (IsIconic(targetHwnd)) ShowWindow(targetHwnd, SW_RESTORE);
                bool ok = SetForegroundWindow(targetHwnd);
                await Task.Delay(180);
                AttachThreadInput(currentTid, targetTid, false);
                return ok;
            }
            catch
            {
                return false;
            }
            finally
            {
                await Task.Delay(50);
                Topmost = wasTop;
            }
        }

        private async Task<bool> ClickCenterOfWindowAsync(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                if (!GetWindowRect(hWnd, out RECT r)) return false;
                int cx = (r.left + r.right) / 2;
                int cy = (r.top + r.bottom) / 2;

                GetCursorPos(out POINT orig);
                SetCursorPos(cx, cy);
                await Task.Delay(80);

                var inputs = new INPUT[2];
                inputs[0].type = INPUT_MOUSE;
                inputs[0].U.mi.dx = 0;
                inputs[0].U.mi.dy = 0;
                inputs[0].U.mi.mouseData = 0;
                inputs[0].U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
                inputs[0].U.mi.time = 0;
                inputs[0].U.mi.dwExtraInfo = IntPtr.Zero;

                inputs[1].type = INPUT_MOUSE;
                inputs[1].U.mi.dx = 0;
                inputs[1].U.mi.dy = 0;
                inputs[1].U.mi.mouseData = 0;
                inputs[1].U.mi.dwFlags = MOUSEEVENTF_LEFTUP;
                inputs[1].U.mi.time = 0;
                inputs[1].U.mi.dwExtraInfo = IntPtr.Zero;

                SendInput(2, inputs, INPUT.Size);
                await Task.Delay(80);

                SetCursorPos(orig.X, orig.Y);
                await Task.Delay(20);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void BtnPause_Click(object? sender, RoutedEventArgs? e)
        {
            // 暂停功能已移除，按钮保持禁用
        }

        private async Task StartSendingWithCountdownAsync()
        {
            if (CbFiles == null || CbFiles.SelectedItem is not FileInfo fi)
            {
                MessageBox.Show(L("ui.msg.nofile", "请先在导入页选择一个 txt 文件。"), L("ui.msg.nofile.title", "未选择文件"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ChkTopMost != null && ChkTopMost.IsChecked == true) Topmost = true;

            if (!int.TryParse(SafeGetText(TbCountdown, "3"), out int countdownSecs)) countdownSecs = 3;
            if (countdownSecs > 0)
            {
                SafeSetStatus(string.Format(L("ui.msg.countdown", "倒计时 {0} 秒，请切换到目标窗口（或若已锁定则忽略）..."), countdownSecs));
                for (int i = countdownSecs; i > 0; i--)
                {
                    SafeSetStatus(string.Format(L("ui.msg.countdown2", "倒计时 {0} 秒..."), i));
                    await Task.Delay(1000);
                }
            }

            var selectedFi = CbFiles.SelectedItem as FileInfo;
            if (selectedFi != null) WriteIni("General", "LastFile", selectedFi.Name);

            var helper = new WindowInteropHelper(this);
            IntPtr ourHwnd = helper.Handle;
            IntPtr targetHwnd = IntPtr.Zero;

            var targetMode = ReadIni("Target", "TargetMode", "Active");
            if (targetMode.Equals("Locked", StringComparison.OrdinalIgnoreCase) && _lockedWindowHandle != IntPtr.Zero)
            {
                targetHwnd = _lockedWindowHandle;
                var ok = await EnsureTargetForegroundAsync(targetHwnd);
                if (!ok)
                {
                    MessageBox.Show(L("ui.msg.lockfail", "无法将锁定的目标窗口置前台。请确保目标窗口仍然存在并不是更高权限进程。"), L("ui.msg.lockfail.title", "前台失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == ourHwnd)
                {
                    MessageBox.Show(L("ui.msg.nofocus", "当前仍为本程序窗口。请切换到目标程序的输入框或锁定目标窗口。"), L("ui.msg.nofocus.title", "目标窗口未切换"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                targetHwnd = fg;
            }

            _currentTargetHwnd = targetHwnd;
            _didClickToFocus = false;

            // 建立 CancellationTokenSource
            _cts = new CancellationTokenSource();
            _isSending = true;
            // 更新 UI 按钮文字为“停止”以提示正在发送
            Dispatcher.Invoke(() =>
            {
                if (BtnStartStop != null) BtnStartStop.Content = L("ui.btn.stop", "停止");
            });

            try
            {
                var (text, _) = ReadFileWithAutoEncoding(fi.FullName);
                var settings = ReadSettingsFromUI();
                // Run sending on thread-pool to keep UI responsive for pause/stop
                await Task.Run(() => SendTextAsync(text, settings, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                SafeSetStatus(L("ui.status.cancelled", "已取消"));
            }
            catch (Exception ex)
            {
                SafeSetStatus(L("ui.status.stopped", "发送出错: ") + ex.Message);
            }
            finally
            {
                _isSending = false;
                _isPaused = false;

                
                Dispatcher.Invoke(() =>
                {
                    if (BtnStartStop != null) BtnStartStop.Content = L("ui.btn.start", "开始（发送）");
                    // if (BtnPause != null) { BtnPause.IsEnabled = false; BtnPause.Content = L("ui.btn.pause", "暂停"); }
                    SafeSetStatus(L("ui.status.ready", "就绪"));
                });
                

                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        private void StopSending()
        {
            // 取消并清理发送状态
            try { _cts?.Cancel(); } catch { }
            _isSending = false;
            _isPaused = false;

            Dispatcher.Invoke(() =>
            {
                if (BtnStartStop != null) BtnStartStop.Content = L("ui.btn.start", "开始（发送）");
                // if (BtnPause != null) { BtnPause.IsEnabled = false; BtnPause.Content = L("ui.btn.pause", "暂停"); }
                SafeSetStatus(L("ui.status.stopped", "已停止"));
            });

            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _pauseCanStop = false;
        }

        // -------------------------
        // Helper: get window title
        private string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // Read/Write INI helpers (keep existing)
        private Dictionary<string, Dictionary<string, string>> ReadIniAll()
        {
            var data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_settingsPath)) return data;
            string currentSection = "";
            try
            {
                foreach (var raw in File.ReadAllLines(_settingsPath))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        if (!data.ContainsKey(currentSection))
                            data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        continue;
                    }
                    var idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = line.Substring(0, idx).Trim();
                        var val = line.Substring(idx + 1).Trim();
                        if (!data.ContainsKey(currentSection))
                            data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        data[currentSection][key] = val;
                    }
                }
            }
            catch { }
            return data;
        }

        private string ReadIni(string section, string key, string defaultValue)
        {
            try
            {
                var all = ReadIniAll();
                if (all.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var v)) return v;
            }
            catch { }
            return defaultValue;
        }

        private void WriteIni(string section, string key, string value)
        {
            var all = ReadIniAll();
            if (!all.TryGetValue(section, out var sec))
            {
                sec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                all[section] = sec;
            }
            sec[key] = value ?? "";
            var sb = new StringBuilder();
            foreach (var secName in all.Keys)
            {
                sb.AppendLine($"[{secName}]");
                foreach (var kv in all[secName])
                {
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                }
                sb.AppendLine();
            }
            File.WriteAllText(_settingsPath, sb.ToString(), Encoding.UTF8);
        }

        private Dictionary<string, string> _langDict = new();
        private LangInfo? _currentLang;

        private string L(string key, string fallback = "")
        {
            if (_langDict.TryGetValue(key, out var v)) return v;
            return fallback;
        }

        // 暂停逻辑已移除：保留占位以便未来恢复更复杂的暂停/继续实现。

        // 发送字符串（SendInput 或 剪贴板）
        private async Task SendStringAsync(string s, bool useSendInput, CancellationToken token)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (useSendInput)
            {
                foreach (var ch in s)
                {
                    token.ThrowIfCancellationRequested();
                    // send unicode char via SendInput keyboard events
                    var inputs = new INPUT[2];
                    inputs[0].type = INPUT_KEYBOARD;
                    inputs[0].U.ki.wVk = 0;
                    inputs[0].U.ki.wScan = ch;
                    inputs[0].U.ki.dwFlags = KEYEVENTF_UNICODE;
                    inputs[0].U.ki.time = 0;
                    inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;

                    inputs[1].type = INPUT_KEYBOARD;
                    inputs[1].U.ki.wVk = 0;
                    inputs[1].U.ki.wScan = ch;
                    inputs[1].U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                    inputs[1].U.ki.time = 0;
                    inputs[1].U.ki.dwExtraInfo = IntPtr.Zero;

                    SendInput(2, inputs, INPUT.Size);
                    // per-char delay (use small delay; overall speed controlled by UI settings)
                    await Task.Delay(1, token);
                }
            }
            else
            {
                // Clipboard paste
                string? prev = null;
                try
                {
                    // get previous clipboard on STA thread
                    prev = GetClipboardTextSTA();
                    // set new text on STA thread
                    SetClipboardTextSTA(s);
                    // small delay to ensure clipboard is updated
                    await Task.Delay(80, token);
                    SendCtrlV();
                    await Task.Delay(60, token);
                }
                finally
                {
                    try { if (prev != null) SetClipboardTextSTA(prev); } catch { }
                }
            }
        }

        private int GetRandomDelay(int settingsDelayMin, int settingsDelayMax)
        {
            var rnd = _threadLocalRandom.Value ?? new Random();
            if (settingsDelayMax <= settingsDelayMin) return settingsDelayMin;
            return rnd.Next(settingsDelayMin, settingsDelayMax + 1);
        }

        private string? ClipboardGetTextSafe()
        {
            try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
            catch { return null; }
        }
        private void ClipboardSetTextSafe(string s)
        {
            try { System.Windows.Clipboard.SetText(s); } catch { }
        }

        // STA helpers that run clipboard ops on Dispatcher
        private string? GetClipboardTextSTA()
        {
            try
            {
                return Dispatcher.Invoke(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null);
            }
            catch { return null; }
        }

        private void SetClipboardTextSTA(string s)
        {
            try
            {
                Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(s));
            }
            catch { }
        }

        // Special key helpers (very basic parsing of {KEY} patterns, only supports {Ctrl} etc.)
        private void SendSpecialKeys(string keys)
        {
            // 简单实现：不解析复杂序列，仅支持单个特殊键如 {Enter}、{F1}、{Ctrl} 等
            var parts = keys.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.StartsWith("{") && t.EndsWith("}"))
                {
                    var key = t.Substring(1, t.Length - 2).ToLowerInvariant();
                    switch (key)
                    {
                        case "enter": SendEnter(); break;
                        case "tab": SendKeyDownUp(0x09); break;
                        // add more if needed
                        default: break;
                    }
                }
            }
        }

        private void SendEnter()
        {
            SendKeyDownUp(0x0D);
        }

        private void SendCtrlEnter()
        {
            // Ctrl down
            SendKeyDown(0x11);
            SendKeyDownUp(0x0D);
            SendKeyUp(0x11);
        }

        private void SendCtrlV()
        {
            // Ctrl+V
            SendKeyDown(0x11);
            SendKeyDownUp(0x56);
            SendKeyUp(0x11);
        }

        private void SendKeyDownUp(ushort vk)
        {
            // not Unicode path, use virtual key via SendInput with wVk
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = vk;
            inputs[0].U.ki.wScan = 0;
            inputs[0].U.ki.dwFlags = 0;
            inputs[0].U.ki.time = 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;

            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = vk;
            inputs[1].U.ki.wScan = 0;
            inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[1].U.ki.time = 0;
            inputs[1].U.ki.dwExtraInfo = IntPtr.Zero;

            SendInput(2, inputs, INPUT.Size);
        }

        private void SendKeyDown(ushort vk)
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = vk;
            inputs[0].U.ki.wScan = 0;
            inputs[0].U.ki.dwFlags = 0;
            inputs[0].U.ki.time = 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, inputs, INPUT.Size);
        }

        private void SendKeyUp(ushort vk)
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = vk;
            inputs[0].U.ki.wScan = 0;
            inputs[0].U.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[0].U.ki.time = 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, inputs, INPUT.Size);
        }
        private void ApplyLangToUI()
        {
            // 窗口标题
            this.Title = L("ui.title", "AutoText 模拟打字");

            // Tab标题
            if (MainTabs != null)
            {
                if (MainTabs.Items.Count > 0) ((TabItem)MainTabs.Items[0]).Header = L("ui.tab.main", "主页");
                if (MainTabs.Items.Count > 1) ((TabItem)MainTabs.Items[1]).Header = L("ui.tab.import", "导入（预览）");
                if (MainTabs.Items.Count > 2) ((TabItem)MainTabs.Items[2]).Header = L("ui.tab.settings", "设置");
                if (MainTabs.Items.Count > 3) ((TabItem)MainTabs.Items[3]).Header = L("ui.tab.more", "更多操作");
                if (MainTabs.Items.Count > 4) ((TabItem)MainTabs.Items[4]).Header = L("ui.tab.other", "其他");
            }

            // 按钮
            if (BtnStartStop != null) BtnStartStop.Content = L(_isSending ? "ui.btn.stop" : "ui.btn.start", "开始（发送）");
            if (BtnSaveSettings != null) BtnSaveSettings.Content = L("ui.btn.save", "保存设置");
            if (BtnRefreshFiles != null) BtnRefreshFiles.Content = L("ui.btn.refresh", "刷新文件列表");
            if (BtnRefreshLang != null) BtnRefreshLang.Content = L("ui.btn.refreshlang", "刷新语言列表");
            if (BtnLockWindow != null) BtnLockWindow.Content = L("ui.btn.lock", "锁定当前窗口");

            // CheckBox
            if (ChkTopMost != null) ChkTopMost.Content = L("ui.chk.topmost", "窗口置顶（便于启停）");
            if (ChkGlobalHotkey != null) ChkGlobalHotkey.Content = L("ui.chk.hotkey", "启用全局热键 Ctrl+Shift+T");
            if (ChkSimulateTypos != null) ChkSimulateTypos.Content = L("ui.chk.simtypo", "模拟错别字并回退（提高拟真度）");
            if (ChkLoop != null) ChkLoop.Content = L("ui.chk.loop", "循环发送文件");

            // Label/TextBlock
            if (TxtStatus != null) TxtStatus.Text = L("ui.status.ready", "就绪");
            if (TxtFileEncoding != null) TxtFileEncoding.Text = L("ui.lbl.encoding", "检测编码：");
            if (TxtLangDesc != null) TxtLangDesc.Text = L("ui.lbl.langdesc", "描述：");
            if (TxtLangAuthor != null) TxtLangAuthor.Text = L("ui.lbl.langauthor", "作者：");

            // Additional UI texts
            if (TxtWindowTitle != null) TxtWindowTitle.Text = L("ui.title", "AutoText 模拟打字");
            if (TxtNoteSelectPreview != null) TxtNoteSelectPreview.Text = L("ui.note.select_preview_send", "选择文件后可在导入页预览并在目标窗口发送。");
            if (TxtLblSelectedFile != null) TxtLblSelectedFile.Text = L("ui.lbl.selectedfile", "选中文件：");
            if (TxtPreviewTitle != null) TxtPreviewTitle.Text = L("ui.preview.title", "预览（只读）");

            if (TxtSectionTargetWindow != null) TxtSectionTargetWindow.Text = L("ui.section.target_window", "目标窗口选择");
            if (RbActiveWindow != null) RbActiveWindow.Content = L("ui.opt.target_active", "发送到当前窗口（先切换到输入框）");
            if (RbLockedWindow != null) RbLockedWindow.Content = L("ui.opt.target_locked", "锁定特定窗口（锁定后始终发送到该窗口）");

            if (TxtSectionSendMethod != null) TxtSectionSendMethod.Text = L("ui.section.send_method", "发送方式");
            if (RbSendInput != null) RbSendInput.Content = L("ui.opt.send_sendinput", "逐字符 SendInput（默认）");
            if (RbClipboard != null) RbClipboard.Content = L("ui.opt.send_clipboard", "剪贴板粘贴（先复制再 Ctrl+V）");

            if (TxtSectionRhythm != null) TxtSectionRhythm.Text = L("ui.section.rhythm", "打字节奏与行为");
            if (TxtLblDelayPerChar != null) TxtLblDelayPerChar.Text = L("ui.lbl.delay_per_char", "每字符延迟(ms)：");
            if (TxtNoteDelayRange != null) TxtNoteDelayRange.Text = L("ui.note.delay_range", "（可为固定或随机区间）");
            if (TxtLblLinePause != null) TxtLblLinePause.Text = L("ui.lbl.line_pause", "换行额外停顿(ms)：");
            if (TxtLblEnterMethod != null) TxtLblEnterMethod.Text = L("ui.lbl.enter_method", "回车发送方式：");
            if (RbEnter != null) RbEnter.Content = L("ui.opt.enter", "Enter");
            if (RbCtrlEnter != null) RbCtrlEnter.Content = L("ui.opt.ctrlenter", "Ctrl+Enter");

            if (TxtLblSendOrder != null) TxtLblSendOrder.Text = L("ui.lbl.send_order", "发送顺序：");
            if (CbSendOrderItemSequential != null) CbSendOrderItemSequential.Content = L("ui.opt.order_sequential", "按顺序（默认）");
            if (CbSendOrderItemRandom != null) CbSendOrderItemRandom.Content = L("ui.opt.order_random", "随机行发送");
            if (TxtLblLoopInterval != null) TxtLblLoopInterval.Text = L("ui.lbl.loop_interval", "循环间隔(ms)：");

            if (TxtSectionHotkey != null) TxtSectionHotkey.Text = L("ui.section.hotkey", "热键与倒计时");
            if (TxtLblCountdown != null) TxtLblCountdown.Text = L("ui.lbl.countdown", "触发前倒计时(s)：");

            if (TxtMorePreKeysLabel != null) TxtMorePreKeysLabel.Text = L("ui.more.prekeys_label", "输入前按键（特殊键必须写在花括号内，例如 {F1}、{Insert}，普通字符直接写入，仅支持单个键）");
            if (TxtMorePrefixLabel != null) TxtMorePrefixLabel.Text = L("ui.more.prefix_label", "输入前输入内容（例如: Prefix | ）");
            if (TxtMorePostKeysLabel != null) TxtMorePostKeysLabel.Text = L("ui.more.postkeys_label", "输入后按键（同上）");
            if (TxtMoreSuffixLabel != null) TxtMoreSuffixLabel.Text = L("ui.more.suffix_label", "输入后输入内容（例如: | Suffix）");
            if (TxtMoreNote != null) TxtMoreNote.Text = L("ui.more.note", "注意：特殊按键请用花括号包裹，如 {F1}、{Insert}，普通文本可直接输入（设置会随“保存设置”保存在 settings.ini 的 [More] 节。）");

            if (TxtSectionLangSettings != null) TxtSectionLangSettings.Text = L("ui.section.lang_settings", "语言设置");
            if (TxtLblCurrentLang != null) TxtLblCurrentLang.Text = L("ui.lbl.current_lang", "当前语言：");
            if (TxtNoteDataFolder != null) TxtNoteDataFolder.Text = L("ui.note.data_folder", "提示：将要发送的 .txt 放到程序根目录下的 Data 文件夹中。");
        }

        // 1. SaveSettingsToIni
        private void SaveSettingsToIni()
        {
            WriteIni("General", "TopMost", (ChkTopMost?.IsChecked == true).ToString().ToLowerInvariant());
            var targetMode = (RbLockedWindow?.IsChecked == true) ? "Locked" : "Active";
            WriteIni("Target", "TargetMode", targetMode);
            WriteIni("Target", "LockEnabled", ((RbLockedWindow?.IsChecked == true) && !string.IsNullOrEmpty(_lockedWindowTitle)).ToString().ToLowerInvariant());
            WriteIni("Target", "LockedWindowTitle", _lockedWindowTitle ?? "");
            WriteIni("Sending", "Method", (RbSendInput?.IsChecked == true) ? "SendInput" : "Clipboard");
            WriteIni("Sending", "EnterMode", (RbCtrlEnter?.IsChecked == true) ? "CtrlEnter" : "Enter");
            WriteIni("More", "PreKeys", SafeGetText(TbPreKeys, ""));
            WriteIni("More", "Prefix", SafeGetText(TbPrefix, ""));
            WriteIni("More", "PostKeys", SafeGetText(TbPostKeys, ""));
            WriteIni("More", "Suffix", SafeGetText(TbSuffix, ""));
            WriteIni("Timing", "DelayMin", SafeGetText(TbDelayMin, "50"));
            WriteIni("Timing", "DelayMax", SafeGetText(TbDelayMax, "150"));
            WriteIni("Timing", "LinePause", SafeGetText(TbLinePause, "200"));
            WriteIni("Timing", "SimulateTypos", (ChkSimulateTypos?.IsChecked == true).ToString().ToLowerInvariant());
            WriteIni("Behavior", "Order", (CbSendOrder != null && CbSendOrder.SelectedIndex == 1) ? "Random" : "Sequential");
            WriteIni("Behavior", "Loop", (ChkLoop?.IsChecked == true).ToString().ToLowerInvariant());
            WriteIni("Behavior", "LoopInterval", SafeGetText(TbLoopInterval, "1000"));
            WriteIni("Hotkey", "Enabled", (ChkGlobalHotkey?.IsChecked == true).ToString().ToLowerInvariant());
            WriteIni("Hotkey", "Countdown", SafeGetText(TbCountdown, "3"));
            if (CbFiles != null && CbFiles.SelectedItem is FileInfo fi)
            {
                WriteIni("General", "LastFile", fi.Name);
            }
            _settingsDirty = false;
            SafeSetStatus(L("ui.status.saved", "设置已保存"));
        }

        // 2. ReadSettingsFromUI
        private (int delayMin, int delayMax, int linePause, bool simulateTypos, bool randomOrder, bool loop, int loopInterval, bool useSendInput, bool ctrlEnter, bool lockWindow, int countdown,
                 string preKeys, string prefix, string postKeys, string suffix) ReadSettingsFromUI()
        {
            int delayMin = int.TryParse(SafeGetText(TbDelayMin, "50"), out var dm) ? dm : 50;
            int delayMax = int.TryParse(SafeGetText(TbDelayMax, delayMin.ToString()), out var dM) ? dM : delayMin;
            int linePause = int.TryParse(SafeGetText(TbLinePause, "200"), out var lp) ? lp : 200;
            bool simulateTypos = ChkSimulateTypos != null && ChkSimulateTypos.IsChecked == true;
            bool randomOrder = (CbSendOrder != null && CbSendOrder.SelectedIndex == 1);
            bool loop = ChkLoop != null && ChkLoop.IsChecked == true;
            int loopInterval = int.TryParse(SafeGetText(TbLoopInterval, "1000"), out var li) ? li : 1000;
            bool useSendInput = RbSendInput != null && RbSendInput.IsChecked == true;
            bool ctrlEnter = RbCtrlEnter != null && RbCtrlEnter.IsChecked == true;
            bool lockWindow = RbLockedWindow != null && RbLockedWindow.IsChecked == true && _lockedWindowHandle != IntPtr.Zero;
            int countdown = int.TryParse(SafeGetText(TbCountdown, "3"), out var c) ? c : 3;
            string preKeys = SafeGetText(TbPreKeys, "");
            string prefix = SafeGetText(TbPrefix, "");
            string postKeys = SafeGetText(TbPostKeys, "");
            string suffix = SafeGetText(TbSuffix, "");
            return (delayMin, delayMax, linePause, simulateTypos, randomOrder, loop, loopInterval, useSendInput, ctrlEnter, lockWindow, countdown, preKeys, prefix, postKeys, suffix);
        }

        // 3. SendTextAsync (实现发送主循环，遵循暂停信号与取消)
        private async Task SendTextAsync(string text, (int delayMin, int delayMax, int linePause, bool simulateTypos, bool randomOrder, bool loop, int loopInterval, bool useSendInput, bool ctrlEnter, bool lockWindow, int countdown,
                                                     string preKeys, string prefix, string postKeys, string suffix) settings, CancellationToken token)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Prepare lines
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None).ToList();

            // Determine order
            if (settings.randomOrder)
            {
                var rnd = _threadLocalRandom.Value ?? new Random();
                for (int i = lines.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    var tmp = lines[i]; lines[i] = lines[j]; lines[j] = tmp;
                }
            }

            do
            {
                for (int idx = 0; idx < lines.Count; idx++)
                {
                    token.ThrowIfCancellationRequested();

                    string line = lines[idx] ?? "";

                    // optional prekeys
                    if (!string.IsNullOrEmpty(settings.preKeys))
                        SendSpecialKeys(settings.preKeys);

                    // prefix
                    if (!string.IsNullOrEmpty(settings.prefix))
                        await SendStringAsync(settings.prefix, settings.useSendInput, token);

                    // send the line
                    await SendStringAsync(line, settings.useSendInput, token);

                    // post keys / suffix (确保在回车前输出，避免后缀成为下一行的前缀)
                    if (!string.IsNullOrEmpty(settings.postKeys))
                        SendSpecialKeys(settings.postKeys);
                    if (!string.IsNullOrEmpty(settings.suffix))
                        await SendStringAsync(settings.suffix, settings.useSendInput, token);

                    // press Enter / Ctrl+Enter（在后缀发送完成后再回车）
                    if (settings.ctrlEnter)
                    {
                        SendCtrlEnter();
                    }
                    else
                    {
                        SendEnter();
                    }

                    // per-line pause (使用设置中的间隔)
                    await Task.Delay(settings.linePause, token);

                    // 可在此处更新 UI 状态（例如行号），如需请用 Dispatcher.Invoke
                }

                if (settings.loop)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(settings.loopInterval, token);
                }
                else break;

            } while (true);
        }
    }
}