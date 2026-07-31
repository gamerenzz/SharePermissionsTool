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
using System.Windows.Media;
using Microsoft.Win32;

namespace SharePermissionsTool
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;

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
            public string RiskBadge { get; set; } = "🟩 安全";
            public string Account { get; set; } = "";
            public string ShareName { get; set; } = "";
            public string Path { get; set; } = "";
            public string PermType { get; set; } = ""; // SMB ∩ NTFS 或 NTFS
            public string InheritanceStatus { get; set; } = ""; // 直接 或 继承
            public string AccessControlType { get; set; } = "Allow"; // Allow 或 Deny

            public bool Read { get; set; }
            public bool Write { get; set; }
            public bool Modify { get; set; }
            public bool Delete { get; set; }
            public bool FullControl { get; set; }

            public string ReadStr => Read ? "√" : "-";
            public string WriteStr => Write ? "√" : "-";
            public string ModifyStr => Modify ? "√" : "-";
            public string DeleteStr => Delete ? "√" : "-";
            public string FullControlStr => FullControl ? "√" : "-";

            public string RightsDescription { get; set; } = "";
        }
        #endregion

        #region 系统账号过滤
        private static bool IsSystemAccount(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return false;

            string clean = account.ToUpperInvariant();
            if (clean.StartsWith(@"NT SERVICE\") || clean.StartsWith(@"NT AUTHORITY\"))
                return true;

            string[] blackList = {
                @"BUILTIN\ADMINISTRATORS",
                @"BUILTIN\USERS",
                @"CREATOR OWNER",
                @"TRUSTEDINSTALLER",
                @"SYSTEM"
            };

            return blackList.Any(b => clean.Equals(b) || clean.EndsWith(@"\" + b));
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

        #region GUI 交互控制
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow)
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (dep is DataGridRow row)
            {
                row.IsSelected = true;
                row.Focus();
            }
        }

        private PermissionResult? GetSelectedPermissionResult(object sender)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu menu && menu.PlacementTarget is DataGrid dg)
            {
                return dg.SelectedItem as PermissionResult;
            }

            if (dgUserResults.SelectedItem is PermissionResult uItem) return uItem;
            if (dgShareResults.SelectedItem is PermissionResult sItem) return sItem;
            return null;
        }

        private void ContextMenu_CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedPermissionResult(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                Clipboard.SetText(item.Path);
                lblStatus.Text = $"已复制路径: {item.Path}";
            }
        }

        private void ContextMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedPermissionResult(sender);
            if (item != null && Directory.Exists(item.Path))
            {
                OpenFolderInExplorer(item.Path);
            }
        }

        private void ContextMenu_CopyRow_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedPermissionResult(sender);
            if (item != null)
            {
                string rowText = $"{item.RiskBadge}\t{item.Account}\t{item.ShareName}\t{item.Path}\t{item.PermType}\t{item.InheritanceStatus}\t{item.AccessControlType}\t{item.ReadStr}\t{item.WriteStr}\t{item.ModifyStr}\t{item.DeleteStr}\t{item.FullControlStr}\t{item.RightsDescription}";
                Clipboard.SetText(rowText);
                lblStatus.Text = "已复制整行数据到剪贴板。";
            }
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid dg && dg.SelectedItem is PermissionResult item && Directory.Exists(item.Path))
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
                    MessageBox.Show("该文件夹不存在或无法访问！");
                }
            }
            catch (Exception ex) { MessageBox.Show("无法打开资源管理器: " + ex.Message); }
        }
        #endregion

        #region 核心权限引擎：SMB Share 权限读取
        private Dictionary<string, FileSystemRights> GetSmbShareAccessRights(string shareName)
        {
            var map = new Dictionary<string, FileSystemRights>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_LogicalShareSecuritySetting WHERE Name='{shareName}'");
                foreach (ManagementObject shareSec in searcher.Get())
                {
                    var outParams = shareSec.InvokeMethod("GetSecurityDescriptor", null, null);
                    if (outParams != null && outParams["Descriptor"] is ManagementBaseObject descriptor)
                    {
                        var dacl = descriptor["DACL"] as ManagementBaseObject[];
                        if (dacl != null)
                        {
                            foreach (var ace in dacl)
                            {
                                var trustee = ace["Trustee"] as ManagementBaseObject;
                                string domain = trustee?["Domain"]?.ToString() ?? "";
                                string name = trustee?["Name"]?.ToString() ?? "";
                                string account = string.IsNullOrEmpty(domain) ? name : $"{domain}\\{name}";

                                uint accessMask = Convert.ToUInt32(ace["AccessMask"]);
                                FileSystemRights rights = (FileSystemRights)accessMask;

                                map[account] = rights;
                            }
                        }
                    }
                }
            }
            catch { }
            return map;
        }
        #endregion

        #region 核心权限引擎：深度扫描与有效权限演算
        private async void BtnSearchByUser_Click(object sender, RoutedEventArgs e)
        {
            var selectedUsers = GetCheckedTags(lstTargetUsers);
            var selectedShares = GetCheckedShares(lstUserShares);
            bool showSystem = chkShowSystemTab1.IsChecked == true;
            bool includePureInherited = chkIncludeInheritedTab1.IsChecked == true;
            bool calcEffective = chkCalcEffectiveTab1.IsChecked == true;

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
                var rawResults = await Task.Run(() => PerformScan(selectedUsers, selectedShares, showSystem, includePureInherited, calcEffective, _cts.Token, progress));
                dgUserResults.ItemsSource = DeduplicateAndEvaluateRisks(rawResults);
                lblStatus.Text = $"查询完成，共找到 {dgUserResults.Items.Count} 条记录。";
            }
            catch (OperationCanceledException) { lblStatus.Text = "查询停止。"; }
            catch (Exception ex) { MessageBox.Show("查询出错: " + ex.Message); }
            finally { ToggleUI(true); }
        }

        private async void BtnSearchByShare_Click(object sender, RoutedEventArgs e)
        {
            var selectedShares = GetCheckedShares(lstShareTabShares);
            bool showSystem = chkShowSystemTab2.IsChecked == true;
            bool includePureInherited = chkIncludeInheritedTab2.IsChecked == true;
            bool calcEffective = chkCalcEffectiveTab2.IsChecked == true;

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
                var rawResults = await Task.Run(() => PerformScan(new(), selectedShares, showSystem, includePureInherited, calcEffective, _cts.Token, progress));
                dgShareResults.ItemsSource = DeduplicateAndEvaluateRisks(rawResults);
                lblStatus.Text = $"查询完成，共找到 {dgShareResults.Items.Count} 条记录。";
            }
            catch (OperationCanceledException) { lblStatus.Text = "查询停止。"; }
            catch (Exception ex) { MessageBox.Show("查询出错: " + ex.Message); }
            finally { ToggleUI(true); }
        }

        private List<PermissionResult> PerformScan(List<string> targetUsers, List<ShareInfo> shares, bool showSystem, bool includePureInherited, bool calcEffective, CancellationToken token, IProgress<string> progress)
        {
            var results = new List<PermissionResult>();

            foreach (var share in shares)
            {
                token.ThrowIfCancellationRequested();
                progress.Report($"正在扫描共享: {share.Name}...");

                if (!Directory.Exists(share.Path)) continue;

                var smbShareRightsMap = calcEffective ? GetSmbShareAccessRights(share.Name) : new();

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

                int countBefore = results.Count;

                foreach (var folder in allFolders)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        bool isRoot = folder.TrimEnd('\\').Equals(share.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        var dirInfo = new DirectoryInfo(folder);
                        var acl = dirInfo.GetAccessControl(AccessControlSections.Access);
                        var rules = acl.GetAccessRules(true, true, typeof(NTAccount));

                        foreach (FileSystemAccessRule rule in rules)
                        {
                            if (!includePureInherited && !isRoot && rule.IsInherited)
                                continue;

                            string account = rule.IdentityReference.Value;
                            string shortAccount = account.Contains('\\') ? account.Split('\\')[1] : account;

                            if (!showSystem && IsSystemAccount(account))
                                continue;

                            if (targetUsers.Any() &&
                                !targetUsers.Contains(shortAccount, StringComparer.OrdinalIgnoreCase) &&
                                !targetUsers.Contains(account, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            FileSystemRights ntfsRights = rule.FileSystemRights;
                            FileSystemRights effectiveRights = ntfsRights;
                            string permType = "NTFS 权限";

                            // SMB ∩ NTFS 交集演算
                            if (calcEffective && smbShareRightsMap.Any())
                            {
                                var matchingSmbKey = smbShareRightsMap.Keys.FirstOrDefault(k => k.Equals(account, StringComparison.OrdinalIgnoreCase) || k.Equals("Everyone", StringComparison.OrdinalIgnoreCase));
                                if (matchingSmbKey != null)
                                {
                                    FileSystemRights smbRights = smbShareRightsMap[matchingSmbKey];
                                    effectiveRights = ntfsRights & smbRights;
                                    permType = "SMB ∩ NTFS";
                                }
                            }

                            var item = new PermissionResult
                            {
                                Account = account,
                                ShareName = share.Name,
                                Path = folder,
                                PermType = permType,
                                InheritanceStatus = rule.IsInherited ? "继承" : "直接",
                                AccessControlType = rule.AccessControlType.ToString()
                            };

                            // Deny 优先级处理
                            if (rule.AccessControlType == AccessControlType.Deny)
                            {
                                item.AccessControlType = "Deny";
                                item.RightsDescription = "[拒绝 Deny] " + effectiveRights;
                            }
                            else
                            {
                                ParseRights(effectiveRights, item);
                                item.RightsDescription = CleanRightsString(effectiveRights);
                            }

                            results.Add(item);
                        }
                    }
                    catch { }
                }

                if (results.Count == countBefore && !showSystem)
                {
                    results.Add(new PermissionResult
                    {
                        RiskBadge = "🟩 安全",
                        Account = "[仅系统/管理员默认权限]",
                        ShareName = share.Name,
                        Path = share.Path,
                        PermType = "NTFS 权限",
                        InheritanceStatus = "直接",
                        AccessControlType = "-",
                        RightsDescription = "默认控制 (无单独用户权限)"
                    });
                }
            }
            return results;
        }

        private static void ParseRights(FileSystemRights rights, PermissionResult result)
        {
            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                result.FullControl = true;
                result.Modify = true;
                result.Read = true;
                result.Write = true;
                result.Delete = true;
                return;
            }

            if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
            {
                result.Modify = true;
                result.Read = true;
                result.Write = true;
                result.Delete = true;
            }

            if ((rights & (FileSystemRights.ReadData | FileSystemRights.ReadAndExecute | FileSystemRights.Read)) != 0)
                result.Read = true;

            if ((rights & (FileSystemRights.WriteData | FileSystemRights.Write | FileSystemRights.AppendData)) != 0)
                result.Write = true;

            if ((rights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0)
                result.Delete = true;
        }

        private static string CleanRightsString(FileSystemRights rights)
        {
            string s = rights.ToString();
            if (s.Contains("FullControl")) return "FullControl";
            if (int.TryParse(s, out _) || s.StartsWith("-")) return "特殊扩展权限";
            return s;
        }

        private List<PermissionResult> DeduplicateAndEvaluateRisks(List<PermissionResult> raw)
        {
            return raw.GroupBy(r => new { r.Path, r.Account, r.PermType, r.InheritanceStatus, r.AccessControlType })
                .Select(g =>
                {
                    var first = g.First();
                    var item = new PermissionResult
                    {
                        Account = first.Account,
                        ShareName = first.ShareName,
                        Path = first.Path,
                        PermType = first.PermType,
                        InheritanceStatus = first.InheritanceStatus,
                        AccessControlType = first.AccessControlType,
                        Read = g.Any(x => x.Read),
                        Write = g.Any(x => x.Write),
                        Modify = g.Any(x => x.Modify),
                        Delete = g.Any(x => x.Delete),
                        FullControl = g.Any(x => x.FullControl),
                        RightsDescription = first.RightsDescription
                    };

                    string accUpper = item.Account.ToUpperInvariant();
                    if (item.AccessControlType == "Deny")
                    {
                        item.RiskBadge = "🟧 拒绝限制";
                    }
                    else if ((accUpper.Contains("EVERYONE") || accUpper.Contains("AUTHENTICATED USERS")) && (item.FullControl || item.Write || item.Modify))
                    {
                        item.RiskBadge = "🚩 高风险";
                        item.RightsDescription += " [警告: 广域账号拥有高权]";
                    }
                    else if (accUpper.StartsWith("S-1-5-"))
                    {
                        item.RiskBadge = "🟧 孤立SID";
                        item.RightsDescription += " [提示: 已删账号残留]";
                    }
                    else
                    {
                        item.RiskBadge = "🟩 安全";
                    }

                    return item;
                }).ToList();
        }

        private void ToggleUI(bool isEnabled)
        {
            btnSearchByUser.IsEnabled = isEnabled;
            btnSearchByShare.IsEnabled = isEnabled;
            btnCancelUser.IsEnabled = !isEnabled;
            btnCancelShare.IsEnabled = !isEnabled;
        }
        #endregion

        #region CSV 与 HTML 报告导出
        private void BtnExportUserCsv_Click(object sender, RoutedEventArgs e) => ExportCsv(dgUserResults, "按用户查询权限表");
        private void BtnExportShareCsv_Click(object sender, RoutedEventArgs e) => ExportCsv(dgShareResults, "按共享文件夹查询表");

        private void BtnExportUserHtml_Click(object sender, RoutedEventArgs e) => ExportHtml(dgUserResults);
        private void BtnExportShareHtml_Click(object sender, RoutedEventArgs e) => ExportHtml(dgShareResults);

        private void ExportCsv(DataGrid grid, string defaultFileName)
        {
            if (grid.ItemsSource is not IEnumerable<PermissionResult> items || !items.Any())
            {
                MessageBox.Show("没有可导出的数据！");
                return;
            }

            var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"{defaultFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (dialog.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine("安全风险,匹配账号,共享根名称,实际路径,权限来源,继承状态,访问控制,读取,写入,修改,删除,完全控制,权限描述");
                foreach (var item in items)
                {
                    sb.AppendLine($"\"{item.RiskBadge}\",\"{item.Account}\",\"{item.ShareName}\",\"{item.Path}\",\"{item.PermType}\",\"{item.InheritanceStatus}\",\"{item.AccessControlType}\",\"{item.ReadStr}\",\"{item.WriteStr}\",\"{item.ModifyStr}\",\"{item.DeleteStr}\",\"{item.FullControlStr}\",\"{item.RightsDescription}\"");
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show("CSV 导出成功！");
            }
        }

        private void ExportHtml(DataGrid grid)
        {
            if (grid.ItemsSource is not IEnumerable<PermissionResult> items || !items.Any())
            {
                MessageBox.Show("没有可导出的数据！");
                return;
            }

            var list = items.ToList();
            var dialog = new SaveFileDialog { Filter = "HTML 报告 (*.html)|*.html", FileName = $"权限安全审计报告_{DateTime.Now:yyyyMMdd_HHmmss}.html" };
            if (dialog.ShowDialog() == true)
            {
                int highRisks = list.Count(r => r.RiskBadge.Contains("高风险"));
                int mediumRisks = list.Count(r => r.RiskBadge.Contains("孤立") || r.RiskBadge.Contains("拒绝"));
                int safeCount = list.Count - highRisks - mediumRisks;

                var html = new StringBuilder();
                html.AppendLine("<!DOCTYPE html><html><head><meta charset='UTF-8'><title>Windows 权限安全审计报告</title>");
                html.AppendLine("<style>");
                html.AppendLine("body { font-family: 'Microsoft YaHei', Arial, sans-serif; margin: 30px; background-color: #f8f9fa; }");
                html.AppendLine(".header { background: #1e88e5; color: white; padding: 20px; border-radius: 8px; }");
                html.AppendLine(".card-container { display: flex; gap: 20px; margin: 20px 0; }");
                html.AppendLine(".card { background: white; padding: 20px; border-radius: 8px; flex: 1; box-shadow: 0 2px 4px rgba(0,0,0,0.1); text-align: center; }");
                html.AppendLine(".card h2 { margin: 0; font-size: 28px; }");
                html.AppendLine("table { width: 100%; border-collapse: collapse; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
                html.AppendLine("th, td { padding: 12px 15px; text-align: left; border-bottom: 1px solid #ddd; }");
                html.AppendLine("th { background-color: #f1f3f5; font-weight: bold; }");
                html.AppendLine(".high-risk { color: #d32f2f; font-weight: bold; }");
                html.AppendLine("</style></head><body>");

                html.AppendLine($"<div class='header'><h1>🛡️ Windows 共享与 NTFS 权限安全审计报告</h1><p>生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 主机/域名: {txtDomain.Text}</p></div>");

                html.AppendLine("<div class='card-container'>");
                html.AppendLine($"<div class='card'><p>总审计记录</p><h2>{list.Count}</h2></div>");
                html.AppendLine($"<div class='card'><p style='color:red;'>高风险项</p><h2 style='color:red;'>{highRisks}</h2></div>");
                html.AppendLine($"<div class='card'><p style='color:orange;'>中风险/警告</p><h2 style='color:orange;'>{mediumRisks}</h2></div>");
                html.AppendLine($"<div class='card'><p style='color:green;'>合规/安全</p><h2 style='color:green;'>{safeCount}</h2></div>");
                html.AppendLine("</div>");

                html.AppendLine("<h3>📋 详细权限审计清单</h3>");
                html.AppendLine("<table><thead><tr><th>安全风险</th><th>账号 / 用户组</th><th>共享名</th><th>实际路径</th><th>来源</th><th>继承</th><th>控制</th><th>读取</th><th>写入</th><th>修改</th><th>删除</th><th>全控</th><th>权限描述</th></tr></thead><tbody>");

                foreach (var item in list)
                {
                    string riskClass = item.RiskBadge.Contains("高风险") ? "class='high-risk'" : "";
                    html.AppendLine($"<tr {riskClass}><td>{item.RiskBadge}</td><td>{item.Account}</td><td>{item.ShareName}</td><td>{item.Path}</td><td>{item.PermType}</td><td>{item.InheritanceStatus}</td><td>{item.AccessControlType}</td><td>{item.ReadStr}</td><td>{item.WriteStr}</td><td>{item.ModifyStr}</td><td>{item.DeleteStr}</td><td>{item.FullControlStr}</td><td>{item.RightsDescription}</td></tr>");
                }

                html.AppendLine("</tbody></table></body></html>");

                File.WriteAllText(dialog.FileName, html.ToString(), Encoding.UTF8);
                MessageBox.Show("HTML 可视化报告导出成功！");
            }
        }
        #endregion
    }
}
