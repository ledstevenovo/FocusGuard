using System.Text;

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

    // hosts 是被安全软件重点关照的文件：会被瞬时独占扫描，也可能被长期占用；
    // 本应用自己的状态探测也在周期性读它。因此所有 IO 都按"共享冲突可退避重试"处理。
    // 写入总退避约 1.9 秒、读取约 1.4 秒 —— 操作期间按钮本来就显示"正在锁定/解除"，可接受。
    private static readonly int[] ReadRetryDelaysMs = { 150, 400, 800 };
    private static readonly int[] WriteRetryDelaysMs = { 120, 250, 500, 1000 };

    public bool IsActive() => File.Exists(HostsPath) && HostsFile.ContainsBlock(ReadAllBytesWithRetry(HostsPath));

    public void Apply(IEnumerable<string> domains)
    {
        if (!File.Exists(HostsPath))
        {
            throw new FileNotFoundException("找不到 hosts 文件：" + HostsPath, HostsPath);
        }

        var original = ReadAllBytesWithRetry(HostsPath);
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

        WritePreservingSecurity(updated, original);
    }

    public void Clear()
    {
        if (!File.Exists(HostsPath))
        {
            return;
        }

        var original = ReadAllBytesWithRetry(HostsPath);
        var updated = HostsFile.RemoveBlock(original);

        if (updated.AsSpan().SequenceEqual(original))
        {
            return;
        }

        WritePreservingSecurity(updated, original);
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

        var readBack = ReadAllBytesWithRetry(backupPath);
        if (!readBack.AsSpan().SequenceEqual(pristine))
        {
            throw new IOException("hosts 备份回读校验失败，已中止本次操作：" + backupPath);
        }
    }

    /// <summary>
    /// 写入并保留原文件的安全描述符（ACL）与创建时间。三层策略：
    /// <list type="number">
    /// <item><b>原子替换优先</b>（File.Replace）—— ACL、创建时间由系统整体保留；</item>
    /// <item><b>共享冲突退避重试</b> —— hosts 常被安全软件瞬时独占扫描，也容易被
    /// 只共享"读"而不共享"删除"的进程（含本应用自己的读）挡住，重试可穿越瞬时冲突；</item>
    /// <item><b>重试耗尽后就地写入兜底</b> —— 不换文件对象，ACL 与创建时间天然保留；
    /// 代价是失去原子性，因此写前先校验 hosts 仍是当初读到的内容，绝不覆盖别人的修改。
    /// 注意：临时文件都写不出来（D4 破坏性场景）不算共享冲突，仍然直接报错，不走兜底。</item>
    /// </list>
    /// </summary>
    private void WritePreservingSecurity(byte[] content, byte[] expectedCurrent)
    {
        var temp = HostsPath + ".focusguard.tmp";

        try
        {
            try
            {
                File.WriteAllBytes(temp, content);
            }
            catch (Exception ex)
            {
                // 没能写出临时文件就没有任何退路：保持"明确报错、不留半成品"的原语义
                throw new IOException(
                    "hosts 写入失败（为保留原有权限，未采用会丢权限的覆盖方式）：" + ex.Message, ex);
            }

            try
            {
                ReplaceWithRetry(temp);
            }
            catch (Exception ex) when (IsSharingViolation(ex))
            {
                AppPaths.Log("hosts 原子替换重试后仍被占用，改试就地写入：" + ex.Message);

                try
                {
                    WriteInPlaceAfterVerify(content, expectedCurrent);
                }
                catch (Exception fallbackEx)
                {
                    throw new IOException(
                        "hosts 写入失败（为保留原有权限，未采用会丢权限的覆盖方式）：" +
                        "重试后仍被其他程序占用，就地写入也未成功。常见原因是安全软件正在扫描或锁定 hosts，" +
                        "可稍后重试；若持续失败请检查安全软件的 hosts 防护设置。" +
                        $"替换阶段：{ex.Message}；就地写入阶段：{fallbackEx.Message}", fallbackEx);
                }
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "hosts 写入失败（为保留原有权限，未采用会丢权限的覆盖方式）：" + ex.Message, ex);
            }
        }
        finally
        {
            // Replace 成功后 temp 已随替换消失，Delete 是无害兜底；失败时清掉半成品
            try { File.Delete(temp); } catch { }
        }

        // 写完回读校验，确认真的落盘（替换与就地写入两条路径共用）
        var actual = ReadAllBytesWithRetry(HostsPath);
        if (!actual.AsSpan().SequenceEqual(content))
        {
            throw new IOException("hosts 写入后回读校验失败，内容与预期不一致：" + HostsPath);
        }
    }

    private void ReplaceWithRetry(string temp)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // 原子替换：保留被替换文件的 ACL、创建时间等身份信息。
                // 不使用 File.Move / 删除后重建 —— 那些会把 hosts 的权限重置为继承父目录。
                File.Replace(temp, HostsPath, destinationBackupFileName: null);
                return;
            }
            catch (IOException ex) when (attempt <= WriteRetryDelaysMs.Length && IsSharingViolation(ex))
            {
                Thread.Sleep(WriteRetryDelaysMs[attempt - 1]);
            }
        }
    }

    /// <summary>
    /// 就地写入兜底：只申请"写"访问，不要求删除 —— 那些开着 hosts 却不共享"删除"的进程
    /// （内容监视、同步工具、部分安全软件）挡得住 File.Replace，挡不住这里。
    /// 写前必须确认内容仍是本次更新所依据的那份，期间被别人改过就宁可失败也不覆盖。
    /// </summary>
    private void WriteInPlaceAfterVerify(byte[] content, byte[] expectedCurrent)
    {
        var current = ReadAllBytesWithRetry(HostsPath);
        if (!current.AsSpan().SequenceEqual(expectedCurrent))
        {
            throw new IOException("hosts 在写入前又被其他程序修改过，为避免覆盖已中止，请直接重试。");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    HostsPath, FileMode.Open, FileAccess.Write,
                    FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
                stream.SetLength(0);
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
                return;
            }
            catch (IOException ex) when (attempt <= WriteRetryDelaysMs.Length && IsSharingViolation(ex))
            {
                Thread.Sleep(WriteRetryDelaysMs[attempt - 1]);
            }
        }
    }

    /// <summary>
    /// 以"最宽共享"方式读文件，并对共享冲突做短暂退避重试。
    /// File.ReadAllBytes 默认只声明 FILE_SHARE_READ —— 会挡住任何并发的 File.Replace
    /// （替换需要以"写+删除"方式打开目标文件）。本应用每秒都在探测 hosts 状态，
    /// 用默认共享方式读就会和自己的写入撞车（2026-09-18 实测复现）。
    /// </summary>
    private static byte[] ReadAllBytesWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    bufferSize: 4096, FileOptions.SequentialScan);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (IOException ex) when (attempt <= ReadRetryDelaysMs.Length && IsSharingViolation(ex))
            {
                Thread.Sleep(ReadRetryDelaysMs[attempt - 1]);
            }
        }
    }

    /// <summary>共享冲突（32）与锁冲突（33）；Win32 错误码在 HResult 低 16 位。</summary>
    private static bool IsSharingViolation(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33;

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
