using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusGuard.Core;
using Microsoft.Win32;

namespace FocusGuard.App;

/// <summary>屏蔽程序列表的一行：文件名 + 完整路径。</summary>
public sealed record ExeRow(string Name, string FilePath);

/// <summary>
/// 设置页：屏蔽网站（按主域成组增删）、屏蔽程序（资源管理器选择 exe：
/// 开始专注时结束其进程，勾选锁定后专注期间改名锁定文件）。
/// 编辑的是工作副本，点"保存"才写回配置并落盘，"取消"不留痕迹。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush ErrorRed = Hex("#DC2626");
    private static readonly Brush MutedGray = Hex("#9CA3AF");

    private readonly FocusConfig _config;
    private readonly string _configPath;

    private readonly List<string> _domains;
    private readonly List<string> _exePaths;
    private bool _lockFiles;

    public SettingsWindow(FocusConfig config, string configPath, bool isFocusing)
    {
        InitializeComponent();
        _config = config;
        _configPath = configPath;
        _domains = new List<string>(config.BlockedDomains);
        _exePaths = new List<string>(config.BlockedExePaths ?? new List<string>());
        _lockFiles = config.LockTargetExe;

        if (isFocusing)
        {
            FocusingNote.Visibility = Visibility.Visible;
        }

        LockFilesCheck.IsChecked = _lockFiles;
        RefreshSites();
        RefreshExes();
    }

    // ---- 屏蔽网站 ----

    private void AddSite_Click(object sender, RoutedEventArgs e) => AddSiteFromInput();

    private void SiteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddSiteFromInput();
            e.Handled = true;
        }
    }

    private void AddSiteFromInput()
    {
        var site = DomainRules.TryParseSite(SiteInput.Text);
        if (site is null)
        {
            ShowSiteMessage("无法识别的网站：请输入类似 hupu.com 或 www.hupu.com 的域名", isError: true);
            return;
        }

        var added = 0;
        foreach (var host in DomainRules.ExpandFamily(site))
        {
            if (!_domains.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                _domains.Add(host);
                added++;
            }
        }

        SiteInput.Clear();
        ShowSiteMessage(added == 0 ? site + " 已在屏蔽列表中" : null, isError: false);
        RefreshSites();
    }

    private void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DomainGroup group })
        {
            _domains.RemoveAll(d => group.Hostnames.Contains(d, StringComparer.OrdinalIgnoreCase));
            RefreshSites();
        }
    }

    private void ShowSiteMessage(string? text, bool isError)
    {
        SiteMessage.Text = text ?? string.Empty;
        SiteMessage.Foreground = isError ? ErrorRed : MutedGray;
        SiteMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshSites()
    {
        SiteList.ItemsSource = DomainRules.GroupBySite(_domains);
    }

    // ---- 屏蔽程序 ----

    private void BrowseExes_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要屏蔽的程序",
            Filter = "程序 (*.exe)|*.exe",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (!_exePaths.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _exePaths.Add(file);
            }
        }
        RefreshExes();
    }

    private void RemoveExe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ExeRow row })
        {
            _exePaths.RemoveAll(p => string.Equals(p, row.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshExes();
        }
    }

    private void RefreshExes()
    {
        ExeList.ItemsSource = null;
        ExeList.ItemsSource = _exePaths
            .Select(p => new ExeRow(Path.GetFileName(p), p))
            .ToList();
    }

    // ---- 保存 / 取消 ----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _lockFiles = LockFilesCheck.IsChecked == true;

        _config.BlockedDomains = _domains;
        _config.BlockedExePaths = _exePaths;
        _config.LockTargetExe = _lockFiles;

        try
        {
            _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            AppPaths.Log("设置保存失败: " + AppPaths.Describe(ex));
            MessageBox.Show(this,
                "配置保存失败：" + ex.Message + "\r\n更改未生效，请确认数据目录可写后重试。",
                "FocusGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AppPaths.Log($"设置已保存：{_domains.Count} 个域名、{_exePaths.Count} 个程序" +
                     (_lockFiles ? "，启用文件锁定" : "，未启用文件锁定"));
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Esc 等同取消：不保存直接关闭。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
