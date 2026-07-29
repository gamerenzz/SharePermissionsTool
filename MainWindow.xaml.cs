using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SharePermissionsTool
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;

        // 系统内置黑名单
        private static readonly HashSet<string> SystemBuiltInAccounts = new(StringComparer.OrdinalIgnoreCase)
        {
            @"NT AUTHORITY\SYSTEM",
            @"NT AUTHORITY\Authenticated Users",
            @"NT AUTHORITY\INTERACTIVE",
            @"BUILTIN\Administrators",
            @"BUILTIN\Users",
            @"CREATOR OWNER"
        };

        public MainWindow()
        {
            InitializeComponent();
            txtDomain.Text = Environment.UserDomainName;
            LoadLocalUsersAndGroups();
            LoadShares();
        }

        #region 数据模型
        public class ShareInfo
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
        }

        public class PermissionResult
        {
            public string Account { get; set; } = "";
            public string ShareName { get; set; } = "";
            public string Path { get; set; } = "";
            public string PermType { get; set; } = ""; // SMB 或 NTFS
            public string AccessControlType { get; set; } = "";
            public string Rights { get; set; } = "";
        }
        #endregion

        #region 初始化列表加载
        private void LoadLocalUsersAndGroups()
        {
            lstTargetUsers.Items.Clear();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_UserAccount WHERE LocalAccount=True");
                foreach (ManagementObject user in searcher.Get())
                {
                    string name = user["Name"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                        lstTargetUsers.Items.Add(new CheckBox { Content = $"[用户] {name}", Tag = name, Margin = new Thickness(2) });
                }

                using var groupSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Group WHERE LocalAccount=True");
                foreach (ManagementObject group in groupSearcher.Get())
                {
                    string name = group["Name"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                        lstTargetUsers.Items.Add(new CheckBox { Content = $"[组] {name}", Tag = name, Margin = new Thickness(2) });
                }
            }
            catch (Exception ex) { MessageBox.Show("加载本地账号失败: " + ex.Message); }
        }

        private List<ShareInfo> GetSmbShares()
        {
            var list = new List<ShareInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, Path FROM Win32_Share WHERE Type = 0");
                foreach (ManagementObject share in searcher.Get())
                {
                    string name = share["Name"]?.ToString() ?? "";
                    string path = share["Path"]?.ToString() ?? "";
                    if (!name.EndsWith("$") && !string.IsNullOrEmpty(path))
                    {
                        list.Add(new ShareInfo { Name = name, Path = path });
                    }
                }
            }
            catch { }
            return list;
        }

        private void LoadShares()
        {
            lstUserShares.Items.Clear();
            lstShareTabShares.Items.Clear();
            var shares = GetSmbShares();

            foreach (var s in shares)
            {
                lstUserShares.Items.Add(new CheckBox { Content = s.Name, Tag = s.Path, IsChecked = true, Margin = new Thickness(2) });
                lstShareTabShares.Items.Add(new CheckBox { Content = s.Name, Tag = s.Path, IsChecked = true, Margin = new Thickness(2) });
            }
        }
        #endregion

        #region 按钮事件与 GUI 控制
        private void BtnSelectAllUsers_Click(object sender, RoutedEventArgs e) => SetListChecked(lstTargetUsers, true);
        private void BtnClearAllUsers_Click(object sender, RoutedEventArgs e) => SetListChecked(lstTargetUsers, false);
        private void BtnRefreshUsers_Click(object sender, RoutedEventArgs e) => LoadLocalUsersAndGroups();

        private void BtnSelectAllUserShares_Click(object sender, RoutedEventArgs e) => SetListChecked(lstUserShares, true);
        private void BtnClearAllUserShares_Click(object sender, RoutedEventArgs e) => SetListChecked(lstUserShares, false);

        private void BtnSelectAllShareTabShares_Click(object sender, RoutedEventArgs e) => SetListChecked(lstShareTabShares, true);
        private void BtnClearAllShareTabShares_Click(object sender, RoutedEventArgs e) => SetListChecked(lstShareTabShares, false);
        private void BtnRefreshShares_Click(object sender, RoutedEventArgs e) => LoadShares();

        private void SetListChecked(ListBox box, bool isChecked)
        {
            foreach (CheckBox item in box.Items) item.IsChecked = isChecked;
        }

        private List<string> GetCheckedTags(ListBox box)
        {
            return box.Items.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag?.ToString() ?? c.Content.ToString()!)
                .ToList();
        }

        private List<ShareInfo> GetCheckedShares(ListBox box)
        {
            return box.Items.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => new ShareInfo { Name = c.Content.ToString()!, Path = c.Tag?.ToString() ?? "" })
                .ToList();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }
        #endregion

        #region 右键菜单与双击快捷打开/复制
        private void ContextMenu_CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is PermissionResult item)
            {
                if (!string.IsNullOrEmpty(item.Path))
                {
                    Clipboard.SetText(item.Path);
                    lblStatus.Text = $"已复制文件夹路径: {item.Path}";
                }
            }
        }

        private void ContextMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is PermissionResult item)
            {
                OpenFolderInExplorer(item.Path);
            }
        }

        private void ContextMenu_CopyRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is PermissionResult item)
            {
                string rowText = $"{item.Account}\t{item.ShareName}\t{item.Path}\t{item.PermType}\t{item.AccessControlType}\t{item.Rights}";
                Clipboard.SetText(rowText);
                lblStatus.Text = "已复制整行数据到剪贴板。";
            }
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid dg && dg.SelectedItem is PermissionResult item)
            {
                OpenFolderInExplorer(item.Path);
            }
        }

        private void OpenFolderInExplorer(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    Process.Start("explorer.exe", path);
                }
                else
                {
                    MessageBox.Show("该文件夹不存在或无法访问！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开资源管理器: " + ex.Message);
            }
        }
        #endregion

        #region 异步查询 1：按用户/组查询
        private async void BtnSearchByUser_Click(object sender, RoutedEventArgs e)
        {
            var selectedUsers = GetCheckedTags(lstTargetUsers);
            var selectedShares = GetCheckedShares(lstUserShares);

            if (!selectedUsers.Any() || !selectedShares.Any())
            {
                MessageBox.Show("请确保至少勾选了一个用户和共享文件夹！");
                return;
            }

            ToggleUI(false);
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s => lblStatus.Text = s);

            try
            {
                var rawResults = await Task.Run(() => ScanPermissionsByUser(selectedUsers, selectedShares, _cts.Token, progress));
                dgUserResults.ItemsSource = DeduplicateResults(rawResults);
                lblStatus.Text = $"查询完成，共找到 {dgUserResults.Items.Count} 条记录。";
            }
            catch (OperationCanceledException) { lblStatus.Text = "用户已停止查询。"; }
            catch (Exception ex) { MessageBox.Show("查询出错: " + ex.Message); }
            finally { ToggleUI(true); }
        }

        private List<PermissionResult> ScanPermissionsByUser(List<string> targetUsers, List<ShareInfo> shares, CancellationToken token, IProgress<string> progress)
        {
            var results = new List<PermissionResult>();

            foreach (var share in shares)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在扫描共享: {share.Name}...");

                if (!Directory.Exists(share.Path)) continue;

                var options = new System.IO.EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                var allFolders = new List<string> { share.Path };
                try
                {
                    allFolders.AddRange(Directory.EnumerateDirectories(share.Path, "*", options));
                }
                catch { }

                foreach (var folder in allFolders)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var dirInfo = new DirectoryInfo(folder);
                        var acl = dirInfo.GetAccessControl(AccessControlSections.Access);
                        var rules = acl.GetAccessRules(true, false, typeof(NTAccount)); // 仅非继承权限

                        foreach (FileSystemAccessRule rule in rules)
                        {
                            string account = rule.IdentityReference.Value;
                            string shortAccount = account.Contains('\\') ? account.Split('\\')[1] : account;

                            if (targetUsers.Contains(shortAccount, StringComparer.OrdinalIgnoreCase) ||
                                targetUsers.Contains(account, StringComparer.OrdinalIgnoreCase))
                            {
                                results.Add(new PermissionResult
                                {
                                    Account = account,
                                    ShareName = share.Name,
                                    Path = folder,
                                    PermType = "NTFS 权限",
                                    AccessControlType = rule.AccessControlType.ToString(),
                                    Rights = rule.FileSystemRights.ToString()
                                });
                            }
                        }
                    }
                    catch { }
                }
            }
            return results;
        }
        #endregion

        #region 异步查询 2：按共享文件夹查询
        private async void BtnSearchByShare_Click(object sender, RoutedEventArgs e)
        {
            var selectedShares = GetCheckedShares(lstShareTabShares);
            if (!selectedShares.Any())
            {
                MessageBox.Show("请至少选择一个共享文件夹！");
                return;
            }

            ToggleUI(false);
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s => lblStatus.Text = s);

            try
            {
                var rawResults = await Task.Run(() => ScanPermissionsByShare(selectedShares, _cts.Token, progress));
                dgShareResults.ItemsSource = DeduplicateResults(rawResults);
                lblStatus.Text = $"查询完成，共找到 {dgShareResults.Items.Count} 条记录。";
            }
            catch (OperationCanceledException) { lblStatus.Text = "用户已停止查询。"; }
            catch (Exception ex) { MessageBox.Show("查询出错: " + ex.Message); }
            finally { ToggleUI(true); }
        }

        private List<PermissionResult> ScanPermissionsByShare(List<ShareInfo> shares, CancellationToken token, IProgress<string> progress)
        {
            var results = new List<PermissionResult>();

            foreach (var share in shares)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在扫描共享路径: {share.Path}...");

                if (!Directory.Exists(share.Path)) continue;

                var options = new System.IO.EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                var allFolders = new List<string> { share.Path };
                try
                {
                    allFolders.AddRange(Directory.EnumerateDirectories(share.Path, "*", options));
                }
                catch { }

                foreach (var folder in allFolders)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        bool isRoot = folder.TrimEnd('\\').Equals(share.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        var dirInfo = new DirectoryInfo(folder);
                        var acl = dirInfo.GetAccessControl(AccessControlSections.Access);
                        var rules = acl.GetAccessRules(true, false, typeof(NTAccount)); // 仅非继承权限

                        foreach (FileSystemAccessRule rule in rules)
                        {
                            string account = rule.IdentityReference.Value;

                            // 过滤黑名单
                            if (!isRoot && SystemBuiltInAccounts.Contains(account))
                                continue;

                            results.Add(new PermissionResult
                            {
                                Account = account,
                                ShareName = share.Name,
                                Path = folder,
                                PermType = "NTFS 权限",
                                AccessControlType = rule.AccessControlType.ToString(),
                                Rights = rule.FileSystemRights.ToString()
                            });
                        }
                    }
                    catch { }
                }
            }
            return results;
        }
        #endregion

        #region 数据去重与权限文本清洗
        private List<PermissionResult> DeduplicateResults(List<PermissionResult> raw)
        {
            return raw.GroupBy(r => new { r.Path, r.Account, r.PermType, r.AccessControlType })
                .Select(g => new PermissionResult
                {
                    Account = g.Key.Account,
                    ShareName = g.First().ShareName,
                    Path = g.Key.Path,
                    PermType = g.Key.PermType,
                    AccessControlType = g.Key.AccessControlType,
                    Rights = CleanRightsString(g.Select(x => x.Rights))
                }).ToList();
        }

        private string CleanRightsString(IEnumerable<string> rights)
        {
            var list = rights.ToList();
            if (list.Any(r => r.Contains("FullControl"))) return "FullControl";

            var cleanList = new List<string>();
            foreach (var r in list.Distinct())
            {
                // 自动翻译未识别的数值位（如 268435456、-1610612 等）
                if (int.TryParse(r, out int val) || (r.StartsWith("-") && int.TryParse(r, out _)))
                {
                    cleanList.Add("特殊扩展权限 (Special Rights)");
                }
                else
                {
                    cleanList.Add(r);
                }
            }
            return string.Join(", ", cleanList.Distinct());
        }

        private void ToggleUI(bool isEnabled)
        {
            btnSearchByUser.IsEnabled = isEnabled;
            btnSearchByShare.IsEnabled = isEnabled;
            btnCancelUser.IsEnabled = !isEnabled;
            btnCancelShare.IsEnabled = !isEnabled;
        }
        #endregion

        #region CSV 导出
        private void BtnExportUser_Click(object sender, RoutedEventArgs e) => ExportCsv(dgUserResults, "按用户查询权限表");
        private void BtnExportShare_Click(object sender, RoutedEventArgs e) => ExportCsv(dgShareResults, "按共享文件夹查询表");

        private void ExportCsv(DataGrid grid, string defaultFileName)
        {
            if (grid.ItemsSource is not IEnumerable<PermissionResult> items || !items.Any())
            {
                MessageBox.Show("没有可导出的数据！");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"{defaultFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("匹配账号,共享根名称,实际路径,权限来源,访问类型,详细权限");
                    foreach (var item in items)
                    {
                        sb.AppendLine($"\"{item.Account}\",\"{item.ShareName}\",\"{item.Path}\",\"{item.PermType}\",\"{item.AccessControlType}\",\"{item.Rights}\"");
                    }
                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show("导出成功！\n路径: " + dialog.FileName);
                }
                catch (Exception ex) { MessageBox.Show("导出失败: " + ex.Message); }
            }
        }
        #endregion
    }
}
