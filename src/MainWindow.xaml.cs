using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace MinecraftSessionTool;

public partial class MainWindow : Window
{
    private string? cookiePath;
    private SessionResult? session;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool closeAfterCancellation;


    private const string CurrentVersion = "1.0.0";
    private string? updateUrl;

    public MainWindow(string? initialFile = null)
    {
        InitializeComponent();
        AddLog("Khu vườn đã sẵn sàng", "Chọn một file để bắt đầu nhé.");
        bool initialFileLoaded = false;
        Loaded += async (_, _) =>
        {
            _ = CheckForUpdatesAsync();
            if (initialFileLoaded) return;
            initialFileLoaded = true;
            if (!string.IsNullOrWhiteSpace(initialFile))
                await SelectFileAsync(initialFile);
            else
            {
                var adjacent = Path.Combine(AppContext.BaseDirectory, "cookies.txt");
                if (File.Exists(adjacent)) await SelectFileAsync(adjacent);
            }
        };
    }

    private async Task CheckForUpdatesAsync()
    {
        // Chạy hoàn toàn trên ThreadPool nền để UI không bao giờ bị ảnh hưởng
        await Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "MinecraftSessionTool");

                using var response = await client.GetAsync("https://api.github.com/repos/GauCandy/mc_session/releases/latest", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                    if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                    {
                        string latestTag = tagProp.GetString()?.Trim().TrimStart('v', 'V') ?? "";
                        if (Version.TryParse(latestTag, out var latestVer) && Version.TryParse(CurrentVersion, out var curVer))
                        {
                            if (latestVer > curVer)
                            {
                                updateUrl = $"https://github.com/GauCandy/mc_session/releases/tag/v{latestTag}";
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateBadgeText.Text = $"Có bản mới: v{latestTag}";
                                    UpdateBadge.Visibility = Visibility.Visible;
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                // Mất mạng, lỗi DNS, GitHub chặn IP, hoặc timeout -> im lặng bỏ qua, UI chạy bình thường
            }
        });
    }

    private void UpdateBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        string target = updateUrl ?? "https://github.com/GauCandy/mc_session/releases";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    internal async Task SelectFileAsync(string path)
    {
        if (busy) return;
        if (!File.Exists(path))
        {
            ShowNotice("Không tìm thấy file. Bạn chọn lại một file đang có trên máy nhé.", true);
            return;
        }
        cookiePath = Path.GetFullPath(path);
        ResetResult();
        FileNameText.Text = Path.GetFileName(cookiePath);
        FileNameText.ToolTip = cookiePath;
        var size = new FileInfo(cookiePath).Length;
        FileHintText.Text = $"{size / 1024d:0.#} KB  ·  Đã chọn, tự động tạo phiên";
        FileHintText.ToolTip = cookiePath;
        DropOutline.Stroke = Brush("#B7CDA6");
        SetStatus("Đã chọn file", "#EDF4E8", "#6E8A61");
        ShowNotice("Đã nhận file. Đang tự động tạo phiên Minecraft…");
        AddLog("Đã chọn file cookie", Path.GetFileName(cookiePath));
        await CreateSessionAsync();
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn file cookie Minecraft",
            Filter = "File cookie (*.txt;*.cookies)|*.txt;*.cookies|Tất cả file (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await SelectFileAsync(dialog.FileName);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && File.Exists(files[0])
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropOutline.Stroke = Brush(e.Effects == DragDropEffects.Copy ? "#A789C5" : "#DCCCEB");
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => RestoreDropOutline();

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        RestoreDropOutline();
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (files.Length == 1) await SelectFileAsync(files[0]);
            else ShowNotice("Chọn từng file một để tạo phiên nhé.", true);
        }
        e.Handled = true;
    }

    private void RestoreDropOutline() => DropOutline.Stroke = Brush(cookiePath == null ? "#DCCCEB" : "#B7CDA6");



    internal async Task CreateSessionAsync()
    {
        if (busy) return;
        if (cookiePath == null || !File.Exists(cookiePath))
        {
            ShowNotice("Bạn chọn file cookie trước nhé. Có thể kéo thả file vào khung phía trên.", true);
            return;
        }
        ResetResult();
        LogEntries.Children.Clear();
        using var source = new CancellationTokenSource();
        cancellation = source;
        SetBusy(true);
        SetStatus("Đang tạo phiên", "#F0E9F8", "#8868AA");
        ShowNotice("Đang chuẩn bị chuyến phiêu lưu của bạn…");
        var progress = new Progress<string>(message =>
        {
            if (!busy || source.IsCancellationRequested) return;
            AddLog(message);
            LogFooter.Text = message + "…";
        });
        try
        {
            var result = await SessionService.CreateAsync(cookiePath, progress, source.Token);
            ShowResult(result);
            AddLog("Phiên đã sẵn sàng!", $"Xin chào, {result.Username} ♡", true);
            LogFooter.Text = "Kết quả đã sẵn sàng để sao chép.";
            if (TryCopy(result.AccessToken))
                ShowNotice("Tạo phiên thành công! Token đã được sao chép.", success: true);
            else
                ShowNotice("Tạo phiên thành công! Bạn có thể nhấn “Sao chép token” để thử lại.", success: true);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            SetStatus("Đã hủy", "#F2EDF8", "#8C799F");
            AddLog("Đã dừng theo yêu cầu");
            ShowNotice("Đã hủy tạo phiên. Chọn hoặc thả lại file để thử lại nhé.");
            LogFooter.Text = "Nghỉ một chút rồi thử lại nhé.";
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Kết nối mất quá nhiều thời gian. Bạn thử lại nhé." : ex is HttpRequestException
                ? "Chưa kết nối được dịch vụ. Bạn kiểm tra mạng rồi thử lại nhé."
                : ex.Message;
            SetStatus("Cần kiểm tra", "#FBEAF0", "#B16A84");
            AddLog("Chưa tạo được phiên", message);
            ShowNotice(message, true);
            LogFooter.Text = "Kiểm tra file rồi thử lại nhé.";
        }
        finally
        {
            cancellation = null;
            SetBusy(false);
            if (closeAfterCancellation) Close();
        }
    }

    internal void ShowResult(SessionResult result)
    {
        session = result;
        UsernameField.Text = result.Username;
        UuidField.Text = result.FormattedUuid;
        TokenPassword.Password = result.AccessToken;
        TokenField.Text = result.AccessToken;
        TokenPlaceholder.Visibility = Visibility.Collapsed;
        TokenPassword.Visibility = Visibility.Visible;
        TokenField.Visibility = Visibility.Collapsed;
        RevealToken.IsEnabled = true;
        CopyTokenButton.IsEnabled = true;
        CopyArgsButton.IsEnabled = true;
        SetStatus("Thành công", "#EDF4E8", "#6E8A61");
    }

    private void ResetResult()
    {
        session = null;
        UsernameField.Text = "Chưa có người chơi";
        UuidField.Text = "Sẽ xuất hiện khi tạo phiên";
        TokenPassword.Password = "";
        TokenField.Clear();
        RevealToken.IsChecked = false;
        RevealToken.IsEnabled = false;
        TokenPlaceholder.Visibility = Visibility.Visible;
        TokenPassword.Visibility = TokenField.Visibility = Visibility.Collapsed;
        CopyTokenButton.IsEnabled = CopyArgsButton.IsEnabled = false;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ChooseButton.IsEnabled = !value;
        CancelButton.Visibility = WorkProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = value;
        WorkProgress.IsIndeterminate = value;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void RevealToken_Changed(object sender, RoutedEventArgs e)
    {
        if (TokenField == null || TokenPassword == null || session == null) return;
        TokenField.Visibility = RevealToken.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TokenPassword.Visibility = RevealToken.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool TryCopy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (ExternalException) { return false; }
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        ShowNotice(TryCopy(session.AccessToken) ? "Đã sao chép token. Dán bằng Ctrl + V nhé!" : "Clipboard đang bận. Bạn thử sao chép lại nhé.", success: true);
    }

    private void CopyArgs_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        ShowNotice(TryCopy(session.LaunchArguments) ? "Đã sao chép tham số khởi chạy Minecraft." : "Clipboard đang bận. Bạn thử sao chép lại nhé.", success: true);
    }

    private void AddLog(string title, string? detail = null, bool success = false)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new Border
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = Brush(success ? "#95B57E" : "#C6B0D8"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0)
        };
        row.Children.Add(dot);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = Brush(success ? "#6E8A61" : "#776382"), TextWrapping = TextWrapping.Wrap });
        if (detail != null)
            text.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = Brush("#AC98AE"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        LogEntries.Children.Add(row);
        LogScroll.ScrollToEnd();
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusText.Text = text;
        StatusBadge.Background = Brush(background);
        StatusText.Foreground = StatusDot.Fill = Brush(foreground);
    }

    private void ShowNotice(string message, bool error = false, bool success = false)
    {
        NoticeText.Text = message;
        NoticeBorder.Background = Brush(error ? "#FBEAF0" : success ? "#EDF4E8" : "#F2EDF8");
        NoticeText.Foreground = NoticeIcon.Foreground = Brush(error ? "#AD637F" : success ? "#6E8A61" : "#8C799F");
        NoticeIcon.Text = error ? "!" : success ? "✓" : "♡";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        closeAfterCancellation = true;
        cancellation?.Cancel();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (QuickGuide != null) QuickGuide.Visibility = e.NewSize.Height < 720 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GitHubCredit_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/GauCandy") { UseShellExecute = true }); }
        catch { ShowNotice("Bạn có thể ghé github.com/GauCandy để xem GitHub nhé."); }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}




