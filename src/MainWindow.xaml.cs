using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MinecraftSessionTool;

public partial class MainWindow : Window
{
    private static readonly string CurrentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.1";
    private string? updateUrl;
    private bool isUpdatePreview;
    private int versionEggClicks;
    private long versionEggLastClick;
    private string? latestAvailableVersion;
    private AppRelease? availableRelease;
    private CancellationTokenSource? updateCancellation;
    private bool updateInProgress;
    private bool updaterRestarting;
    private bool closeAfterUpdateCancel;
    internal string? StartupUpdateMessage { get; set; }

    private List<SavedAccount> accounts = new();
    private SavedAccount? currentAccount;
    private SessionResult? currentSession;

    private List<SavedProxy> proxies = new();
    private readonly HashSet<string> checkingProxyIds = new();
    private readonly SessionActivityLog activityLog;
    private CancellationTokenSource? proxyTestCts;
    private bool isTestingProxies;

    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool importingFiles;
    private bool closeAfterCancellation;
    private TaskCompletionSource<bool>? pendingConfirmation;

    public MainWindow(string? initialFile = null) : this(initialFile, null) { }

    internal MainWindow(string? initialFile, SessionActivityLog? injectedLog)
    {
        activityLog = injectedLog ?? new SessionActivityLog();
        InitializeComponent();
        TitleVersionText.Text = "v" + CurrentVersion;
        AuthorVersionText.Text = $"v{CurrentVersion}  ·  AGPL-3.0";
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(320, workArea.Width - 24));
        MinHeight = Math.Min(MinHeight, Math.Max(320, workArea.Height - 24));
        Width = Math.Min(Width, workArea.Width - 24);
        Height = Math.Min(Height, workArea.Height - 24);
        foreach (var entry in activityLog.Entries) AppendLogEntry(entry);
        AddLog(ActivityEvent.AppStarted);

        bool initialLoaded = false;
        Loaded += async (_, _) =>
        {
            _ = CheckForUpdatesAsync();
            if (StartupUpdateMessage != null) ShowNotice(StartupUpdateMessage);
            ReloadAccountsList();
            ReloadProxyList();

            if (initialLoaded) return;
            initialLoaded = true;

            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            {
                await ImportFileAsync(initialFile);
            }
            else
            {
                var adjacent = Path.Combine(AppContext.BaseDirectory, "cookies.txt");
                if (File.Exists(adjacent) && accounts.Count == 0)
                {
                    await ImportFileAsync(adjacent);
                }
            }
        };
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    #region Update Checker
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var release = await AppUpdater.CheckAsync(CurrentVersion, timeout.Token);
            if (release == null) return;
            availableRelease = release;
            latestAvailableVersion = release.Tag;
            updateUrl = release.PageUrl;
            if (!isUpdatePreview)
            {
                UpdateBadgeText.Text = $"Cập nhật v{release.Tag}";
                UpdateBadge.Visibility = Visibility.Visible;
                AddLog(ActivityEvent.UpdateAvailable);
            }
        }
        catch (Exception ex) { LogError(ActivityEvent.UpdateFailed, ex); }
    }

    private void UpdateBadge_Click(object sender, RoutedEventArgs e)
    {
        if (!isUpdatePreview && availableRelease == null) return;
        var sample = NextPreviewVersion();
        UpdateVersionText.Text = $"v{CurrentVersion}  →  v{(isUpdatePreview ? sample : availableRelease!.Tag)}";
        UpdatePreviewLabel.Visibility = isUpdatePreview ? Visibility.Visible : Visibility.Collapsed;
        UpdateDescription.Text = isUpdatePreview
            ? "Đây là bản xem thử. Bạn có thể chạy mô phỏng để xem tiến trình; app không tải hay thay file thật."
            : "App sẽ tải bản mới, tự thay phiên bản hiện tại và mở lại. Cookie và proxy đã lưu của bạn được giữ nguyên.";
        UpdateNotesText.Text = isUpdatePreview
            ? "• Giao diện thông báo mới, rõ ràng hơn\n• Tải và cài bản mới ngay trong ứng dụng\n• Đây là nội dung minh họa, không phải bản phát hành thật."
            : string.IsNullOrWhiteSpace(availableRelease!.Notes) ? "Tác giả chưa thêm ghi chú cho bản phát hành này." : availableRelease.Notes;
        UpdateDownloadBar.Visibility = Visibility.Collapsed;
        UpdateDownloadBar.Value = 0;
        UpdateDownloadText.Text = isUpdatePreview ? "Xem trước toàn bộ thao tác mà không khởi động lại app."
            : availableRelease!.Asset is { } asset ? $"Dung lượng tải: {asset.Size / 1048576d:0.#} MB. App mở lại khi cập nhật xong."
            : "Bản này chưa có gói cập nhật tự động phù hợp. Bạn có thể mở trang GitHub để tải thủ công.";
        InstallUpdateButton.Content = isUpdatePreview ? "Chạy thử cập nhật" : availableRelease!.Asset == null ? "Mở trang tải về" : "Cập nhật và mở lại";
        InstallUpdateButton.IsEnabled = true;
        UpdateLaterButton.Content = "Để sau";
        UpdateLaterButton.IsEnabled = true;
        UpdateOverlay.Visibility = Visibility.Visible;
        InstallUpdateButton.Focus();
    }

    private static string NextPreviewVersion()
    {
        var current = Version.Parse(CurrentVersion);
        return new Version(current.Major, current.Minor, Math.Max(0, current.Build) + 1).ToString();
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        if (updateInProgress) { updateCancellation?.Cancel(); return; }
        UpdateOverlay.Visibility = Visibility.Collapsed;
        UpdateBadge.Focus();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (updateInProgress) return;
        if (busy || importingFiles || isTestingProxies || checkingProxyIds.Count != 0)
        {
            UpdateDownloadText.Text = "App đang xử lý cookie hoặc proxy. Bạn chờ thao tác đó xong rồi cập nhật nhé.";
            return;
        }
        if (!isUpdatePreview && availableRelease?.Asset == null)
        {
            try { Process.Start(new ProcessStartInfo(availableRelease?.PageUrl ?? AppUpdater.ReleasesUrl) { UseShellExecute = true }); }
            catch { UpdateDownloadText.Text = "Chưa mở được trình duyệt. Bạn có thể ghé GitHub của dự án để tải bản mới."; }
            return;
        }
        var preview = isUpdatePreview;
        var release = availableRelease;
        updateInProgress = true;
        using var source = new CancellationTokenSource();
        updateCancellation = source;
        InstallUpdateButton.IsEnabled = false;
        UpdateLaterButton.Content = "Dừng tải";
        UpdateDownloadBar.Visibility = Visibility.Visible;
        UpdateDownloadBar.Value = 0;
        string? workDirectory = null;
        try
        {
            if (preview)
            {
                for (int step = 0; step <= 100; step += 4)
                {
                    source.Token.ThrowIfCancellationRequested();
                    UpdateDownloadBar.Value = step;
                    UpdateDownloadText.Text = step < 80 ? $"Đang mô phỏng tải bản mới… {step}%" : "Đang mô phỏng kiểm tra và chuẩn bị mở lại…";
                    await Task.Delay(70, source.Token);
                }
                UpdateDownloadText.Text = "Hoàn tất xem thử ♡ Với bản cập nhật thật, app sẽ tự mở lại ở bước này. Không có file nào được thay đổi.";
                InstallUpdateButton.Content = "Chạy thử lại";
                return;
            }
            AddLog(ActivityEvent.UpdateDownloadStarted);
            workDirectory = AppUpdater.CreateWorkDirectory();
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (!updateInProgress || source.IsCancellationRequested) return;
                UpdateDownloadBar.Value = value.Percent;
                UpdateDownloadText.Text = $"Đang tải… {value.Percent:0}%  ·  {value.Received / 1048576d:0.#} / {value.Total / 1048576d:0.#} MB";
            });
            var downloaded = await AppUpdater.DownloadAsync(release!, workDirectory, progress, source.Token);
            source.Token.ThrowIfCancellationRequested();
            AddLog(ActivityEvent.UpdateDownloadFinished, ActivityLevel.Success);
            UpdateDownloadText.Text = "File đã được kiểm tra. Đang chuẩn bị mở lại ứng dụng…";
            UpdateLaterButton.IsEnabled = false;
            await AppUpdater.StartInstallerAsync(workDirectory, downloaded, release!, source.Token);
            updaterRestarting = true;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AddLog(ActivityEvent.UpdateCanceled, ActivityLevel.Warning, ActivityFailure.Canceled);
            UpdateDownloadText.Text = "Đã dừng tải. Bản hiện tại vẫn giữ nguyên, bạn có thể thử lại bất cứ lúc nào.";
        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.UpdateFailed, ex);
            UpdateDownloadText.Text = ex is HttpRequestException ? "Chưa tải được bản cập nhật. Bạn kiểm tra mạng rồi thử lại nhé."
                : ex is UnauthorizedAccessException ? "App chưa có quyền ghi vào thư mục hiện tại. Bạn có thể chuyển app sang thư mục của mình rồi thử lại."
                : ex is InvalidDataException ? "Bản tải về chưa vượt qua kiểm tra. Chưa thay file ứng dụng; bạn có thể thử lại hoặc tải từ GitHub."
                : "Chưa cài được bản cập nhật. Bản hiện tại vẫn được giữ nguyên; bạn có thể xem mã lỗi trong Nhật ký.";
        }
        finally
        {
            updateInProgress = false;
            updateCancellation = null;
            InstallUpdateButton.IsEnabled = true;
            UpdateLaterButton.IsEnabled = true;
            UpdateLaterButton.Content = "Đóng";
            if (!preview && !updaterRestarting && workDirectory != null) _ = AppUpdater.CleanupAsync(workDirectory);
            if (closeAfterUpdateCancel && !updaterRestarting) Close();
        }
    }
    #endregion
    #region Navigation (Categories)
    private void NavCookie_Click(object sender, MouseButtonEventArgs e) => SwitchCategory(isCookie: true);

    private void NavProxy_Click(object sender, MouseButtonEventArgs e) => SwitchCategory(isCookie: false);

    private void NavLogs_Click(object sender, MouseButtonEventArgs e) => SwitchCategory(2);

    private void SwitchCategory(bool isCookie) => SwitchCategory(isCookie ? 0 : 1);

    private void SwitchCategory(int category)
    {
        CookieView.Visibility = category == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProxyView.Visibility = category == 1 ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = category == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetNavState(NavCookieBtn, category == 0);
        SetNavState(NavProxyBtn, category == 1);
        SetNavState(NavLogsBtn, category == 2);
        if (category == 1) ReloadProxyList();
        if (category == 2) RefreshLogMetadata();
    }

    private void SetNavState(Border border, bool selected)
    {
        border.Background = Brush(selected ? "#EDE2F6" : "Transparent");
        border.BorderBrush = Brush(selected ? "#C9B3E4" : "Transparent");
        if (border.Child is Grid grid)
        {
            var label = grid.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1);
            if (label != null)
            {
                label.Foreground = Brush(selected ? "#5E3D8E" : "#7D6B92");
                label.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }
    }
    #endregion

    #region Account Storage & List Rendering
    private void ReloadAccountsList(string? selectId = null)
    {
        accounts = CookieStore.LoadAll();
        AccountsCountText.Text = accounts.Count.ToString();
        CategoryCookieCount.Text = accounts.Count.ToString();
        EmptyAccountsPlaceholder.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountsListContainer.Children.Clear();

        SavedAccount? targetSelect = null;

        foreach (var acc in accounts)
        {
            if (selectId != null && acc.Id == selectId) targetSelect = acc;

            var card = CreateAccountCard(acc);
            AccountsListContainer.Children.Add(card);
        }

        if (targetSelect != null)
        {
            SelectAccount(targetSelect);
        }
        else if (currentAccount != null && accounts.Any(a => a.Id == currentAccount.Id))
        {
            var match = accounts.First(a => a.Id == currentAccount.Id);
            SelectAccount(match);
        }
        else if (accounts.Count > 0)
        {
            SelectAccount(accounts[0]);
        }
        else
        {
            SelectAccount(null);
        }
    }

    private Border CreateAccountCard(SavedAccount acc)
    {
        bool isSelected = currentAccount != null && currentAccount.Id == acc.Id;

        var card = new Border
        {
            Background = Brush(isSelected ? "#F4ECFA" : "#FFFFFF"),
            BorderBrush = Brush(isSelected ? "#B697D6" : "#EBE3F2"),
            BorderThickness = new Thickness(isSelected ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Tag = acc
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Avatar Skin Container
        var avatarBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = Brush(isSelected ? "#EDE2F7" : "#F3EDF9"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            ClipToBounds = true
        };

        var avatarGrid = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, 36, 36), 8, 8) };
        var fallbackIcon = new TextBlock
        {
            Text = "👤",
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#8C72B2")
        };
        avatarGrid.Children.Add(fallbackIcon);

        string skinId = !string.IsNullOrWhiteSpace(acc.Uuid) ? acc.Uuid : acc.Username;
        if (!string.IsNullOrWhiteSpace(skinId))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri($"https://mc-heads.net/avatar/{Uri.EscapeDataString(skinId)}/32");
                bi.DecodePixelWidth = 32;
                bi.DecodePixelHeight = 32;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();

                var avatarImg = new Image
                {
                    Width = 36,
                    Height = 36,
                    Stretch = Stretch.UniformToFill,
                    Source = bi
                };
                RenderOptions.SetBitmapScalingMode(avatarImg, BitmapScalingMode.NearestNeighbor);

                bi.DownloadFailed += (_, _) =>
                {
                    avatarImg.Visibility = Visibility.Collapsed;
                };

                avatarGrid.Children.Add(avatarImg);
            }
            catch { }
        }

        avatarBorder.Child = avatarGrid;
        Grid.SetColumn(avatarBorder, 0);
        grid.Children.Add(avatarBorder);

        // Account Details Info
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        // Row 1: IGN + Status badge
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(acc.Username) ? "(Chưa có tên)" : acc.Username,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#332A42"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameText, 0);
        row1.Children.Add(nameText);

        var statusBadge = new Border
        {
            Background = Brush(acc.StatusBgColor),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var statusText = new TextBlock
        {
            Text = acc.Status,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(acc.StatusColor)
        };
        statusBadge.Child = statusText;
        Grid.SetColumn(statusBadge, 1);

        info.Children.Add(row1);
        statusBadge.HorizontalAlignment = HorizontalAlignment.Left;
        statusBadge.Margin = new Thickness(0, 4, 0, 0);
        info.Children.Add(statusBadge);

        // Row 3: Nguồn + Thời gian làm mới
        var metaText = new TextBlock
        {
            Text = string.IsNullOrEmpty(acc.SourceName) ? "Trực tiếp" : acc.SourceName,
            ToolTip = $"Nguồn: {acc.SourceName}\nCập nhật: {acc.LastRefreshedText}",
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 10,
            Foreground = Brush("#A495B5"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        info.Children.Add(metaText);

        if (!string.IsNullOrEmpty(acc.LastProxyUsed))
        {
            var proxyPill = new TextBlock
            {
                Text = $"🌐 {ProxyStore.GetDisplayAddress(acc.LastProxyUsed)}",
                ToolTip = $"Proxy đã dùng: {ProxyStore.GetDisplayAddress(acc.LastProxyUsed)}",
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 9.5,
                Foreground = Brush("#7C5CB7"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            info.Children.Add(proxyPill);
        }

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        card.Child = grid;

        card.MouseLeftButtonDown += (_, _) => SelectAccount(acc);

        card.MouseEnter += (_, _) =>
        {
            if (currentAccount?.Id != acc.Id) card.Background = Brush("#FAF6FD");
        };
        card.MouseLeave += (_, _) =>
        {
            if (currentAccount?.Id != acc.Id) card.Background = Brush("#FFFFFF");
        };

        return card;
    }

    private void SelectAccount(SavedAccount? acc)
    {
        currentAccount = acc;
        var cookieInfo = CookieExpiryInfo.Read(acc?.RawCookies, DateTimeOffset.UtcNow);
        CookieSummaryText.Text = cookieInfo.Summary;
        CookieExpiryText.Text = cookieInfo.ExpiryText(DateTimeOffset.UtcNow);

        // Cập nhật giao diện card list được chọn
        foreach (var child in AccountsListContainer.Children)
        {
            if (child is Border b && b.Tag is SavedAccount item)
            {
                bool sel = acc != null && item.Id == acc.Id;
                b.Background = Brush(sel ? "#F4ECFA" : "#FFFFFF");
                b.BorderBrush = Brush(sel ? "#B697D6" : "#EBE3F2");
                b.BorderThickness = new Thickness(sel ? 1.5 : 1);
            }
        }

        if (acc == null)
        {
            currentSession = null;
            ActiveInitialText.Text = "✿";
            ActiveInitialText.Visibility = Visibility.Visible;
            ActiveAvatarImage.Visibility = Visibility.Collapsed;
            ActiveAvatarImage.Source = null;
            ActiveAccountTitle.Text = "Chưa chọn tài khoản";
            ActiveAccountSubtitle.Text = "Chọn tài khoản bên trái hoặc nhập cookie mới";
            UsernameField.Text = "Chưa có";
            UuidField.Text = "Chưa có";
            TokenPassword.Password = "";
            TokenField.Clear();
            TokenPlaceholder.Visibility = Visibility.Visible;
            TokenPassword.Visibility = TokenField.Visibility = Visibility.Collapsed;
            RevealToken.IsChecked = false;
            RevealToken.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            CopyTokenButton.IsEnabled = false;
            ExportCookieButton.IsEnabled = false;
            return;
        }

        ActiveAccountTitle.Text = acc.Username;
        string proxyUsedStr = !string.IsNullOrEmpty(acc.LastProxyUsed) ? $"  ·  Proxy: {ProxyStore.GetDisplayAddress(acc.LastProxyUsed)}" : "";
        ActiveAccountSubtitle.Text = $"Cập nhật lúc: {acc.LastRefreshedText}  ·  Nguồn: {(string.IsNullOrEmpty(acc.SourceName) ? "Trực tiếp" : acc.SourceName)}{proxyUsedStr}";
        UsernameField.Text = acc.Username;
        UuidField.Text = acc.FormattedUuid;

        // Cập nhật Avatar skin 64px trong chi tiết tài khoản
        string skinIdentifier = !string.IsNullOrWhiteSpace(acc.Uuid) ? acc.Uuid : acc.Username;
        if (!string.IsNullOrWhiteSpace(skinIdentifier))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri($"https://mc-heads.net/avatar/{Uri.EscapeDataString(skinIdentifier)}/64");
                bi.DecodePixelWidth = 64;
                bi.DecodePixelHeight = 64;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();

                bi.DownloadCompleted += (_, _) =>
                {
                    ActiveAvatarImage.Visibility = Visibility.Visible;
                    ActiveInitialText.Visibility = Visibility.Collapsed;
                };
                bi.DownloadFailed += (_, _) =>
                {
                    ActiveAvatarImage.Visibility = Visibility.Collapsed;
                    ActiveInitialText.Visibility = Visibility.Visible;
                    ActiveInitialText.Text = acc.InitialLetter;
                };

                ActiveAvatarImage.Source = bi;
                ActiveAvatarImage.Visibility = Visibility.Visible;
                ActiveInitialText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ActiveAvatarImage.Visibility = Visibility.Collapsed;
                ActiveInitialText.Visibility = Visibility.Visible;
                ActiveInitialText.Text = acc.InitialLetter;
            }
        }
        else
        {
            ActiveAvatarImage.Visibility = Visibility.Collapsed;
            ActiveInitialText.Visibility = Visibility.Visible;
            ActiveInitialText.Text = acc.InitialLetter;
        }

        string token = acc.AccessToken ?? "";
        TokenPassword.Password = token;
        TokenField.Text = token;

        bool hasToken = !string.IsNullOrEmpty(token);
        TokenPlaceholder.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
        TokenPassword.Visibility = hasToken && RevealToken.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        TokenField.Visibility = hasToken && RevealToken.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        RevealToken.IsEnabled = hasToken;
        CopyTokenButton.IsEnabled = hasToken;
        ExportCookieButton.IsEnabled = !string.IsNullOrEmpty(acc.RawCookies);
        RefreshButton.IsEnabled = !busy && !string.IsNullOrEmpty(acc.RawCookies);
        DeleteButton.IsEnabled = !busy;

        string formattedUuidClean = (acc.Uuid ?? "").Replace("-", "");
        currentSession = hasToken
            ? new SessionResult(acc.Username, formattedUuidClean, token)
            : null;
    }
    #endregion

    #region Cookie Processing (Import / Refresh)
    private async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (busy || importingFiles) return;
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        importingFiles = true;
        ImportResultsList.Children.Clear();
        ImportResultsPanel.Visibility = Visibility.Visible;
        int passed = 0, failed = 0;
        try
        {
            for (int index = 0; index < files.Length; index++)
            {
                if (closeAfterCancellation) break;
                ShowNotice($"Đang nhập file {index + 1}/{files.Length}…");
                bool success = await ImportFileAsync(files[index]);
                if (success) passed++; else failed++;
                AddImportResult(Path.GetFileName(files[index]), success, success ? "Đã thêm tài khoản" : NoticeText.Text);
            }
            if (!closeAfterCancellation)
                ShowNotice($"Đã nhập {passed}/{files.Length} file" + (failed > 0 ? $" · {failed} file chưa thành công. Xem Nhật ký để biết thêm." : "."), error: failed > 0, success: failed == 0);
        }
        finally
        {
            importingFiles = false;
            if (failed > 0 && !closeAfterCancellation) SwitchCategory(2);
        }
    }
    // Filenames remain local UI-only; exported/persisted activity logs never read this control.
    private void AddImportResult(string fileName, bool success, string reason)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = (success ? "✓  " : "✕  ") + fileName, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush(success ? "#789567" : "#B46C89") });
        text.Children.Add(new TextBlock { Text = reason, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A18AAF"), Margin = new Thickness(17, 5, 0, 0) });
        ImportResultsList.Children.Add(new Border { Child = text, Background = Brush(success ? "#F3F7EF" : "#FCF0F5"), CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 0, 0, 7) });
    }
    private async Task<bool> ImportFileAsync(string filePath)
    {
        if (busy) return false;
        if (!File.Exists(filePath))
        {
            AddLog(ActivityEvent.FileReadFailed, ActivityLevel.Warning, ActivityFailure.FileAccess);
            ShowNotice("Không tìm thấy file trên máy.", error: true);
            return false;
        }

        try
        {
            FileNameText.Text = Path.GetFileName(filePath);
            var fi = new FileInfo(filePath);
            FileHintText.Text = $"{fi.Length / 1024d:0.#} KB  ·  Đang kiểm tra và lưu cookie…";

            string text = await File.ReadAllTextAsync(filePath);
            return await ProcessCookieTextAsync(text, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.FileReadFailed, ex);
            ShowNotice("Chưa đọc được file. Bạn kiểm tra file và quyền truy cập rồi thử lại nhé.", error: true);
            return false;
        }
    }

    private async Task<bool> ProcessCookieTextAsync(string cookieText, string sourceName)
    {
        if (busy) return false;
        if (string.IsNullOrWhiteSpace(cookieText))
        {
            AddLog(ActivityEvent.InputRejected, ActivityLevel.Warning, ActivityFailure.InvalidInput);
            ShowNotice("Nội dung cookie trống. Vui lòng kiểm tra lại file hoặc clipboard.", error: true);
            return false;
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        SetBusy(true);
        var operationTimer = Stopwatch.StartNew();
        SetStatus("Đang xử lý cookie", "#F0E9F8", "#8868AA");
        ShowNotice("Đang đăng nhập Microsoft & xác thực Minecraft…");

        var progress = new Progress<string>(message =>
        {
            if (!busy || source.IsCancellationRequested) return;
            LogProgress(message);
        });

        try
        {
            var proxy = ProxyStore.GetNextRotatingProxy();
            string? proxyAddr = proxy?.Address;
            if (!string.IsNullOrEmpty(proxyAddr))
            {
                AddLog(ActivityEvent.ProxySelected);
            }
            var result = await SessionService.CreateFromTextAsync(cookieText, progress, source.Token, proxyAddr);

            // Lưu vào CookieStore tại ~/minecraft_cookie_manager/accounts.json
            var saved = CookieStore.Upsert(result.Username, result.FormattedUuid, cookieText, result.AccessToken, sourceName, proxyAddr);

            ReloadAccountsList(saved.Id);

            AddLog(ActivityEvent.CookieSaved, ActivityLevel.Success, durationMs: operationTimer.ElapsedMilliseconds);
            SetStatus("Thành công", "#EDF4E8", "#6E8A61");

            ShowNotice("Đã thêm tài khoản.", success: true);

            FileHintText.Text = $"Đã lưu vào kho  ·  {result.Username}";
            return true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            SetStatus("Đã hủy", "#F2EDF8", "#8C799F");
            AddLog(ActivityEvent.ProcessingCanceled, ActivityLevel.Warning, ActivityFailure.Canceled);
            ShowNotice("Đã hủy xử lý cookie.");
            return false;
        }
        catch (Exception ex)
        {
            var message = ex is HttpRequestException
                ? "Chưa kết nối được dịch vụ. Vui lòng kiểm tra mạng rồi thử lại nhé."
                : FriendlyFailure(ex);
            SetStatus("Lỗi cookie", "#FBEAF0", "#B16A84");
            LogError(ActivityEvent.ProcessingFailed, ex, operationTimer.ElapsedMilliseconds);
            ShowNotice(message, error: true);
            return false;
        }
        finally
        {
            cancellation = null;
            SetBusy(false);
            if (closeAfterCancellation) Close();
        }
    }

    private async void RefreshCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (busy || importingFiles || currentAccount == null) return;
        if (string.IsNullOrWhiteSpace(currentAccount.RawCookies))
        {
            ShowNotice("Tài khoản này không có dữ liệu cookie gốc để làm mới.", error: true);
            return;
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        SetBusy(true);
        var operationTimer = Stopwatch.StartNew();
        SetStatus("Đang làm mới token", "#F0E9F8", "#8868AA");
        ShowNotice("Đang làm mới phiên…");

        var progress = new Progress<string>(message =>
        {
            if (!busy || source.IsCancellationRequested) return;
            LogProgress(message);
        });

        try
        {
            var proxy = ProxyStore.GetNextRotatingProxy();
            string? proxyAddr = proxy?.Address;
            if (!string.IsNullOrEmpty(proxyAddr))
            {
                AddLog(ActivityEvent.ProxySelected);
            }
            var result = await SessionService.CreateFromTextAsync(currentAccount.RawCookies, progress, source.Token, proxyAddr);
            CookieStore.UpdateStatus(currentAccount.Id, "Hoạt động", result.AccessToken, proxyAddr);
            ReloadAccountsList(currentAccount.Id);

            AddLog(ActivityEvent.SessionRefreshed, ActivityLevel.Success, durationMs: operationTimer.ElapsedMilliseconds);
            SetStatus("Hoạt động", "#EDF4E8", "#6E8A61");

            if (TryCopy(result.AccessToken))
                ShowNotice("Đã làm mới phiên và sao chép token.", success: true);
            else
                ShowNotice("Đã làm mới phiên.", success: true);
        }
        catch (Exception ex)
        {
            CookieStore.UpdateStatus(currentAccount.Id, "Hết hạn");
            ReloadAccountsList(currentAccount.Id);

            var msg = FriendlyFailure(ex);
            SetStatus("Hết hạn", "#FFF1E5", "#D35400");
            LogError(ActivityEvent.RefreshFailed, ex, operationTimer.ElapsedMilliseconds);
            ShowNotice($"Làm mới thất bại: {msg}", error: true);
        }
        finally
        {
            cancellation = null;
            SetBusy(false);
        }
    }

    private async void DeleteCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (currentAccount == null) return;

        var account = currentAccount;
        if (await ConfirmDeletionAsync("Xóa cookie này?", $"Cookie của “{account.Username}” sẽ được xóa khỏi danh sách đã lưu. Bạn sẽ cần nhập lại cookie nếu muốn dùng tài khoản này sau đó.", "Xóa cookie"))
        {
            string deletedName = account.Username;
            CookieStore.Delete(account.Id);
            currentAccount = null;
            ReloadAccountsList();
            ShowNotice("Đã xóa tài khoản.", success: true);
            AddLog(ActivityEvent.AccountRemoved, ActivityLevel.Success);
        }
    }
    #endregion

    #region Drop & File Dialog & Paste
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && !importingFiles && e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && files.All(File.Exists)
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropZoneCard.BorderBrush = Brush(e.Effects == DragDropEffects.Copy ? "#A789C5" : "#DECDEE");
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropZoneCard.BorderBrush = Brush("#DECDEE");

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropZoneCard.BorderBrush = Brush("#DECDEE");
        if (!busy && !importingFiles && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            await ImportFilesAsync(files);
        }
        e.Handled = true;
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || importingFiles) return;
        var dialog = new OpenFileDialog
        {
            Title = "Chọn file cookie Minecraft",
            Filter = "File Cookie (*.txt;*.json;*.cookies)|*.txt;*.json;*.cookies|Cookie Netscape (*.txt)|*.txt|Cookie JSON (*.json)|*.json|Tất cả file (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportFilesAsync(dialog.FileNames);
        }
    }

    private async void PasteCookie_Click(object sender, RoutedEventArgs e)
    {
        if (busy || importingFiles) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowNotice("Bộ nhớ tạm (Clipboard) không có văn bản.", error: true);
                    return;
                }
                FileNameText.Text = "Nhập từ Clipboard";
                FileHintText.Text = "Đang kiểm tra dữ liệu cookie vừa dán…";
                await ProcessCookieTextAsync(text, "Clipboard");
            }
            else
            {
                ShowNotice("Bộ nhớ tạm (Clipboard) không chứa văn bản.", error: true);
            }
        }
        catch (ExternalException)
        {
            ShowNotice("Không truy cập được Clipboard. Bạn thử lại nhé.", error: true);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        CookieStore.OpenFolder();
        ShowNotice("Đã mở thư mục lưu trữ.", success: true);
    }
    #endregion

    #region Copy & Visibility
    private void RevealToken_Changed(object sender, RoutedEventArgs e)
    {
        if (currentAccount == null) return;
        bool isRevealed = RevealToken.IsChecked == true;
        TokenField.Visibility = isRevealed ? Visibility.Visible : Visibility.Collapsed;
        TokenPassword.Visibility = isRevealed ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool TryCopy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (ExternalException) { return false; }
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (currentAccount?.AccessToken == null) return;
        bool copied = TryCopy(currentAccount.AccessToken);
        AddLog(copied ? ActivityEvent.TokenCopied : ActivityEvent.ClipboardFailed, copied ? ActivityLevel.Success : ActivityLevel.Warning);
        ShowNotice(copied ? "Đã sao chép token." : "Clipboard đang bận. Bạn thử lại nhé.", success: copied);
    }
    private static string ConvertCookiesToJson(string rawCookies)
    {
        string trimmed = rawCookies.Trim();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch { return rawCookies; }
        }

        var list = new List<Dictionary<string, object>>();
        using var reader = new StringReader(rawCookies);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string clean = line.Trim();
            if (string.IsNullOrEmpty(clean)) continue;
            if (clean.StartsWith("#HttpOnly_")) clean = clean.Substring(10);
            else if (clean.StartsWith("#")) continue;

            var parts = clean.Split('\t');
            if (parts.Length < 7) continue;

            long exp = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            if (long.TryParse(parts[4], out var parsedExp) && parsedExp > 0) exp = parsedExp;

            var item = new Dictionary<string, object>
            {
                ["domain"] = parts[0],
                ["hostOnly"] = !parts[0].StartsWith("."),
                ["httpOnly"] = parts[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                ["path"] = parts[2],
                ["secure"] = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                ["expirationDate"] = exp,
                ["name"] = parts[5],
                ["value"] = parts[6]
            };
            list.Add(item);
        }
        return System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private void ExportCookie_Click(object sender, RoutedEventArgs e)
    {
        if (currentAccount == null || string.IsNullOrWhiteSpace(currentAccount.RawCookies))
        {
            ShowNotice("Tài khoản chưa có dữ liệu cookie để xuất.", error: true);
            return;
        }

        try
        {
            var dialog = new SaveFileDialog
            {
                Title = $"Xuất file Cookie cho {currentAccount.Username}",
                Filter = "File Cookie Netscape (*.txt)|*.txt|File Cookie JSON (*.json)|*.json|Tất cả file (*.*)|*.*",
                FileName = $"{currentAccount.Username}_cookies.txt"
            };

            if (dialog.ShowDialog(this) == true)
            {
                string exportContent = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ConvertCookiesToJson(currentAccount.RawCookies)
                : currentAccount.RawCookies;
            File.WriteAllText(dialog.FileName, exportContent);
                TryCopy(currentAccount.RawCookies);
                ShowNotice("Đã xuất cookie.", success: true);
                AddLog(ActivityEvent.CookieExported, ActivityLevel.Success);
            }
        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.FileReadFailed, ex);
            ShowNotice("Chưa xuất được cookie. Bạn chọn thư mục có quyền ghi rồi thử lại nhé.", error: true);
        }
    }

    private void StarRepo_Click(object sender, RoutedEventArgs e)
    {
        string targetUrl = "https://github.com/GauCandy/mc_session";
        try
        {
            Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
            ShowNotice("Đã mở GitHub. Cảm ơn bạn đã ghé thăm ♡", success: true);

        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.UnexpectedFailure, ex);
            ShowNotice("Chưa mở được trình duyệt. Bạn có thể ghé GitHub của dự án để xem nhé.", error: true);
        }
    }

    private void CopyUsername_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(UsernameField.Text) || UsernameField.Text == "Chưa chọn tài khoản") return;
        bool copied = TryCopy(UsernameField.Text);
        AddLog(copied ? ActivityEvent.UsernameCopied : ActivityEvent.ClipboardFailed, copied ? ActivityLevel.Success : ActivityLevel.Warning);
        ShowNotice(copied ? "Đã sao chép tên người chơi." : "Clipboard đang bận.", success: copied);
    }

    private void CopyUuid_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(UuidField.Text) || UuidField.Text.Contains("Chưa") || UuidField.Text.Contains("Sẽ hiển thị")) return;
        bool copied = TryCopy(UuidField.Text);
        AddLog(copied ? ActivityEvent.UuidCopied : ActivityEvent.ClipboardFailed, copied ? ActivityLevel.Success : ActivityLevel.Warning);
        ShowNotice(copied ? "Đã sao chép UUID." : "Clipboard đang bận.", success: copied);
    }
    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        activityLog.Clear();
        ImportResultsList.Children.Clear();
        ImportResultsPanel.Visibility = Visibility.Collapsed;
        LogEntries.Children.Clear();
        AddLog(ActivityEvent.LogsCleared);
    }
    #endregion

    #region Proxy Manager View & Operations
    private void UpdateProxyPoolUI()
    {
        if (ProxyPoolBadge == null || ProxyPoolText == null || ProxyPoolIcon == null) return;
        int count = proxies.Count;
        if (count > 0)
        {
            ProxyPoolBadge.Background = Brush("#EDF7EE");
            ProxyPoolText.Text = $"{count} Proxy";
            ProxyPoolText.Foreground = Brush("#4E8C5C");
            ProxyPoolIcon.Text = "🛡️";
            ProxyPoolBadge.ToolTip = $"Đang bật xoay vòng qua {count} proxy để lấy NFA, tránh bị gắn cờ spam IP.";
        }
        else
        {
            ProxyPoolBadge.Background = Brush("#F1EAF7");
            ProxyPoolText.Text = "IP máy";
            ProxyPoolText.Foreground = Brush("#85719B");
            ProxyPoolIcon.Text = "🌐";
            ProxyPoolBadge.ToolTip = "Chưa có proxy nào. Ứng dụng sẽ dùng IP trực tiếp của máy bạn.";
        }
    }

    private void ReloadProxyList()
    {
        proxies = ProxyStore.LoadAll();
        CategoryProxyCount.Text = proxies.Count.ToString();
        ProxyTotalCountText.Text = $"{proxies.Count} proxy";
        int activeCount = proxies.Count(p => p.Status == "Hoạt động");
        ProxyLiveSummaryText.Text = $"{activeCount} hoạt động";
        UpdateProxyPoolUI();

        EmptyProxyPlaceholder.Visibility = proxies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProxyListContainer.Children.Clear();

        foreach (var p in proxies)
        {
            ProxyListContainer.Children.Add(CreateProxyCard(p));
        }
    }

    private Border CreateProxyCard(SavedProxy proxy)
    {
        var card = new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#EBE3F2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var addr = new TextBlock
        {
            Text = ProxyStore.GetDisplayAddress(proxy.Address),
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#3E354D"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var meta = new TextBlock
        {
            Text = proxy.LastChecked.HasValue
                ? $"Đã test: {proxy.LastChecked.Value.ToLocalTime():dd/MM HH:mm:ss}  ·  Độ trễ: {proxy.LatencyText}"
                : "Chưa kiểm tra",
            FontSize = 10,
            Foreground = Brush("#9D8FA8"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        info.Children.Add(addr);
        info.Children.Add(meta);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var statusBadge = new Border
        {
            Background = Brush(proxy.StatusBgColor),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        var statusText = new TextBlock
        {
            Text = proxy.Status,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(proxy.StatusColor)
        };
        statusBadge.Child = statusText;
        Grid.SetColumn(statusBadge, 1);
        grid.Children.Add(statusBadge);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var testBtn = new Button
        {
            Content = "⚡ Kiểm tra",
            IsEnabled = !isTestingProxies && !checkingProxyIds.Contains(proxy.Id),
            ToolTip = "Kiểm tra kết nối qua proxy tới Minecraft",
            Padding = new Thickness(7, 3, 7, 3),
            FontSize = 10,
            Margin = new Thickness(0, 0, 4, 0)
        };
        testBtn.Click += async (_, _) =>
        {
            if (isTestingProxies || !checkingProxyIds.Add(proxy.Id)) return;
            testBtn.IsEnabled = false;
            statusText.Text = "Đang kiểm tra…";
            AddLog(ActivityEvent.ProxyCheckStarted, itemCount: 1);
            try
            {
                await ProxyStore.TestProxyAsync(proxy);
                bool saveFailed = PersistProxyResult(proxy);
                LogProxyResult(proxy);
                ShowNotice(saveFailed ? "Đã kiểm tra nhưng chưa lưu được kết quả. Bạn kiểm tra quyền thư mục lưu trữ rồi thử lại nhé." : proxy.ErrorDescription,
                    error: saveFailed || proxy.FailureKind != ProxyCheckFailure.None, success: !saveFailed && proxy.FailureKind == ProxyCheckFailure.None);
            }
            finally
            {
                checkingProxyIds.Remove(proxy.Id);
                ReloadProxyList();
            }
        };
        var delBtn = new Button
        {
            Content = "🗑",
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 10,
            Style = (Style)FindResource("DangerButton")
        };
        delBtn.Click += (_, _) =>
        {
            ProxyStore.Delete(proxy.Id);
            AddLog(ActivityEvent.ProxyRemoved, ActivityLevel.Success);
            ReloadProxyList();
            ShowNotice("Đã xóa proxy khỏi kho.", success: true);
        };
        actions.Children.Add(testBtn);
        actions.Children.Add(delBtn);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions[2].Width = new GridLength(0);
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 3);
        statusBadge.ToolTip = proxy.LastChecked.HasValue ? proxy.ErrorDescription : "Chưa kiểm tra";
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private void AddProxies_Click(object sender, RoutedEventArgs e)
    {
        string text = ProxyInputText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            AddLog(ActivityEvent.InputRejected, ActivityLevel.Warning, ActivityFailure.InvalidInput);
            ShowNotice("Vui lòng nhập hoặc dán danh sách proxy vào ô.", error: true);
            return;
        }
        int added = ProxyStore.AddFromText(text);
        AddLog(ActivityEvent.ProxyAdded, added > 0 ? ActivityLevel.Success : ActivityLevel.Warning, itemCount: added);
        ProxyInputText.Clear();
        ReloadProxyList();
        ShowNotice($"Đã thêm thành công {added} proxy vào kho lưu trữ.", success: true);
    }

    private void PasteProxies_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ProxyInputText.Text = text;
                    ShowNotice("Đã dán danh sách từ Clipboard. Bấm '+ Lưu Proxy' để lưu.", success: true);
                }
            }
        }
        catch (ExternalException)
        {
            ShowNotice("Clipboard đang bận. Bạn vui lòng thử lại.", error: true);
        }
    }

    private async void ClearAllProxies_Click(object sender, RoutedEventArgs e)
    {
        if (proxies.Count == 0) return;
        if (await ConfirmDeletionAsync("Xóa tất cả proxy?", $"Toàn bộ {proxies.Count} proxy sẽ được xóa khỏi danh sách đã lưu. Thao tác này không thể hoàn tác.", "Xóa tất cả"))
        {
            ProxyStore.ClearAll();
            AddLog(ActivityEvent.ProxiesCleared, ActivityLevel.Success);
            ReloadProxyList();
            ShowNotice("Đã xóa toàn bộ proxy.", success: true);
        }
    }

    private async void TestAllProxies_Click(object sender, RoutedEventArgs e)
    {
        if (isTestingProxies) { proxyTestCts?.Cancel(); return; }
        if (checkingProxyIds.Count != 0)
        {
            ShowNotice("Đang kiểm tra một proxy. Bạn chờ thao tác đó xong rồi kiểm tra tất cả nhé.");
            return;
        }
        var snapshot = proxies.ToArray();
        if (snapshot.Length == 0) { ShowNotice("Chưa có proxy nào để kiểm tra.", error: true); return; }
        isTestingProxies = true;
        TestAllProxiesBtn.Content = "⛔ Dừng kiểm tra";
        using var source = new CancellationTokenSource();
        proxyTestCts = source;
        var token = source.Token;
        AddLog(ActivityEvent.ProxyCheckStarted, itemCount: snapshot.Length);
        ShowNotice($"Đang kiểm tra {snapshot.Length} proxy kết nối tới Minecraft…");
        int storageFailures = 0;
        using var limit = new SemaphoreSlim(8);
        try
        {
            var tasks = snapshot.Select(async proxy =>
            {
                await limit.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await ProxyStore.TestProxyAsync(proxy, token);
                    if (!token.IsCancellationRequested)
                    {
                        if (PersistProxyResult(proxy)) Interlocked.Increment(ref storageFailures);
                        LogProxyResult(proxy);
                        ReloadProxyList();
                    }
                }
                finally { limit.Release(); }
            });
            await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();
            ShowNotice(storageFailures == 0 ? $"Đã kiểm tra xong {snapshot.Length} proxy!"
                : "Đã kiểm tra nhưng có kết quả chưa lưu được. Bạn kiểm tra quyền thư mục lưu trữ nhé.", error: storageFailures > 0, success: storageFailures == 0);
        }
        catch (OperationCanceledException)
        {
            AddLog(ActivityEvent.ProxyCheckCanceled, ActivityLevel.Warning, ActivityFailure.Canceled);
            ShowNotice("Đã dừng kiểm tra proxy.");
        }
        finally
        {
            isTestingProxies = false;
            TestAllProxiesBtn.Content = "⚡ Kiểm tra tất cả";
            proxyTestCts = null;
            ReloadProxyList();
        }
    }

    // Returns true only for a storage problem, not for an intentionally discarded stale/deleted result.
    private bool PersistProxyResult(SavedProxy result)
    {
        if (ProxyStore.SaveTestResult(result)) return false;
        var current = ProxyStore.LoadAll().FirstOrDefault(p => p.Id == result.Id);
        if (current == null || current.Address != result.Address || current.LastChecked > result.LastChecked) return false;
        AddLog(ActivityEvent.ProxyResultNotSaved, ActivityLevel.Warning, ActivityFailure.FileAccess);
        return true;
    }
    private void LogProxyResult(SavedProxy proxy)
    {
        var failure = proxy.FailureKind switch
        {
            ProxyCheckFailure.None => ActivityFailure.None,
            ProxyCheckFailure.InvalidAddress => ActivityFailure.InvalidInput,
            ProxyCheckFailure.Timeout => ActivityFailure.Timeout,
            ProxyCheckFailure.AuthRequired => ActivityFailure.ProxyAuthentication,
            ProxyCheckFailure.Blocked => ActivityFailure.ProxyBlocked,
            ProxyCheckFailure.RateLimited => ActivityFailure.RateLimited,
            ProxyCheckFailure.RemoteError => ActivityFailure.RemoteService,
            ProxyCheckFailure.Dns => ActivityFailure.Dns,
            ProxyCheckFailure.Connection => ActivityFailure.Connection,
            ProxyCheckFailure.Tls => ActivityFailure.Tls,
            ProxyCheckFailure.Tunnel => ActivityFailure.ProxyTunnel,
            ProxyCheckFailure.Canceled => ActivityFailure.Canceled,
            _ => ActivityFailure.Unexpected
        };
        AddLog(failure == ActivityFailure.None ? ActivityEvent.ProxyCheckPassed : ActivityEvent.ProxyCheckFailed,
            failure == ActivityFailure.None ? ActivityLevel.Success : ActivityLevel.Error,
            failure, proxy.LastHttpStatus, proxy.LatencyMs >= 0 ? proxy.LatencyMs : null);
    }
    #endregion

    #region UI Helpers & Logging
    private void SetBusy(bool value)
    {
        busy = value;
        WorkProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.IsIndeterminate = value;
        RefreshButton.IsEnabled = !value && currentAccount != null && !string.IsNullOrEmpty(currentAccount.RawCookies);
        DeleteButton.IsEnabled = !value && currentAccount != null;
        ExportCookieButton.IsEnabled = !value && currentAccount != null && !string.IsNullOrEmpty(currentAccount.RawCookies);
    }

    private static string FriendlyFailure(Exception exception) => SessionActivityLog.ClassifyFailure(exception) switch
    {
        ActivityFailure.Timeout => "Kết nối mất quá nhiều thời gian. Bạn thử lại nhé.",
        ActivityFailure.Canceled => "Thao tác đã được dừng.",
        ActivityFailure.InvalidInput => "Dữ liệu chưa hợp lệ. Bạn kiểm tra lại file hoặc định dạng proxy nhé.",
        ActivityFailure.AuthenticationRequired => "Cookie đã hết hạn hoặc tài khoản cần xác minh. Bạn đăng nhập lại rồi thử nhé.",
        ActivityFailure.NoGameLicense => "Tài khoản chưa có quyền sử dụng Minecraft Java.",
        ActivityFailure.FileAccess => "Chưa truy cập được file. Bạn kiểm tra quyền thư mục rồi thử lại nhé.",
        ActivityFailure.ProxyAuthentication => "Proxy yêu cầu xác thực. Bạn kiểm tra tài khoản và mật khẩu proxy nhé.",
        ActivityFailure.ProxyBlocked => "Dịch vụ đích đang từ chối truy cập qua proxy này.",
        ActivityFailure.RateLimited => "Dịch vụ đang giới hạn yêu cầu. Bạn thử lại sau nhé.",
        ActivityFailure.InvalidServiceResponse => "Dịch vụ trả về dữ liệu chưa hợp lệ. Bạn thử lại nhé.",
        ActivityFailure.Network or ActivityFailure.Dns or ActivityFailure.Connection or ActivityFailure.Tls or ActivityFailure.ProxyTunnel
            => "Chưa kết nối được dịch vụ. Bạn kiểm tra mạng, loại proxy và cổng kết nối nhé.",
        _ => "Thao tác chưa hoàn tất. Bạn có thể xuất log trong mục Nhật ký để báo lỗi."
    };
    private void LogProgress(string message)
    {
        var step = SessionActivityLog.ProgressEvent(message);
        if (step.HasValue) AddLog(step.Value);
    }

    private void LogError(ActivityEvent evt, Exception exception, long? durationMs = null)
        => AddLog(evt, ActivityLevel.Error, SessionActivityLog.ClassifyFailure(exception), SessionActivityLog.GetHttpStatus(exception), durationMs);

    private void AddLog(ActivityEvent evt, ActivityLevel level = ActivityLevel.Info,
        ActivityFailure failure = ActivityFailure.None, int? httpStatus = null,
        long? durationMs = null, int? itemCount = null)
        => AppendLogEntry(activityLog.Record(evt, level, failure, httpStatus, durationMs, itemCount));

    private void AppendLogEntry(ActivityEntry entry)
    {
        bool follow = LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 20;
        var row = new Grid { Margin = new Thickness(8, 10, 8, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = entry.Time.ToLocalTime().ToString("HH:mm:ss"), FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = Brush("#B09CBE"), Margin = new Thickness(0, 2, 0, 0) });
        var color = entry.Level switch
        {
            ActivityLevel.Error => "#C67C96", ActivityLevel.Warning => "#CBA268",
            ActivityLevel.Success => "#8EAC7D", _ => "#B59BCC"
        };
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Brush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
        Grid.SetColumn(dot, 1);
        row.Children.Add(dot);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = entry.Message, FontSize = 12, Foreground = Brush("#705883"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = entry.Detail, FontSize = 10, Foreground = Brush("#AA94B8"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        LogEntries.Children.Add(row);
        while (LogEntries.Children.Count > SessionActivityLog.MaxEntries) LogEntries.Children.RemoveAt(0);
        RefreshLogMetadata();
        if (follow) LogScroll.ScrollToEnd();
    }

    private void RefreshLogMetadata()
    {
        int count = activityLog.Entries.Count;
        CategoryLogCount.Text = count.ToString();
        LogSessionText.Text = $"Phiên {activityLog.SessionId}  ·  {count} sự kiện";
        LogStatusText.Text = activityLog.CanPersist
            ? "Log được lưu trên máy, giữ tối đa 10 phiên gần nhất. Chỉ xuất và gửi khi bạn chủ động chọn."
            : "Chưa ghi được log ra ổ đĩa. Phiên này vẫn xem và xuất được trong ứng dụng.";
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Xuất log an toàn để báo lỗi", Filter = "Nhật ký (*.txt)|*.txt", FileName = $"minecraft-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, activityLog.ToReport(), new System.Text.UTF8Encoding(false));
            AddLog(ActivityEvent.LogExported, ActivityLevel.Success);
            LogStatusText.Text = "Đã xuất log. Bạn có thể đính kèm tệp khi báo lỗi; app không tự tải tệp lên mạng.";
        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.FileReadFailed, ex);
            LogStatusText.Text = "Chưa xuất được log. Bạn thử chọn thư mục có quyền ghi nhé.";
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(activityLog.DirectoryPath);
            Process.Start(new ProcessStartInfo(activityLog.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ActivityEvent.FileReadFailed, ex);
            LogStatusText.Text = "Chưa mở được thư mục log. Bạn vẫn có thể dùng nút xuất log.";
        }
    }

    internal void RecordUnhandledException(Exception exception)
    {
        var entry = activityLog.Record(ActivityEvent.UnexpectedFailure, ActivityLevel.Error,
            SessionActivityLog.ClassifyFailure(exception), SessionActivityLog.GetHttpStatus(exception));
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => AppendLogEntry(entry)));
    }

    internal void RecordShutdown() => activityLog.Record(ActivityEvent.AppClosed);
    private void SetStatus(string text, string background, string foreground)
    {
        ActiveStatusText.Text = text;
        ActiveStatusBadge.Background = Brush(background);
        ActiveStatusText.Foreground = Brush(foreground);
    }

    private void ShowNotice(string message, bool error = false, bool success = false)
    {
        NoticeText.Text = message;
        NoticeBorder.Background = Brush(error ? "#FBEAF0" : success ? "#EDF4E8" : "#F2EDF8");
        NoticeText.Foreground = NoticeIcon.Foreground = Brush(error ? "#B16A84" : success ? "#6E8A61" : "#8C799F");
        NoticeIcon.Text = error ? "!" : success ? "✓" : "♡";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (updaterRestarting) return;
        if (updateInProgress)
        {
            e.Cancel = true;
            closeAfterUpdateCancel = true;
            updateCancellation?.Cancel();
            return;
        }
        if (!busy) return;
        e.Cancel = true;
        closeAfterCancellation = true;
        cancellation?.Cancel();
    }

    private async Task<bool> ConfirmDeletionAsync(string title, string message, string action)
    {
        if (pendingConfirmation != null) return false;
        var completion = new TaskCompletionSource<bool>();
        pendingConfirmation = completion;
        var previousFocus = Keyboard.FocusedElement;
        ConfirmationTitle.Text = title;
        ConfirmationMessage.Text = message;
        ConfirmationDeleteButton.Content = action;
        ConfirmationOverlay.Visibility = Visibility.Visible;
        ConfirmationCancelButton.Focus();
        try { return await completion.Task; }
        finally
        {
            ConfirmationOverlay.Visibility = Visibility.Collapsed;
            pendingConfirmation = null;
            previousFocus?.Focus();
        }
    }

    private void CancelConfirmation_Click(object sender, RoutedEventArgs e) => pendingConfirmation?.TrySetResult(false);
    private void AcceptConfirmation_Click(object sender, RoutedEventArgs e) => pendingConfirmation?.TrySetResult(true);
    private void VersionEgg_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (updateInProgress) return;
        var now = Stopwatch.GetTimestamp();
        if (versionEggLastClick == 0 || Stopwatch.GetElapsedTime(versionEggLastClick, now) > TimeSpan.FromSeconds(2))
            versionEggClicks = 0;
        versionEggLastClick = now;
        if (++versionEggClicks < 5) return;
        versionEggClicks = 0;
        ToggleUpdatePreview_Click(sender, e);
    }
    private void ToggleUpdatePreview_Click(object sender, RoutedEventArgs e)
    {
        isUpdatePreview = !isUpdatePreview;
        AddLog(isUpdatePreview ? ActivityEvent.UpdatePreviewEnabled : ActivityEvent.UpdatePreviewDisabled);
        if (isUpdatePreview)
        {
            var current = Version.Parse(CurrentVersion);
            var sample = new Version(current.Major, current.Minor, Math.Max(0, current.Build) + 1);
            UpdateBadgeText.Text = $"Cập nhật v{sample}";
            UpdateBadge.Visibility = Visibility.Visible;
            UpdateBadge.ToolTip = "Đang xem thử giao diện thông báo cập nhật";
            ShowNotice("Bạn tìm thấy easter egg rồi ♡ Bấm nút cập nhật màu cam để xem thử. Bấm phiên bản 5 lần nữa để tắt.");
        }
        else
        {
            UpdateBadgeText.Text = latestAvailableVersion == null ? "Có bản mới" : $"Cập nhật v{latestAvailableVersion}";
            UpdateBadge.Visibility = latestAvailableVersion == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateBadge.ToolTip = "Nhấp để xem bản cập nhật mới";
            ShowNotice("Đã tắt xem thử. Thông báo cập nhật trở về trạng thái thực tế.");
        }

    }
    private void OpenAuthor_Click(object sender, RoutedEventArgs e)
    {
        AuthorOverlay.Visibility = Visibility.Visible;
        AuthorCloseButton.Focus();
    }

    private void CloseAuthor_Click(object sender, RoutedEventArgs e)
    {
        AuthorOverlay.Visibility = Visibility.Collapsed;
        AuthorButton.Focus();
    }

    private void AuthorBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == AuthorOverlay) CloseAuthor_Click(sender, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ConfirmationOverlay.Visibility == Visibility.Visible)
        {
            pendingConfirmation?.TrySetResult(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && UpdateOverlay.Visibility == Visibility.Visible)
        {
            UpdateLater_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && AuthorOverlay.Visibility == Visibility.Visible)
        {
            CloseAuthor_Click(sender, e);
            e.Handled = true;
        }
    }
    private void GitHubCredit_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/GauCandy") { UseShellExecute = true }); }
        catch { ShowNotice("Truy cập github.com/GauCandy trên trình duyệt."); }
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColumn == null || AccountsColumn == null || IdentityFields == null) return;
        var compact = e.NewSize.Width < 1000;
        SidebarColumn.Width = new GridLength(compact ? 164 : 200);
        AccountsColumn.Width = new GridLength(compact ? 180 : 240);
        var stackedFields = e.NewSize.Width < 1100;
        if (IdentityFields.RowDefinitions.Count == 0)
        {
            IdentityFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            IdentityFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var uuidGroup = (FrameworkElement)UuidField.Parent is Grid uuidGrid ? (FrameworkElement)uuidGrid.Parent : null;
        if (uuidGroup != null)
        {
            Grid.SetRow(uuidGroup, stackedFields ? 1 : 0);
            Grid.SetColumn(uuidGroup, stackedFields ? 0 : 2);
            uuidGroup.Margin = stackedFields ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        }
        IdentityFields.ColumnDefinitions[1].Width = new GridLength(stackedFields ? 0 : 12);
        IdentityFields.ColumnDefinitions[2].Width = stackedFields ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (ProxyWorkspace != null)
        {
            ProxyWorkspace.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(280);
            ProxyWorkspace.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 14);
            ProxyWorkspace.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            ProxyWorkspace.RowDefinitions[0].Height = compact ? new GridLength(210) : new GridLength(1, GridUnitType.Star);
            ProxyWorkspace.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(ProxyListCard, compact ? 1 : 0);
            Grid.SetColumn(ProxyListCard, compact ? 0 : 2);
            ProxyListCard.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    #endregion
}


















