using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexUsage;
public partial class MainWindow : Window
{
    private readonly Storage storage;
    private readonly CodexApi api;
    private readonly List<Account> accounts = new();
    private Account? activeAccount;
    private bool updatingAccountBox;
    private readonly bool demo;
    private readonly string? smokeOutput;
    private readonly Forms.NotifyIcon tray = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? login;
    private Settings settings = new();
    private Usage? demoUsage;
    private Usage? usage => activeAccount?.Usage ?? demoUsage;
    private bool signedIn => activeAccount != null || demoUsage != null;
    private bool busy, expanded, dragging, closing, recovery;
    private Point? dragStart;
    private DateTimeOffset lastAttempt;
    private string? error;
    private string? loginError;
    private string? trayPercent;
    private sealed class Account(Storage store, Session session, Usage? usage, bool pendingReset)
    {
        public Storage Store { get; } = store;
        public CodexApi Api { get; } = new(store);
        public string Id { get; } = session.AccountId;
        public string Label { get; set; } = AccountLabelFor(session);
        public Usage? Usage { get; set; } = usage;
        public bool PendingReset { get; set; } = pendingReset;
        public string? Error { get; set; }
    }
    private static string AccountLabelFor(Session session) => string.IsNullOrWhiteSpace(session.Email)
        ? $"ChatGPT · {session.AccountId}"
        : $"{session.Email} · {session.AccountId[^Math.Min(8, session.AccountId.Length)..]}";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public MainWindow(Storage storage, bool demo, string? smokeOutput)
    {
        this.storage = storage; this.demo = demo; this.smokeOutput = smokeOutput;
        api = new(storage);
        InitializeComponent();
        try { settings = storage.Read<Settings>("settings.json") ?? new(); }
        catch (Exception e) when (IsStorageError(e)) { error = "Could not load saved preferences."; }
        LoadAccount(storage);
        try { foreach (var store in storage.AccountStores()) LoadAccount(store); }
        catch (Exception e) when (IsStorageError(e)) { error = "Could not load the saved account list."; }
        SelectAccount(accounts.Find(account => account.Id == settings.SelectedAccountId) ?? accounts.FirstOrDefault());
        Topmost = settings.Pinned; PinnedCheck.IsChecked = settings.Pinned;
        NotifyCheck.IsChecked = settings.Notify;
        IntervalBox.SelectedIndex = ClampIntervalIndex(settings.RefreshMinutes);
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) StartupCheck.IsChecked = key?.GetValue("CodexUsage") != null;
        ApplyTheme(IsLightTheme());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => { if (IsVisible) Hide(); else ShowPanel(); });
        menu.Items.Add("Refresh now", null, async (_, _) => await Refresh("manual"));
        menu.Items.Add("Move near taskbar", null, (_, _) => { Dock(); Show(); SaveSettings(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { if (IsVisible && expanded) { SetExpanded(false); } else ShowPanel(); } };
        tray.BalloonTipClicked += (_, _) => ShowPanel();
        UpdateTray("—"); tray.Visible = true;
        var context = new ContextMenu();
        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); context.Items.Add(item); }
        Item("Refresh", async () => await Refresh("manual")); Item("Move near taskbar", () => { Dock(); SaveSettings(); }); Item("Hide to tray", Hide); Item("Exit", Close);
        Handle.ContextMenu = context;
        Loaded += OnLoaded;
        timer.Tick += async (_, _) => { ApplyTheme(IsLightTheme()); Render(); if (DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromMinutes(settings.RefreshMinutes)) await Refresh("timer"); };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Closed += (_, _) => {
            closing = true; lifetime.Cancel(); login?.Cancel(); timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
            try { SaveSettings(); } catch (IOException) { /* No credentials or usage are modified during shutdown. */ }
            lifetime.Dispose();
        };
    }
    private static bool IsStorageError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or CryptographicException;
    private void LoadAccount(Storage store)
    {
        Session? session;
        try { session = store.LoadSession(); }
        catch (Exception e) when (IsStorageError(e))
        {
            recovery |= store == storage;
            error = "Could not unlock a saved account. Sign in to it again.";
            return;
        }
        if (session == null || accounts.Exists(account => account.Id == session.AccountId)) return;
        var account = new Account(store, session, null, false);
        accounts.Add(account);
        try
        {
            account.Usage = store.Read<Usage>("usage.json");
            account.PendingReset = store.HasPendingReset(session.AccountId);
        }
        catch (Exception e) when (IsStorageError(e)) { account.Error = "Could not load saved usage. Refresh to try again."; }
    }
    private void SelectAccount(Account? account)
    {
        activeAccount = account;
        settings = settings with { SelectedAccountId = account?.Id };
        updatingAccountBox = true;
        AccountBox.ItemsSource = accounts.Select(item => item.Label).ToArray();
        AccountBox.SelectedIndex = account == null ? -1 : accounts.IndexOf(account);
        updatingAccountBox = false;
    }
    private void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingAccountBox || AccountBox.SelectedIndex < 0) return;
        activeAccount = accounts[AccountBox.SelectedIndex];
        settings = settings with { SelectedAccountId = activeAccount.Id };
        error = null;
        loginError = null;
        TrySaveSettings();
        Render();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wait until WPF finishes creating the native window before resizing it.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (settings.Left is double x && settings.Top is double y && double.IsFinite(x) && double.IsFinite(y)) { Left = x; Top = y; KeepVisible(); } else Dock();
        if (demo && smokeOutput == null) demoUsage = DemoUsage();
        Render();
        if (!signedIn) SetExpanded(true);
        if (smokeOutput != null) { await RunSmoke(); return; }
        timer.Start();
        await Refresh("startup");
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionUnlock) QueueRefresh("unlock"); }
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) QueueRefresh("resume"); }
    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) QueueRefresh("network-restored"); }
    private void OnDisplayChanged(object? sender, EventArgs e) { if (!closing) Dispatcher.BeginInvoke(KeepVisible); }
    private void QueueRefresh(string trigger) { if (!closing) Dispatcher.BeginInvoke(async () => await Refresh(trigger)); }
    private void Dock() { Left = SystemParameters.WorkArea.Right - Width - 18; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12; }
    private void KeepVisible()
    {
        // WPF coordinates are device independent; screen coordinates are physical pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = Forms.Screen.FromPoint(new Drawing.Point((int)(Left * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY)));
        var area = screen.WorkingArea;
        var left = area.Left / dpi.DpiScaleX; var top = area.Top / dpi.DpiScaleY;
        Left = Math.Clamp(Left, left, Math.Max(left, area.Right / dpi.DpiScaleX - ActualWidth));
        Top = Math.Clamp(Top, top, Math.Max(top, area.Bottom / dpi.DpiScaleY - ActualHeight));
    }
    public void ShowPanel() { Show(); SetExpanded(true); Activate(); }
    private void SetExpanded(bool value)
    {
        var oldRight = Left + ActualWidth; var oldBottom = Top + ActualHeight;
        expanded = value; Details.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Width = value ? 340 : 190; Chevron.Text = value ? "⌃" : "⌄";
        Details.MaxHeight = Math.Max(150, Math.Min(650, SystemParameters.WorkArea.Height - 80));
        UpdateLayout();
        Left = oldRight - ActualWidth; Top = oldBottom - ActualHeight;
        KeepVisible();
        if (!value) Hide();
    }
    private void SaveSettings()
    {
        // Persist the compact position even if the details panel is open.
        settings = settings with { Left = Left + ActualWidth - 190, Top = Top + ActualHeight - 36, Pinned = Topmost };
        storage.Save("settings.json", settings);
    }
    private void HandleDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); dragging = false; Handle.Focus(); }
    private void HandleMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || dragging) return;
        var now = e.GetPosition(this);
        if (Math.Abs(start.X - now.X) + Math.Abs(start.Y - now.Y) < 5) return;
        dragging = true; DragMove(); dragStart = null; KeepVisible(); TrySaveSettings();
    }
    private void HandleUp(object sender, MouseButtonEventArgs e) { if (!dragging) SetExpanded(!expanded); dragStart = null; }
    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { SetExpanded(!expanded); e.Handled = true; } }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) SetExpanded(false); }
    private void PinChanged(object sender, RoutedEventArgs e) { Topmost = PinnedCheck.IsChecked == true; TrySaveSettings(); }
    private void NotifyChanged(object sender, RoutedEventArgs e) { settings = settings with { Notify = NotifyCheck.IsChecked == true }; TrySaveSettings(); }
    private void IntervalChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // The constructor preselects the saved interval before the window has coordinates; saving then would persist NaN.
        if (!IsLoaded) return;
        if (IntervalBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var minutes))
        {
            settings = settings with { RefreshMinutes = minutes };
            TrySaveSettings();
        }
    }
    private static int ClampIntervalIndex(int minutes) => minutes switch { <= 1 => 0, <= 3 => 1, <= 5 => 2, <= 10 => 3, <= 15 => 4, <= 30 => 5, _ => 6 };
    private void TrySaveSettings() { try { SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StatusLabel.Text = "Could not save preferences."; } }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (demo) { StartupCheck.IsChecked = false; return; }
        try {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartupCheck.IsChecked == true) key.SetValue("CodexUsage", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("CodexUsage", false);
        } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { StartupCheck.IsChecked = false; StatusLabel.Text = "Windows did not allow changing startup settings."; }
    }
    private void HideClick(object sender, RoutedEventArgs e) { SetExpanded(false); Hide(); }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh("manual");
    private async Task Refresh(string trigger)
    {
        if (busy) { if (!closing) activeAccount?.Store.Log.RecordSkipped(trigger, "busy"); return; }
        if (!signedIn || demo || closing) return;
        busy = true; lastAttempt = DateTimeOffset.UtcNow; Render();
        error = null;
        try
        {
            foreach (var account in accounts)
            {
                if (closing) break;
                try { await FetchAccount(account, trigger); }
                catch (Exception e) { account.Error = FriendlyError(e); }
            }
        }
        finally { busy = false; if (!closing) Render(); }
    }
    private async Task FetchAccount(Account account, string trigger)
    {
        var started = Stopwatch.GetTimestamp();
        var previous = account.Usage;
        Usage? next = null;
        try {
            next = await account.Api.Fetch(lifetime.Token);
            account.Store.Save("usage.json", next);
        } catch (Exception e) {
            account.Store.Log.RecordFetch(trigger, Stopwatch.GetElapsedTime(started), previous, next, e);
            throw;
        }
        var updated = next!;
        var delta = Usage.Change(previous?.Windows.FirstOrDefault()?.Remaining, updated.Windows[0].Remaining);
        account.Usage = updated; account.Error = null;
        account.Store.Log.RecordFetch(trigger, Stopwatch.GetElapsedTime(started), previous, updated, null);
        if (closing || !settings.Notify) return;
        if (delta != null) tray.ShowBalloonTip(5000, "Codex usage changed", $"{account.Label}: {updated.Windows[0].Remaining}% remaining ({delta:+0;-0}%). Resets in {Usage.Countdown(updated.Windows[0].ResetAt, DateTimeOffset.UtcNow)}.", Forms.ToolTipIcon.Info);
        foreach (var reminder in Usage.ResetReminders(previous, updated, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) tray.ShowBalloonTip(5000, "Codex limit reset", $"{account.Label}: {reminder}", Forms.ToolTipIcon.Info);
    }
    private static string FriendlyError(Exception e) => e switch {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Session rejected. Sign out and sign in again.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Too many requests. Try again later.",
        HttpRequestException { StatusCode: not null } h => $"Service returned HTTP {(int)h.StatusCode!}. Try again later.",
        HttpRequestException => "Offline or service unavailable. Last successful reading is shown.",
        OperationCanceledException => "Request cancelled or timed out. Try again.",
        CryptographicException => "Windows could not unlock saved credentials. Sign out and sign in again.",
        IOException or UnauthorizedAccessException => "Could not save local data. Check available disk space and permissions.",
        FormatException or JsonException or InvalidOperationException => "Unexpected service response. Refresh or sign in again.",
        _ => "The operation failed. Please try again."
    };
    private void Render()
    {
        if (closing) return;
        var accountError = activeAccount?.Error;
        var pendingReset = activeAccount?.PendingReset == true;
        var selected = usage?.Windows.ElementAtOrDefault(Math.Clamp(settings.WindowIndex, 0, Math.Max(0, usage.Windows.Count - 1)));
        var stale = usage != null && (accountError != null || DateTimeOffset.UtcNow - usage.FetchedAt > TimeSpan.FromMinutes(16));
        Percent.Text = selected == null ? signedIn ? "—" : "Sign in" : $"{selected.Remaining}%";
        Countdown.Text = selected == null ? "" : (stale ? "! " : "↻ ") + Usage.Countdown(selected.ResetAt, DateTimeOffset.UtcNow);
        var limitTooltip = selected == null ? "Click for details" : $"{selected.Name}: {selected.Remaining}% remaining\n{(stale ? "STALE · " : "")}{accountError ?? "Click for details · drag to move"}";
        Handle.ToolTip = activeAccount == null ? limitTooltip : $"{activeAccount.Label}\n{limitTooltip}";
        UpdateTray(selected == null ? "—" : stale ? "!" : selected.Remaining.ToString());
        tray.Text = selected == null ? "MultiCodexUsage · Sign in" : $"Codex · {selected.Remaining}% left · {Usage.Countdown(selected.ResetAt, DateTimeOffset.UtcNow)}{(stale ? " · stale" : "")}";
        PlanLabel.Text = demo ? "DEMO DATA" : "";
        StatusLabel.Text = loginError ?? error ?? (login != null ? "Waiting for browser authorization…" : busy ? "Refreshing…" : accountError ?? (usage != null ? $"{(stale ? "Stale · " : "")}Updated {usage.FetchedAt.LocalDateTime:g}" : signedIn ? "Refresh to load usage for this account." : "Sign in securely with your ChatGPT account."));
        AccountLabel.Text = accounts.Count > 0 ? $"{accounts.Count} {(accounts.Count == 1 ? "account" : "accounts")}" : demo ? "Preview · no account connected" : "";
        AccountActionLabel.Visibility = accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountBox.Visibility = accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountBox.IsEnabled = !busy;
        CreditsLabel.Text = usage == null ? "" : $"{usage.Resets} banked resets" + (usage.ResetExpiry is long expiry ? $" · next expires {DateTimeOffset.FromUnixTimeSeconds(expiry).LocalDateTime:g}" : "") + (usage.Balance == null ? "" : $" · Credit balance: {usage.Balance}");
        SignInButton.Visibility = demo && signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.Content = signedIn ? "Add account" : "Sign in with ChatGPT";
        SignInButton.IsEnabled = !busy && !demo;
        foreach (var b in new[] { RefreshButton, ResetButton, SignOutButton }) { b.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed; b.IsEnabled = !busy && !demo; }
        SignOutButton.Visibility = signedIn || recovery ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Content = signedIn ? "Sign out selected account" : "Clear saved sign-in";
        ResetButton.IsEnabled = !busy && !demo && (usage?.Resets > 0 || pendingReset);
        ResetButton.Content = pendingReset ? "Retry reset" : "Use reset";
        LimitCards.Children.Clear();
        if (demoUsage != null) AddLimitCards(LimitCards, demoUsage, null);
        foreach (var account in accounts) AddAccountSection(account);
    }
    private void AddAccountSection(Account account)
    {
        var section = new StackPanel();
        var outline = new Border { Child = section, Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        outline.SetResourceReference(Border.BorderBrushProperty, account == activeAccount ? "AccentBrush" : "LineBrush");
        section.Children.Add(new TextBlock { Text = account.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var reading = account.Usage;
        var summary = reading == null ? "Usage not available yet" : $"{reading.Plan.ToUpperInvariant()} · Updated {reading.FetchedAt.LocalDateTime:g}";
        if (reading != null && (account.Error != null || DateTimeOffset.UtcNow - reading.FetchedAt > TimeSpan.FromMinutes(16))) summary += " · Stale";
        var status = new TextBlock { Text = account.Error == null ? summary : $"{summary}\n{account.Error}", Margin = new Thickness(0, 3, 0, 8), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        section.Children.Add(status);
        if (reading != null) AddLimitCards(section, reading, account);
        LimitCards.Children.Add(outline);
    }
    private void AddLimitCards(StackPanel container, Usage reading, Account? account)
    {
        for (int i = 0; i < reading.Windows.Count; i++)
        {
            var index = i; var w = reading.Windows[i];
            var panel = new StackPanel();
            var row = new DockPanel();
            var amount = new TextBlock { Text = $"{w.Remaining}%", FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            DockPanel.SetDock(amount, System.Windows.Controls.Dock.Right); row.Children.Add(amount);
            row.Children.Add(new TextBlock { Text = w.Name, TextTrimming = TextTrimming.CharacterEllipsis }); panel.Children.Add(row);
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = w.Remaining, Height = 4, Margin = new Thickness(0, 9, 0, 7), Foreground = Brush(w.Remaining <= 10 ? "#FF947F" : "#56BFA0"), Background = (Brush)Resources["LineBrush"], BorderThickness = new Thickness(0) });
            panel.Children.Add(new TextBlock { Text = w.ResetAt is long at ? $"Resets {DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime:g} · {Usage.Countdown(at, DateTimeOffset.UtcNow)}" : "Reset time unavailable", FontSize = 11, Foreground = (Brush)Resources["MutedBrush"], TextWrapping = TextWrapping.Wrap });
            var button = new System.Windows.Controls.Button { Content = panel, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10), ToolTip = "Show this limit in the window", BorderThickness = new Thickness(0) };
            button.IsEnabled = !busy || account == activeAccount;
            button.Click += (_, _) =>
            {
                if (account != null && account != activeAccount)
                {
                    if (busy) return;
                    SelectAccount(account);
                    error = null;
                    loginError = null;
                }
                settings = settings with { WindowIndex = index };
                TrySaveSettings();
                Render();
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{w.Name}, {w.Remaining}% remaining. Select for compact display");
            container.Children.Add(button);
        }
    }
    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (busy || demo) return;
        busy = true; error = null; loginError = null; login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Render();
        var step = "Requesting a device code";
        try {
            var code = await api.StartLogin(login.Token);
            step = "Completing browser authorization";
            DeviceCodeText.Text = code.Code; LoginPanel.Visibility = Visibility.Visible;
            StatusLabel.Text = "Waiting for browser authorization…";
            OpenBrowser();
            var session = await api.FinishLogin(code, login.Token);
            step = "Saving the authorized account";
            var account = accounts.Find(item => item.Id == session.AccountId);
            var store = account?.Store ?? storage.ForAccount(session.AccountId);
            store.SaveSession(session);
            if (account == null)
            {
                account = new Account(store, session, null, false);
                accounts.Add(account);
            }
            account.Label = AccountLabelFor(session);
            SelectAccount(account);
            TrySaveSettings();
            try { await FetchAccount(account, "sign-in"); }
            catch (Exception ex) { account.Error = "Account saved. Could not read its usage. " + FriendlyError(ex); }
        }
        catch (OperationCanceledException) when (login.IsCancellationRequested) { }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode status)
        {
            var detail = status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when LoginPanel.Visibility == Visibility.Visible => "Check device code authorization in the target account’s ChatGPT security settings, then try again.",
                HttpStatusCode.TooManyRequests => "Wait before requesting another code.",
                _ => "Try again in a moment."
            };
            loginError = $"{step} failed (HTTP {(int)status}). {detail}";
        }
        catch (Exception ex) { loginError = $"{step} failed. {FriendlyError(ex)}"; }
        finally { login.Dispose(); login = null; busy = false; LoginPanel.Visibility = Visibility.Collapsed; DeviceCodeText.Clear(); if (!closing) Render(); }
    }
    private void OpenBrowser() { try { Process.Start(new ProcessStartInfo("https://auth.openai.com/codex/device") { UseShellExecute = true }); } catch { StatusLabel.Text = "Open https://auth.openai.com/codex/device in your browser."; } }
    private void OpenLoginBrowser(object sender, RoutedEventArgs e) => OpenBrowser();
    private void CopyCode(object sender, RoutedEventArgs e) { try { System.Windows.Clipboard.SetText(DeviceCodeText.Text); } catch (COMException) { StatusLabel.Text = "Clipboard is busy. Select and copy the code manually."; } }
    private void CancelLogin(object sender, RoutedEventArgs e) => login?.Cancel();
    private void SignOut(object sender, RoutedEventArgs e)
    {
        if (busy || demo) return;
        try
        {
            var account = activeAccount;
            (account?.Store ?? storage).ClearSession();
            if (account == null || account.Store == storage) recovery = false;
            if (account != null) accounts.Remove(account);
            SelectAccount(accounts.FirstOrDefault());
            error = null;
            loginError = null;
            TrySaveSettings();
        }
        catch (Exception ex) { error = FriendlyError(ex); }
        Render();
    }
    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        if (busy || activeAccount is not Account account || demo) return;
        busy = true; Render();
        try {
            error = null;
            await FetchAccount(account, "reset-precheck"); Render();
            if (account.Usage!.Resets < 1 && !account.PendingReset) { error = "No banked resets are available."; return; }
            var message = account.PendingReset ? "Retry the pending reset with the same request ID? If it was already redeemed, no additional credit will be spent." : "Use one banked reset? This spends one reset credit and resets the eligible usage windows.";
            if (System.Windows.MessageBox.Show(this, $"{account.Label}\n\n{message}", "Confirm usage reset", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            account.PendingReset = true;
            var result = await account.Api.ConsumeReset(lifetime.Token);
            account.PendingReset = false;
            error = null;
            System.Windows.MessageBox.Show(this, result switch { "reset" => "Usage reset completed.", "already_redeemed" => "This request was already redeemed. No additional credit was spent.", "no_credit" => "No reset credit is available.", _ => "No usage window needs a reset." }, Title);
            await FetchAccount(account, "reset-postcheck");
        } catch (Exception ex) { error = FriendlyError(ex) + " If a reset was sent, retry Use reset safely with the saved request ID."; }
        finally { busy = false; if (!closing) Render(); }
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static bool IsLightTheme() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int n && n == 1; }
    private void ApplyTheme(bool value)
    {
        Resources["SurfaceBrush"] = Brush(value ? "#F5F7FA" : "#191C21"); Resources["TextBrush"] = Brush(value ? "#17212E" : "#F2F5FA");
        Resources["MutedBrush"] = Brush(value ? "#586574" : "#A7B0BD"); Resources["LineBrush"] = Brush(value ? "#CAD2DC" : "#3A404B");
        Resources["CardBrush"] = Brush(value ? "#E9EEF4" : "#242830"); Resources["AccentBrush"] = Brush(value ? "#147D62" : "#7EE3BA");
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private void UpdateTray(string text)
    {
        if (trayPercent == text) return;
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap)) {
            g.Clear(Drawing.Color.FromArgb(20, 30, 28)); g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var family = new Drawing.FontFamily("Segoe UI");
            using var glyphs = new Drawing.Drawing2D.GraphicsPath();
            glyphs.AddString(text, family, (int)Drawing.FontStyle.Bold, 32, Drawing.PointF.Empty, Drawing.StringFormat.GenericTypographic);
            var bounds = glyphs.GetBounds();
            var height = text == "—" ? 5f : 28f;
            var width = Math.Min(30f, bounds.Width * height / bounds.Height);
            using var transform = new Drawing.Drawing2D.Matrix(width / bounds.Width, 0, 0, height / bounds.Height,
                (32 - width) / 2 - bounds.X * width / bounds.Width, (32 - height) / 2 - bounds.Y * height / bounds.Height);
            glyphs.Transform(transform);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(226, 255, 245));
            g.FillPath(brush, glyphs);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); var previous = tray.Icon; tray.Icon = (Drawing.Icon)borrowed.Clone(); previous?.Dispose(); }
        finally { DestroyIcon(handle); }
        trayPercent = text;
    }
    private static Usage DemoUsage()
    {
        var now = DateTimeOffset.UtcNow;
        return new("pro", new() { new("Codex · Weekly", 86, now.AddDays(5).AddHours(11).ToUnixTimeSeconds(), 604800), new("Spark · Daily", 74, now.AddHours(8).AddMinutes(24).ToUnixTimeSeconds(), 86400), new("Spark · Weekly", 62, now.AddDays(3).ToUnixTimeSeconds(), 604800) }, 2, "0", now);
    }
    private async Task RunSmoke()
    {
        try {
            Directory.CreateDirectory(smokeOutput!);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Math.Abs(ActualWidth - 340) > 1 || Details.Visibility != Visibility.Visible) throw new Exception($"Initial sign-in panel is clipped: {ActualWidth}");
            demoUsage = DemoUsage();
            UpdateTray("84");
            using (var iconBitmap = tray.Icon!.ToBitmap()) {
                var top = iconBitmap.Height; var bottom = -1;
                for (var y = 0; y < iconBitmap.Height; y++) for (var x = 0; x < iconBitmap.Width; x++) {
                    var pixel = iconBitmap.GetPixel(x, y);
                    if (pixel.R > 150 && pixel.G > 150 && pixel.B > 150) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                }
                if (bottom - top + 1 < 26) throw new Exception($"Tray digits too small: {bottom - top + 1}px");
                iconBitmap.Save(Path.Combine(smokeOutput!, "tray-84.png"));
            }
            ApplyTheme(false); SetExpanded(false); Render(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Percent.Text != "86%" || Math.Abs(Width - 190) > 1 || Details.Visibility != Visibility.Collapsed || !tray.Visible)
                throw new Exception($"Compact view / tray: percentage={Percent.Text}, width={Width}, details={Details.Visibility}, tray={tray.Visible}");
            Capture("compact.png");
            SetExpanded(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (LimitCards.Children.Count != 3 || Math.Abs(Width - 340) > 1 || Details.Visibility != Visibility.Visible) throw new Exception("Details");
            Capture("expanded.png");
            if (FindName("FloatingCheck") is not null) throw new Exception("Floating window switch should be removed");
            ShowPanel(); SetExpanded(false); if (IsVisible) throw new Exception("Collapsing details must return to tray-only mode");
            ShowPanel(); if (!IsVisible || !expanded) throw new Exception("Tray click should restore the details panel");
            SetExpanded(true);
            ((System.Windows.Controls.Button)LimitCards.Children[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Percent.Text != "74%") throw new Exception("Limit selection");
            if (FindName("IntervalBox") is not System.Windows.Controls.ComboBox interval) throw new Exception("Missing auto-refresh interval selector");
            if (interval.SelectedIndex != 2) throw new Exception($"Default interval should be 5 min, got index {interval.SelectedIndex}");
            interval.SelectedIndex = 0;
            if (storage.Read<Settings>("settings.json")?.RefreshMinutes != 1) throw new Exception("1-minute interval was not saved");
            interval.SelectedIndex = 1;
            if (storage.Read<Settings>("settings.json")?.RefreshMinutes != 3) throw new Exception("3-minute interval was not saved");
            interval.SelectedIndex = 2;
            if (storage.Read<Settings>("settings.json")?.RefreshMinutes != 5) throw new Exception("Interval preference was not restored");
            ApplyTheme(true); Render(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("light.png");
            demoUsage = demoUsage! with { FetchedAt = DateTimeOffset.UtcNow.AddHours(-1) }; Render();
            if (!Countdown.Text.StartsWith("! ")) throw new Exception("Stale indicator");
            PinnedCheck.IsChecked = false; PinChanged(this, new()); if (Topmost) throw new Exception("Unpin");
            HideClick(this, new()); if (IsVisible) throw new Exception("Hide"); ShowPanel(); if (!IsVisible) throw new Exception("Restore");
            SetExpanded(false); SaveSettings();
            var saved = storage.Read<Settings>("settings.json"); if (saved?.WindowIndex != 1 || saved.Pinned) throw new Exception("Preferences persistence");
            demoUsage = null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var first = new Session("", "", "", "account-a", "first@example.invalid", null);
            var second = first with { AccountId = "account-b", Email = "second@example.invalid" };
            accounts.Add(new(storage.ForAccount(first.AccountId), first, new Usage("pro", new() { new("Primary", 86, now + 3600, 18000) }, 0, null, DateTimeOffset.UtcNow), false));
            accounts.Add(new(storage.ForAccount(second.AccountId), second, new Usage("plus", new() { new("Primary", 32, now + 86400, 18000) }, 0, null, DateTimeOffset.UtcNow), false));
            var third = first with { AccountId = "account-c", Email = "third@example.invalid" };
            accounts.Add(new(storage.ForAccount(third.AccountId), third, null, false));
            ShowPanel(); SelectAccount(accounts[0]); Render();
            if (LimitCards.Children.Count != 3) throw new Exception("Every account must have its own usage section");
            var sections = LimitCards.Children.Cast<Border>().Select(card => (StackPanel)card.Child).ToArray();
            string LimitText(StackPanel section) => System.Windows.Automation.AutomationProperties.GetName(section.Children[2]);
            if (!LimitText(sections[0]).Contains("86%") || !LimitText(sections[1]).Contains("32%") || ((TextBlock)sections[2].Children[1]).Text != "Usage not available yet")
                throw new Exception("Account sections must simultaneously show independent usage or an empty state");
            var firstCountdown = Countdown.Text;
            ((System.Windows.Controls.Button)sections[1].Children[2]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Percent.Text != "32%" || Countdown.Text == firstCountdown || !((string)AccountBox.SelectedItem).Contains(second.Email!) || storage.Read<Settings>("settings.json")?.SelectedAccountId != second.AccountId) throw new Exception("Switching accounts must update usage and reset time");
            Capture("accounts.png");
            File.WriteAllText(Path.Combine(smokeOutput!, "smoke-result.txt"), "PASS: compact and expanded views, limit selection, refresh interval, light/dark render, stale indicator, pin, hide/restore, settings persistence, tray, simultaneous account sections and account selection with independent reset times.\n");
            Close();
        } catch (Exception e) { File.WriteAllText(Path.Combine(smokeOutput!, "smoke-result.txt"), e.ToString()); System.Windows.Application.Current.Shutdown(1); }
    }
    private void Capture(string name)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(smokeOutput!, name)); encoder.Save(stream);
    }
}
