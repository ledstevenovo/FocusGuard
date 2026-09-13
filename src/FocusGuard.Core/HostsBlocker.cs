namespace FocusGuard.Core;

/// <summary>读写真实 hosts 文件。构造时传入路径以便在测试里指向临时文件。</summary>
public sealed class HostsBlocker
{
    private readonly Func<DateTime> _clock;

    public HostsBlocker(string? hostsPath = null, Func<DateTime>? clock = null)
    {
        HostsPath = hostsPath ?? DefaultHostsPath;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string HostsPath { get; }

    /// <summary>C:\Windows\System32\drivers\etc\hosts</summary>
    public static string DefaultHostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public string BackupDirectory => Path.GetDirectoryName(HostsPath) ?? ".";

    public bool IsActive() => File.Exists(HostsPath) && HostsFile.ContainsBlock(File.ReadAllBytes(HostsPath));

    public void Apply(IEnumerable<string> domains)
    {
        if (!File.Exists(HostsPath))
        {
            throw new FileNotFoundException("找不到 hosts 文件：" + HostsPath, HostsPath);
        }

        var original = File.ReadAllBytes(HostsPath);
        var updated = HostsFile.AddBlock(original, domains);

        if (updated.AsSpan().SequenceEqual(original))
        {
            return; // 内容没变化，不去碰文件
        }

        // 只备份"还没被我们改过"的原始状态：
        // 这样备份永远是可用的原始副本，不会被我们自己写入的区块污染，也不会过期。
        if (!HostsFile.ContainsBlock(original))
        {
            CreateVerifiedBackup(original);
        }

        WritePreservingSecurity(updated);
    }

    public void Clear()
    {
        if (!File.Exists(HostsPath))
        {
            return;
        }

        var original = File.ReadAllBytes(HostsPath);
        var updated = HostsFile.RemoveBlock(original);

        if (updated.AsSpan().SequenceEqual(original))
        {
            return;
        }

        WritePreservingSecurity(updated);
    }

    /// <summary>
    /// 备份并<b>回读校验</b>；校验不过就抛异常中止本次操作，绝不静默继续 ——
    /// 否则等到真需要手工恢复时才发现备份不可用就晚了。
    /// </summary>
    private void CreateVerifiedBackup(byte[] pristine)
    {
        var backupPath = Path.Combine(
            BackupDirectory,
            $"{Path.GetFileName(HostsPath)}.focusguard-{_clock():yyyyMMdd-HHmmss}.bak");

        File.WriteAllBytes(backupPath, pristine);

        var readBack = File.ReadAllBytes(backupPath);
        if (!readBack.AsSpan().SequenceEqual(pristine))
        {
            throw new IOException("hosts 备份回读校验失败，已中止本次操作：" + backupPath);
        }
    }

    /// <summary>
    /// 原子替换，同时保留被替换文件的安全描述符（ACL）与创建时间。
    /// 若直接删除后重建，hosts 的权限会被重置为继承父目录，丢掉用户原有的自定义权限。
    /// </summary>
    private void WritePreservingSecurity(byte[] content)
    {
        var temp = HostsPath + ".focusguard.tmp";

        try
        {
            File.WriteAllBytes(temp, content);
            // 原子替换：保留被替换文件的 ACL、创建时间等身份信息。
            // 不使用 File.Move / 删除后重建 —— 那些会把 hosts 的权限重置为继承父目录。
            File.Replace(temp, HostsPath, destinationBackupFileName: null);
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { }
            throw new IOException(
                "hosts 写入失败（为保留原有权限，未采用会丢权限的覆盖方式）：" + ex.Message, ex);
        }

        // 替换后回读校验目标内容，确认真的写进去了
        var actual = File.ReadAllBytes(HostsPath);
        if (!actual.AsSpan().SequenceEqual(content))
        {
            throw new IOException("hosts 写入后回读校验失败，内容与预期不一致：" + HostsPath);
        }
    }

    public IReadOnlyList<string> ListBackups()
    {
        try
        {
            var pattern = Path.GetFileName(HostsPath) + ".focusguard-*.bak";
            return Directory.GetFiles(BackupDirectory, pattern)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static CommandResult FlushDns() => CommandRunner.Run("ipconfig", "/flushdns");
}
