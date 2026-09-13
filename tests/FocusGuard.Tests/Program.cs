using System.Text;
using FocusGuard.Core;

int passed = 0;
var failures = new List<string>();

void Check(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine("  PASS  " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + "  ->  " + ex.Message);
        Console.WriteLine("  FAIL  " + name);
        Console.WriteLine("        " + ex.Message);
    }
}

void AssertTrue(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{message}（期望 [{expected}]，实际 [{actual}]）");
}

void AssertBytesEqual(byte[] expected, byte[] actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new Exception($"{message}（期望 {expected.Length} 字节，实际 {actual.Length} 字节）");
}

void AssertThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (Exception ex)
    {
        if (ex is T) return;
        throw new Exception($"{message}（抛出的是 {ex.GetType().Name} 而非 {typeof(T).Name}）");
    }
    throw new Exception(message + "（没有抛出异常）");
}

// --ci：跳过依赖本机环境（真实 hosts / 真实 fm.exe）的 H 组检查，供 CI 流水线使用
bool ciMode = args.Contains("--ci");

int CountOccurrences(string text, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }
    return count;
}

string NewTempDir(string tag)
{
    var dir = Path.Combine(Path.GetTempPath(), "focusguard-test-" + tag + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

void Nuke(string dir)
{
    try
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(dir, true);
    }
    catch { }
}

/// <summary>完整的 ACL 显示输出（去掉尾部统计行）—— 比较完整权限，而不是只看第一行。</summary>
string AclFull(string path)
{
    var result = CommandRunner.Run("icacls", path);
    return string.Join("\n", result.StdOut
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .Where(l => !l.Contains("已成功处理") && !l.Contains("失败") && !l.Contains("Successfully")));
}

/// <summary>让 hosts 的临时文件写入必然失败：在临时文件路径上放一个同名目录。</summary>
void BreakHostsWrite(string hostsPath) => Directory.CreateDirectory(hostsPath + ".focusguard.tmp");

void UnbreakHostsWrite(string hostsPath)
{
    try { Directory.Delete(hostsPath + ".focusguard.tmp"); } catch { }
}

byte[] HostsBytes(string text) => Encoding.ASCII.GetBytes(text);

const string PristineHosts = "# original\r\n127.0.0.1 localhost\r\n";

(FocusConfig Config, string HostsPath, string Target) SetupCycle(string dir)
{
    var hostsPath = Path.Combine(dir, "hosts");
    var target = Path.Combine(dir, "fm.exe");
    File.WriteAllBytes(hostsPath, HostsBytes(PristineHosts));
    File.WriteAllText(target, "game-binary");
    return (new FocusConfig { TargetExe = target }, hostsPath, target);
}

Console.WriteLine("== A. hosts 区块纯逻辑 ==");

Check("A1 区块含 BEGIN/END 标记，且每个域名同时写 IPv4 与 IPv6", () =>
{
    var text = Encoding.UTF8.GetString(HostsFile.AddBlock(Array.Empty<byte>(), new[] { "zhihu.com", "HUPU.com " }));
    AssertTrue(text.Contains(HostsFile.BeginMarker), "缺少 BEGIN 标记");
    AssertTrue(text.Contains(HostsFile.EndMarker), "缺少 END 标记");
    AssertTrue(text.Contains("0.0.0.0 zhihu.com"), "缺少 zhihu.com 的 IPv4 行");
    AssertTrue(text.Contains("::1 zhihu.com"), "缺少 zhihu.com 的 IPv6 行");
    AssertTrue(text.Contains("0.0.0.0 hupu.com"), "域名未做小写规范化");
});

Check("A2 原有字节一个都没被改动（含 GBK 编码的中文注释）", () =>
{
    var original = new byte[]
    {
        0x23, 0x20, 0xD6, 0xD0, 0xCE, 0xC4, 0x0D, 0x0A,
        0x31, 0x32, 0x37, 0x2E, 0x30, 0x2E, 0x30, 0x2E, 0x31, 0x20, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x0D, 0x0A,
    };
    var updated = HostsFile.AddBlock(original, new[] { "zhihu.com" });
    AssertTrue(updated.Length > original.Length, "未追加屏蔽区块");
    AssertBytesEqual(original, updated.Take(original.Length).ToArray(), "原有内容被改动");
});

Check("A3 重复写入是幂等的（不会出现两个区块）", () =>
{
    var once = HostsFile.AddBlock(Array.Empty<byte>(), new[] { "zhihu.com" });
    var twice = HostsFile.AddBlock(once, new[] { "zhihu.com" });
    AssertEqual(1, CountOccurrences(Encoding.UTF8.GetString(twice), HostsFile.BeginMarker), "出现了多个 BEGIN 标记");
    AssertBytesEqual(once, twice, "重复写入结果不一致");
});

Check("A4 原文件以换行结尾（真实 hosts 的情况）→ 还原字节完全一致", () =>
{
    var original = HostsBytes("# Copyright\r\n127.0.0.1 localhost\r\n::1 localhost\r\n");
    var restored = HostsFile.RemoveBlock(HostsFile.AddBlock(original, new[] { "zhihu.com", "bilibili.com" }));
    AssertBytesEqual(original, restored, "还原后与原始内容不一致");
});

Check("A5 原文件不以换行结尾 → 还原时仅多一个尾换行（已知且无害的归一化）", () =>
{
    const string text = "# no trailing newline";
    var restored = HostsFile.RemoveBlock(HostsFile.AddBlock(HostsBytes(text), new[] { "zhihu.com" }));
    AssertEqual(text + "\r\n", Encoding.ASCII.GetString(restored), "归一化结果不符合预期");
});

Check("A6 区块位于文件中间时也能干净删除", () =>
{
    var block = Encoding.ASCII.GetString(HostsFile.BuildBlock(new[] { "zhihu.com" }));
    var restored = HostsFile.RemoveBlock(HostsBytes("A-line\r\n" + block + "B-line\r\n"));
    AssertEqual("A-line\r\nB-line\r\n", Encoding.ASCII.GetString(restored), "中间区块删除不干净");
});

Check("A7 没有区块时 RemoveBlock 原样返回", () =>
{
    var original = HostsBytes("127.0.0.1 localhost\r\n");
    AssertBytesEqual(original, HostsFile.RemoveBlock(original), "无区块时内容被改动");
});

Check("A8 域名规范化：去重 / 转小写 / 剔除非法项 / 稳定排序", () =>
{
    var normalized = HostsFile.Normalize(new[] { " Zhihu.com", "zhihu.com", "", "  ", "a b.com", "x#y.com", "B.com" });
    AssertEqual(2, normalized.Count, "规范化后的域名数量不对");
    AssertEqual("b.com", normalized[0], "排序或小写不正确");
    AssertEqual("zhihu.com", normalized[1], "去重失败");
});

Console.WriteLine();
Console.WriteLine("== B. hosts 文件读写往返 ==");

Check("B1 Apply → IsActive → Clear 后与原始文件字节一致", () =>
{
    var dir = NewTempDir("hosts");
    try
    {
        var path = Path.Combine(dir, "hosts");
        var original = HostsBytes(PristineHosts);
        File.WriteAllBytes(path, original);

        var blocker = new HostsBlocker(path);
        AssertTrue(!blocker.IsActive(), "初始状态不应包含区块");

        blocker.Apply(FocusConfig.DefaultDomains());
        AssertTrue(blocker.IsActive(), "Apply 之后区块不存在");
        var text = File.ReadAllText(path);
        AssertTrue(text.Contains("0.0.0.0 www.zhihu.com"), "屏蔽内容未写入");
        AssertTrue(text.Contains("::1 www.bilibili.com"), "缺少 IPv6 行");

        blocker.Clear();
        AssertTrue(!blocker.IsActive(), "Clear 之后区块仍存在");
        AssertBytesEqual(original, File.ReadAllBytes(path), "还原后与原始内容不一致");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== C. TargetLocker（改名拦截，固定后缀 + 不通配 + 冲突即拒绝） ==");

Check("C1 锁定后原文件消失、改名文件出现；解锁后内容逐字节一致", () =>
{
    var dir = NewTempDir("lock");
    try
    {
        var target = Path.Combine(dir, "fm.exe");
        var content = Encoding.ASCII.GetBytes("game-binary");
        File.WriteAllBytes(target, content);

        var locker = new TargetLocker(target);
        AssertEqual(LockState.Unlocked, locker.State, "初始状态不对");

        locker.Lock();
        AssertEqual(LockState.Locked, locker.State, "锁定后状态不对");

        locker.Unlock();
        AssertEqual(LockState.Unlocked, locker.State, "解锁后状态不对");
        AssertBytesEqual(content, File.ReadAllBytes(target), "解锁后内容不一致");
    }
    finally { Nuke(dir); }
});

Check("C2 Lock / Unlock 都是幂等的", () =>
{
    var dir = NewTempDir("lock-idem");
    try
    {
        var target = Path.Combine(dir, "fm.exe");
        File.WriteAllText(target, "game");
        var locker = new TargetLocker(target);

        locker.Unlock();
        locker.Lock();
        locker.Lock();
        AssertTrue(locker.IsLocked, "重复加锁后未处于锁定态");
        locker.Unlock();
        locker.Unlock();
        AssertTrue(File.Exists(target), "重复解锁后原文件未恢复");
    }
    finally { Nuke(dir); }
});

Check("C3 目标不存在时 Lock 抛异常（不静默成功）", () =>
{
    var dir = NewTempDir("lock-missing");
    try
    {
        var threw = false;
        try { new TargetLocker(Path.Combine(dir, "nope.exe")).Lock(); }
        catch (FileNotFoundException) { threw = true; }
        AssertTrue(threw, "目标不存在时未抛出 FileNotFoundException");
    }
    finally { Nuke(dir); }
});

Check("C4 加锁目标已存在时拒绝操作，且不覆盖、不删除任何文件（核心）", () =>
{
    var dir = NewTempDir("lock-conflict");
    try
    {
        var target = Path.Combine(dir, "fm.exe");
        File.WriteAllText(target, "game");
        var locker = new TargetLocker(target);
        locker.Lock();

        File.WriteAllText(target, "regenerated-by-game-updater");   // 更新程序又生成了一份

        AssertEqual(LockState.Conflict, locker.State, "未识别出冲突态");

        var threw = false;
        try { locker.Lock(); }
        catch (InvalidOperationException ex) { threw = ex.Message.Contains("拒绝本次操作"); }
        AssertTrue(threw, "冲突时加锁未拒绝");

        AssertEqual("regenerated-by-game-updater", File.ReadAllText(target), "原文件被覆盖了");
        AssertEqual("game", File.ReadAllText(locker.LockedPath), "锁定文件被覆盖或删除了");
    }
    finally { Nuke(dir); }
});

Check("C5 冲突时 Unlock 拒绝操作，两份文件都保留", () =>
{
    var dir = NewTempDir("unlock-conflict");
    try
    {
        var target = Path.Combine(dir, "fm.exe");
        File.WriteAllText(target, "game");
        var locker = new TargetLocker(target);
        locker.Lock();
        File.WriteAllText(target, "user-put-this-back");

        var threw = false;
        try { locker.Unlock(); }
        catch (InvalidOperationException ex) { threw = ex.Message.Contains("未做任何改动"); }
        AssertTrue(threw, "冲突时解锁未拒绝");

        AssertEqual("user-put-this-back", File.ReadAllText(target), "原文件被改动");
        AssertEqual("game", File.ReadAllText(locker.LockedPath), "锁定文件被删除或覆盖");
    }
    finally { Nuke(dir); }
});

Check("C6 只认固定后缀名，不做通配匹配", () =>
{
    var dir = NewTempDir("lock-exact");
    try
    {
        var target = Path.Combine(dir, "fm.exe");
        File.WriteAllText(target, "game");

        // 同名但后缀不同的文件不应被当作锁定文件
        File.WriteAllText(target + ".focusguard-locked.old", "stale");
        File.WriteAllText(Path.Combine(dir, "other.focusguard-locked"), "other");

        var locker = new TargetLocker(target);
        AssertEqual(LockState.Unlocked, locker.State, "把无关文件误判为锁定文件");
        AssertEqual(target + TargetLocker.LockedSuffix, locker.LockedPath, "锁定路径不是固定后缀");
        AssertTrue(!File.Exists(target + ".focusguard-locked.old.focusguard-locked"), "产生了错误的目标路径");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== D. hosts 的 ACL 保留、备份与失败处理 ==");

Check("D1 写入 hosts 不会改变它原有权限，也不重建文件", () =>
{
    var dir = NewTempDir("acl-keep");
    try
    {
        var path = Path.Combine(dir, "hosts");
        File.WriteAllBytes(path, HostsBytes(PristineHosts));

        // 给 hosts 一套独特的权限：断开继承 + 只给当前用户完全控制
        CommandRunner.Run("icacls", path, "/inheritance:d");
        CommandRunner.Run("icacls", path, "/grant:r", Environment.UserName + ":(F)");

        var aclBefore = AclFull(path);
        var daclBefore = AclReader.ReadDacl(path);
        var createdBefore = File.GetCreationTimeUtc(path);
        AssertTrue(aclBefore.Contains(Environment.UserName), "测试前置条件不成立");
        AssertTrue(!string.IsNullOrWhiteSpace(daclBefore), "未能读到 hosts 的 DACL");

        var blocker = new HostsBlocker(path);
        blocker.Apply(new[] { "zhihu.com" });
        AssertEqual(aclBefore, AclFull(path), "屏蔽期间 hosts 的完整 ACL 显示输出被改变");
        AssertEqual(daclBefore, AclReader.ReadDacl(path), "屏蔽期间 hosts 的 DACL 被改变");
        blocker.Clear();

        AssertEqual(aclBefore, AclFull(path), "hosts 的完整 ACL 显示输出在写入过程中被改变");
        AssertEqual(daclBefore, AclReader.ReadDacl(path), "hosts 的 DACL 在写入过程中被改变");
        AssertEqual(createdBefore, File.GetCreationTimeUtc(path), "hosts 被重建了（创建时间变了）");
        Console.WriteLine("        （比较对象：icacls 完整输出 + icacls /save 的 DACL；不含所有者与 SACL）");
    }
    finally { Nuke(dir); }
});

Check("D2 备份是原始内容、可重复校验、不会被自己的区块污染", () =>
{
    var dir = NewTempDir("backup");
    try
    {
        var path = Path.Combine(dir, "hosts");
        var pristine = HostsBytes(PristineHosts);
        File.WriteAllBytes(path, pristine);

        var blocker = new HostsBlocker(path, () => new DateTime(2026, 9, 13, 20, 6, 12));
        blocker.Apply(new[] { "zhihu.com" });

        var backups = blocker.ListBackups();
        AssertEqual(1, backups.Count, "应生成 1 份备份");
        AssertBytesEqual(pristine, File.ReadAllBytes(backups[0]), "备份内容不是本次读到的原文件");

        blocker.Apply(new[] { "zhihu.com" });
        AssertEqual(1, blocker.ListBackups().Count, "无变化时不应新增备份");

        blocker.Apply(new[] { "zhihu.com", "hupu.com" });
        var again = blocker.ListBackups();
        AssertEqual(1, again.Count, "重复 Apply 不应堆积备份");
        AssertBytesEqual(pristine, File.ReadAllBytes(again[0]), "备份被我们自己写入的区块污染了");
    }
    finally { Nuke(dir); }
});

Check("D3 hosts 不存在时 Apply 抛异常，且不创建文件", () =>
{
    var dir = NewTempDir("no-hosts");
    try
    {
        var missing = Path.Combine(dir, "no-such-hosts");
        var threw = false;
        try { new HostsBlocker(missing).Apply(new[] { "zhihu.com" }); }
        catch (FileNotFoundException) { threw = true; }

        AssertTrue(threw, "hosts 不存在时未抛异常");
        AssertTrue(!File.Exists(missing), "失败后却创建了文件");
    }
    finally { Nuke(dir); }
});

Check("D4 写入失败时明确报错，不静默继续、也不留临时文件", () =>
{
    var dir = NewTempDir("write-fail");
    try
    {
        var path = Path.Combine(dir, "hosts");
        var pristine = HostsBytes(PristineHosts);
        File.WriteAllBytes(path, pristine);
        BreakHostsWrite(path);   // 让临时文件写入必然失败

        var blocker = new HostsBlocker(path);
        var threw = false;
        try { blocker.Apply(new[] { "zhihu.com" }); }
        catch (IOException) { threw = true; }

        AssertTrue(threw, "写入失败时未抛出异常");
        AssertBytesEqual(pristine, File.ReadAllBytes(path), "失败后 hosts 被改动了");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== E. 开始 / 结束的完整流程与失败语义 ==");

Check("E1 开始成功 → 结束成功 → 全部还原", () =>
{
    var dir = NewTempDir("cycle");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));

        var start = controller.Start();
        AssertTrue(start.Success, "开始失败：" + start.FailureText);
        AssertTrue(File.Exists(target + TargetLocker.LockedSuffix), "目标未被锁定");
        AssertTrue(File.Exists(target) == false, "原文件仍然存在");

        var stop = controller.Stop();
        AssertTrue(stop.Success, "结束失败：" + stop.FailureText);
        AssertTrue(File.Exists(target), "目标未恢复原名");
        AssertTrue(!new HostsBlocker(hostsPath).IsActive(), "hosts 区块仍在");
        AssertTrue(controller.UnresolvedItems.Count == 0, "仍有未恢复项");
    }
    finally { Nuke(dir); }
});

Check("E2 解除失败时不谎报成功，失败项被保留（核心）", () =>
{
    var dir = NewTempDir("stop-fail");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");

        BreakHostsWrite(hostsPath);   // 只让 hosts 的解除失败，文件改名应能成功

        var stop = controller.Stop();
        AssertTrue(!stop.Success, "解除明明失败了，却报告成功");
        AssertTrue(controller.UnresolvedItems.Any(u => u.Contains("hosts")), "未保留失败的项");
        AssertTrue(controller.HostsBlockActive, "hosts 区块实际仍在");

        // 成功的部分不应被失败拖累：原文件已经恢复
        AssertTrue(File.Exists(target), "文件改名这一步本应成功却没成功");
        AssertTrue(controller.IsLockedNow, "还有未恢复项时界面不应显示为已解锁");
    }
    finally { Nuke(dir); }
});

Check("E3 一项失败后，处理完原因可重试成功", () =>
{
    var dir = NewTempDir("stop-retry");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");
        BreakHostsWrite(hostsPath);
        AssertTrue(!controller.Stop().Success, "前置的失败场景没有复现");

        UnbreakHostsWrite(hostsPath);

        var retry = controller.Stop();
        AssertTrue(retry.Success, "重试仍失败：" + retry.FailureText);
        AssertTrue(controller.UnresolvedItems.Count == 0, "重试后仍有未恢复项");
        AssertTrue(!controller.IsLockedNow, "重试后仍处于锁定态");
        AssertTrue(!new HostsBlocker(hostsPath).IsActive(), "hosts 区块仍在");
    }
    finally { Nuke(dir); }
});

Check("E4 配置被改动（换路径 + 关开关）后仍能解锁原目标", () =>
{
    var dir = NewTempDir("cfg-a");
    var otherDir = NewTempDir("cfg-b");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var first = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(first.Start().Success, "前置的开始操作失败");

        var changed = new FocusConfig
        {
            TargetExe = Path.Combine(otherDir, "other.exe"),
            LockTargetExe = false,
        };
        var second = new FocusController(changed, dir, new HostsBlocker(hostsPath));

        var stop = second.Stop();
        AssertTrue(stop.Success, "结束失败：" + stop.FailureText);
        AssertTrue(File.Exists(target), "配置改动后未能解锁原目标");
    }
    finally { Nuke(dir); Nuke(otherDir); }
});

Check("E5 状态文件丢失后仍能发现残留并清除", () =>
{
    var dir = NewTempDir("lost-state");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        AssertTrue(new FocusController(config, dir, new HostsBlocker(hostsPath)).Start().Success, "前置的开始操作失败");

        File.Delete(Path.Combine(dir, "state.json"));

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.IsLockedNow, "状态文件丢失后未能识别残留");

        var stop = controller.Stop();
        AssertTrue(stop.Success, "结束失败：" + stop.FailureText);
        AssertTrue(File.Exists(target), "目标未恢复原名");
        AssertTrue(!controller.IsLockedNow, "残留未被清除");
    }
    finally { Nuke(dir); }
});

Check("E6 并发解除不会互相破坏（控制器内部串行化）", () =>
{
    var dir = NewTempDir("concurrent");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");

        var results = new OperationResult?[4];
        Parallel.For(0, 4, i => results[i] = controller.Stop());

        AssertTrue(results.All(r => r is not null && r.Success), "并发解除出现失败");
        AssertTrue(!controller.IsLockedNow, "并发解除后仍有残留");
        AssertTrue(File.Exists(target), "目标未恢复原名");
    }
    finally { Nuke(dir); }
});

Check("E7 目标不存在时文案与真实状态一致，不谎称已锁定", () =>
{
    var dir = NewTempDir("describe");
    try
    {
        var hostsPath = Path.Combine(dir, "hosts");
        File.WriteAllBytes(hostsPath, HostsBytes(PristineHosts));
        var controller = new FocusController(
            new FocusConfig { TargetExe = Path.Combine(dir, "missing.exe") }, dir, new HostsBlocker(hostsPath));

        var start = controller.Start();
        AssertTrue(start.Success, "开始失败：" + start.FailureText);
        AssertTrue(start.Steps.Any(s => s.Status == StepStatus.Skipped), "目标不存在时未如实标记为跳过");
        AssertTrue(!controller.TargetLockActive, "目标不存在却报告已锁定");

        var summary = controller.EnforcementSummary();
        AssertTrue(summary.Any(l => l.Contains("原文件不存在")), "界面文案与实际不符：" + string.Join(" / ", summary));
        AssertTrue(summary.Any(l => l.Contains("已写入 hosts")), "未说明网站规则已写入");
        AssertTrue(!summary.Any(l => l.Contains("已阻止")), "用了无法验证的强断言措辞");

        controller.Stop();
    }
    finally { Nuke(dir); }
});

Check("E8 崩溃在『已改名、未写确认』→ 新实例仍能恢复", () =>
{
    var dir = NewTempDir("crash-after-rename");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var locked = target + TargetLocker.LockedSuffix;

        // 崩溃现场：意图已落盘（Phase=Locking），改名已发生，确认结果没写
        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Locking,
            RecordedTargetPath = target,
            RecordedLockedPath = locked,
            RecordedHostsPath = hostsPath,
        }.Save(Path.Combine(dir, "state.json"));
        File.Move(target, locked);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var stop = controller.Stop();
        AssertTrue(stop.Success, "恢复失败：" + stop.FailureText);
        AssertTrue(File.Exists(target), "原文件未恢复");
        AssertTrue(!File.Exists(locked), "锁定文件仍在");
    }
    finally { Nuke(dir); }
});

Check("E9 崩溃在『写了意图、还没改名』→ 判定为已解决，不误报无法定位", () =>
{
    var dir = NewTempDir("crash-before-rename");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var locked = target + TargetLocker.LockedSuffix;

        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Locking,
            RecordedTargetPath = target,
            RecordedLockedPath = locked,
            RecordedHostsPath = hostsPath,
        }.Save(Path.Combine(dir, "state.json"));
        // 故意不改名，模拟崩溃

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var stop = controller.Stop();

        AssertTrue(File.Exists(target), "不该动原文件");
        AssertTrue(!stop.Steps.Any(s => s.Name == "定位残留" && s.Status == StepStatus.Warning),
            "把『已恢复』误报成了『无法定位』");
        AssertTrue(stop.Steps.Any(s => s.Name == "定位残留" && s.Status == StepStatus.Skipped),
            "没有报告目标已确认回到正常状态");
        AssertTrue(controller.UnlocatedRecord is null, "对已恢复正常的目标仍报告未定位");
        AssertTrue(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath is null,
            "已确认解决后记录线索未被清空");
    }
    finally { Nuke(dir); }
});

Check("E10 状态丢失 + 配置路径改变 → 明确报告无法定位，并可指定目录补救", () =>
{
    var dirA = NewTempDir("lost-a");
    var dirB = NewTempDir("lost-b");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dirA);
        AssertTrue(new FocusController(config, dirA, new HostsBlocker(hostsPath)).Start().Success, "前置的开始操作失败");
        File.Delete(Path.Combine(dirA, "state.json"));

        // 配置被改到完全不同的目录，按配置再也找不到原来的目标
        var moved = new FocusController(
            new FocusConfig { TargetExe = Path.Combine(dirB, "fm.exe") }, dirA, new HostsBlocker(hostsPath));

        var stop = moved.Stop();
        AssertTrue(stop.Warnings.Any(w => (w.Detail ?? string.Empty).Contains("--unlock")),
            "没有提示可以指定目录补救");

        // 按用户指定目录补救
        var remedy = moved.UnlockInDirectory(dirA);
        AssertTrue(remedy.Success, "按目录恢复失败：" + remedy.FailureText);
        AssertTrue(File.Exists(target), "按目录恢复没有还原文件");
    }
    finally { Nuke(dirA); Nuke(dirB); }
});

Check("E11 游戏更新重新生成 fm.exe → 报告冲突，两份文件都不动", () =>
{
    var dir = NewTempDir("regen");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var locked = target + TargetLocker.LockedSuffix;
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");

        File.WriteAllText(target, "regenerated");   // 更新程序/修复工具重新生成了 fm.exe

        var stop = controller.Stop();
        AssertTrue(!stop.Success, "冲突时却报告解除成功");
        AssertTrue(controller.UnresolvedItems.Any(u => u.Contains("冲突")), "未把冲突项保留下来");

        AssertEqual("regenerated", File.ReadAllText(target), "原文件被覆盖或删除");
        AssertEqual("game-binary", File.ReadAllText(locked), "锁定文件被覆盖或删除");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== F. 配置与状态 ==");

Check("F1 默认配置覆盖四个站点，且包含关键子域", () =>
{
    var domains = HostsFile.Normalize(FocusConfig.DefaultDomains());
    foreach (var required in new[]
             {
                 "zhihu.com", "www.zhihu.com", "zhuanlan.zhihu.com",
                 "hupu.com", "www.hupu.com",
                 "youtube.com", "www.youtube.com", "youtu.be",
                 "bilibili.com", "www.bilibili.com", "m.bilibili.com", "b23.tv",
             })
    {
        AssertTrue(domains.Contains(required), "默认配置缺少 " + required);
    }
});

Check("F2 配置 JSON 往返不丢字段（含补充恢复目录）", () =>
{
    var dir = NewTempDir("cfg");
    try
    {
        var path = Path.Combine(dir, "config.json");
        new FocusConfig
        {
            BlockedDomains = new List<string> { "example.com" },
            TargetExe = @"C:\tmp\x.exe",
            LockTargetExe = false,
            RecoveryDirectories = new List<string> { @"D:\games" },
        }.Save(path);

        var loaded = FocusConfig.LoadOrCreate(path);
        AssertEqual("example.com", loaded.BlockedDomains[0], "域名内容不对");
        AssertEqual(@"C:\tmp\x.exe", loaded.TargetExe, "TargetExe 丢失");
        AssertEqual(false, loaded.LockTargetExe, "LockTargetExe 丢失");
        AssertEqual(@"D:\games", loaded.RecoveryDirectories[0], "RecoveryDirectories 丢失");
    }
    finally { Nuke(dir); }
});

Check("F3 状态往返：Phase / 未恢复项 / 各记录字段", () =>
{
    var dir = NewTempDir("state");
    try
    {
        var path = Path.Combine(dir, "state.json");
        new FocusState
        {
            IsFocusing = true,
            StartedAt = DateTimeOffset.Now,
            Phase = FocusPhases.UnlockIncomplete,
            RecordedTargetPath = @"D:\a\fm.exe",
            RecordedLockedPath = @"D:\a\fm.exe" + TargetLocker.LockedSuffix,
            AppliedHostsBlock = true,
            AppliedTargetLock = true,
            UnresolvedItems = new List<string> { "移除 hosts 规则" },
            LastOperation = "部分解除失败",
        }.Save(path);

        var loaded = FocusState.Load(path);
        AssertTrue(loaded.IsFocusing, "IsFocusing 丢失");
        AssertEqual(FocusPhases.UnlockIncomplete, loaded.Phase, "Phase 丢失");
        AssertEqual(1, loaded.UnresolvedItems.Count, "UnresolvedItems 丢失");
        AssertTrue(loaded.RecordedLockedPath?.EndsWith(TargetLocker.LockedSuffix) == true, "RecordedLockedPath 丢失");
    }
    finally { Nuke(dir); }
});

Check("F4 损坏或缺失的状态文件不会让程序起不来", () =>
{
    var dir = NewTempDir("bad-state");
    try
    {
        var path = Path.Combine(dir, "state.json");
        File.WriteAllText(path, "{ this is not json ");
        var broken = FocusState.TryLoad(path, out var usable);
        AssertTrue(!usable, "损坏的状态文件被当成可用");
        AssertTrue(!broken.IsFocusing, "损坏的状态应回退为未专注");
        AssertTrue(broken.UnresolvedItems is not null, "UnresolvedItems 未初始化");

        var missing = FocusState.TryLoad(Path.Combine(dir, "nope.json"), out var missingUsable);
        AssertTrue(!missingUsable, "不存在的状态文件被当成可用");
        AssertTrue(!missing.IsFocusing, "不存在的状态应为未专注");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== G. 旧版本 ACL 残留的检测与处理 ==");

Check("G1 能检测出旧版本留下的 icacls 拒绝规则", () =>
{
    var dir = NewTempDir("legacy-detect");
    try
    {
        var file = Path.Combine(dir, "fm.exe");
        File.WriteAllText(file, "game");

        CommandRunner.Run("icacls", file, "/deny", Environment.UserName + ":(RX)");

        var findings = LegacyAclCheck.Inspect(new[] { file });
        AssertEqual(1, findings.Count, "未检测到旧版权限残留");
        AssertTrue(findings[0].Detail.Contains("/remove:d"), "提示里没有给出处理方式");
    }
    finally { Nuke(dir); }
});

Check("G2 新的开始/结束流程完全不碰文件权限（旧规则原样保留）", () =>
{
    var dir = NewTempDir("legacy-untouched");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        CommandRunner.Run("icacls", target, "/deny", Environment.UserName + ":(RX)");
        AssertEqual(1, LegacyAclCheck.Inspect(new[] { target }).Count, "测试前置条件不成立");

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var start = controller.Start();
        var stop = controller.Stop();

        // 只看权限有没有被动过：无论本次往返成功与否，旧规则都必须原样保留
        var existing = File.Exists(target) ? target : target + TargetLocker.LockedSuffix;
        AssertEqual(1, LegacyAclCheck.Inspect(new[] { existing }).Count,
            "开始/结束流程改动了文件权限（旧规则被静默移除或新增）");
        Console.WriteLine($"        （本次往返：开始 {(start.Success ? "成功" : "失败")}，" +
                          $"结束 {(stop.Success ? "成功" : "失败")}）");
    }
    finally { Nuke(dir); }
});

Check("G3 只有显式要求时才移除旧规则", () =>
{
    var dir = NewTempDir("legacy-remove");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        CommandRunner.Run("icacls", target, "/deny", Environment.UserName + ":(RX)");

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var result = controller.RemoveLegacyDenyRules();
        AssertTrue(result.Success, "显式移除失败：" + result.FailureText);
        AssertEqual(0, LegacyAclCheck.Inspect(new[] { target }).Count, "旧规则仍在");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
if (ciMode)
{
    Console.WriteLine("== H. 本机环境只读检查（CI 模式跳过） ==");
}
else
{
    Console.WriteLine("== H. 本机环境只读检查 ==");

    Check("H1 真实 hosts 当前不含 FocusGuard 区块（未被污染）", () =>
    {
        var blocker = new HostsBlocker();
        AssertTrue(File.Exists(blocker.HostsPath), "找不到真实 hosts 文件：" + blocker.HostsPath);
        AssertTrue(!blocker.IsActive(), "真实 hosts 里存在残留区块，需要清理");
        Console.WriteLine("        " + blocker.HostsPath);
    });

    Check("H2 真实 fm.exe 存在且未被改名，也没有旧版权限残留", () =>
    {
        var config = new FocusConfig();
        var locker = new TargetLocker(config.TargetExe);
        AssertEqual(LockState.Unlocked, locker.State, "真实目标不是正常状态：" + locker.State);
        var legacy = LegacyAclCheck.Inspect(new[] { config.TargetExe });
        AssertEqual(0, legacy.Count, "真实 fm.exe 上存在旧版拒绝规则，需要手工处理");
        Console.WriteLine("        " + config.TargetExe);
    });
}

Console.WriteLine();
Console.WriteLine("== I. 恢复线索的可靠性（本次修复重点） ==");

Check("I1 状态文件写不下时开始必须中止，不得施加任何限制（核心）", () =>
{
    var dir = NewTempDir("state-write-fail");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        // 让状态写入必然失败：在 state.json 该在的位置放一个同名目录
        Directory.CreateDirectory(Path.Combine(dir, "state.json"));

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var start = controller.Start();

        AssertTrue(!start.Success, "恢复线索存不下来，却仍然报告开始成功");
        AssertTrue(start.Steps.Any(s => s.Name == "记录恢复线索" && s.Status == StepStatus.Failed),
            "没有报告恢复线索保存失败");
        AssertTrue(File.Exists(target), "在没有恢复线索的情况下仍然改动了目标文件");
        AssertTrue(!File.Exists(target + TargetLocker.LockedSuffix), "目标文件被改名了");
        AssertTrue(!new HostsBlocker(hostsPath).IsActive(), "在没有恢复线索的情况下仍然写了 hosts");
        AssertTrue(!controller.IsLockedNow, "中止后不应处于锁定态");
        AssertTrue(!File.Exists(Path.Combine(dir, "state.json.tmp")), "失败后留下了临时文件");
    }
    finally { Nuke(dir); }
});

Check("I2 记录中的目标找不到时，必须报警并保留记录线索（核心）", () =>
{
    var dir = NewTempDir("unlocated");
    var elsewhere = NewTempDir("unlocated-elsewhere");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var locked = target + TargetLocker.LockedSuffix;
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");

        // 把锁定文件移出记录位置，hosts 区块保留（评审复现场景）
        var moved = Path.Combine(elsewhere, Path.GetFileName(locked));
        File.Move(locked, moved);

        var stop = controller.Stop();
        AssertTrue(stop.Warnings.Any(w => (w.Detail ?? string.Empty).Contains("找不到")),
            "没有报告记录中的文件找不到");
        AssertTrue(!new HostsBlocker(hostsPath).IsActive(), "hosts 区块本应正常移除");

        // 记录线索不能被清空
        AssertTrue(!string.IsNullOrWhiteSpace(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath),
            "记录线索被清空了");
        AssertTrue(controller.UnlocatedRecord is not null, "未暴露未定位的记录");
        AssertTrue(controller.EnforcementSummary().Any(l => l.Contains("未找到")), "界面没有显示未找到");

        // 指定目录与记录路径不同 → 只算"同名"，不能据此清空线索
        var remedy = controller.UnlockInDirectory(elsewhere);
        AssertTrue(remedy.Success, "按目录恢复失败：" + remedy.FailureText);
        AssertTrue(File.Exists(Path.Combine(elsewhere, "fm.exe")), "文件未在原位恢复");
        AssertTrue(!File.Exists(moved), "锁定文件仍然存在");
        AssertTrue(remedy.Warnings.Any(w => (w.Detail ?? string.Empty).Contains("同名")),
            "没有提示同名不等于同一个文件");
        AssertTrue(!string.IsNullOrWhiteSpace(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath),
            "路径不同却清空了记录线索");
        AssertTrue(controller.UnlocatedRecord is not null, "路径不同却把记录判为已解决");

        // 用户核对后确认对应，才清掉线索
        AssertTrue(controller.DiscardUnlocatedRecord("核对确认对应"), "确认后清除线索失败");
        AssertTrue(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath is null,
            "确认后记录线索仍未清空");
        AssertTrue(controller.UnlocatedRecord is null, "确认后仍报告未定位");
    }
    finally { Nuke(dir); Nuke(elsewhere); }
});

Check("I3 状态写入采用临时文件替换；正常往返后不残留临时文件", () =>
{
    var dir = NewTempDir("state-atomic");
    try
    {
        var (config, hostsPath, _) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");
        AssertTrue(controller.Stop().Success, "结束失败");

        AssertTrue(!File.Exists(Path.Combine(dir, "state.json.tmp")), "留下了状态临时文件");
        AssertTrue(File.Exists(Path.Combine(dir, "state.json")), "状态文件不存在");
    }
    finally { Nuke(dir); }
});

Check("I4 开始中断时不会把已有的记录线索抹掉", () =>
{
    var dir = NewTempDir("state-preserve");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var statePath = Path.Combine(dir, "state.json");
        var previousLocked = target + TargetLocker.LockedSuffix;

        // 已有记录处于"已恢复正常"状态（原文件在位、没有锁定文件），因此不该阻塞新的开始
        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Idle,
            RecordedTargetPath = target,
            RecordedLockedPath = previousLocked,
        }.Save(statePath);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Inspect().Any(l => l.Contains(previousLocked)), "前置条件不成立：未读到已有记录");

        File.Delete(statePath);
        Directory.CreateDirectory(statePath);   // 让状态写入必然失败

        var start = controller.Start();
        AssertTrue(!start.Success, "写入失败时开始却报告成功");
        AssertTrue(!File.Exists(previousLocked), "写不下记录却仍然改了名");
        AssertTrue(controller.Inspect().Any(l => l.Contains(previousLocked)), "已有的记录线索被抹掉了");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== J. 记录线索的归属与覆盖（第三轮复审修复） ==");

Check("J1 存在未定位记录时，开始新会话必须被拒绝且不得覆盖旧记录（核心）", () =>
{
    var dir = NewTempDir("no-overwrite");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var statePath = Path.Combine(dir, "state.json");
        var oldTarget = Path.Combine(dir, "old.exe");
        var oldLocked = oldTarget + TargetLocker.LockedSuffix;

        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Idle,
            RecordedTargetPath = oldTarget,
            RecordedLockedPath = oldLocked,
        }.Save(statePath);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.UnlocatedRecord is not null, "前置条件不成立：未识别出未定位记录");

        var start = controller.Start();
        AssertTrue(!start.Success, "存在未定位记录时却允许开始");
        AssertTrue(start.Failures.Any(f => f.Name == "检查未解决的记录"), "没有说明拒绝原因");
        AssertEqual(oldLocked, FocusState.Load(statePath).RecordedLockedPath, "旧的恢复线索被覆盖了");
        AssertTrue(!File.Exists(target + TargetLocker.LockedSuffix), "被拒绝后仍然施加了限制");

        // 用户显式放弃旧线索后，才允许开始
        AssertTrue(controller.DiscardUnlocatedRecord("测试"), "放弃旧记录失败");
        AssertTrue(controller.Start().Success, "放弃旧记录后仍无法开始");
        AssertTrue(controller.Stop().Success, "收尾失败");
    }
    finally { Nuke(dir); }
});

Check("J2 目录恢复的是无关文件时，不得清空旧记录线索（核心）", () =>
{
    var dir = NewTempDir("unrelated");
    var otherDir = NewTempDir("unrelated-other");
    try
    {
        var (config, hostsPath, _) = SetupCycle(dir);
        var statePath = Path.Combine(dir, "state.json");
        var oldTarget = Path.Combine(dir, "old.exe");
        var oldLocked = oldTarget + TargetLocker.LockedSuffix;

        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Idle,
            RecordedTargetPath = oldTarget,
            RecordedLockedPath = oldLocked,
        }.Save(statePath);

        // 指定目录里只有一个与记录无关的改名文件
        var other = Path.Combine(otherDir, "other.exe");
        File.WriteAllText(other, "other");
        File.Move(other, other + TargetLocker.LockedSuffix);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var remedy = controller.UnlockInDirectory(otherDir);
        AssertTrue(remedy.Success, "目录恢复失败：" + remedy.FailureText);
        AssertTrue(File.Exists(other), "无关文件本应被还原");
        AssertTrue(remedy.Warnings.Any(w => (w.Detail ?? string.Empty).Contains("文件名不同")),
            "没有说明处理的文件与记录不同");
        AssertEqual(oldLocked, FocusState.Load(statePath).RecordedLockedPath, "旧记录线索被清空了");
        AssertTrue(controller.UnlocatedRecord is not null, "旧记录被误判为已解决");
    }
    finally { Nuke(dir); Nuke(otherDir); }
});

Check("J3 已恢复的目标在重试时不得被误报为未找到（核心）", () =>
{
    var dir = NewTempDir("retry-mixed");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "前置的开始操作失败");

        BreakHostsWrite(hostsPath);           // 只让 hosts 那一步失败，FM 应正常恢复
        var first = controller.Stop();
        AssertTrue(!first.Success, "前置的失败场景没有复现");
        AssertTrue(File.Exists(target), "FM 本应已恢复");
        AssertTrue(controller.UnresolvedItems.Count > 0, "失败项没被保留");

        UnbreakHostsWrite(hostsPath);
        var retry = controller.Stop();
        AssertTrue(retry.Success, "重试失败：" + retry.FailureText);
        AssertTrue(!retry.Warnings.Any(w => (w.Detail ?? string.Empty).Contains("找不到")),
            "把已恢复的 FM 误报成找不到");
        AssertTrue(controller.UnlocatedRecord is null, "已恢复却仍报告未定位");
        AssertTrue(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath is null,
            "已全部恢复却没有清空记录");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== K. 同名 ≠ 同一个文件（第四轮复审修复） ==");

Check("K1 跨目录遇到同名文件时不得清空旧记录线索（核心）", () =>
{
    var dirA = NewTempDir("same-name-a");
    var dirB = NewTempDir("same-name-b");
    try
    {
        var statePath = Path.Combine(dirA, "state.json");
        var hostsPath = Path.Combine(dirA, "hosts");
        File.WriteAllBytes(hostsPath, HostsBytes(PristineHosts));

        var targetA = Path.Combine(dirA, "fm.exe");
        var lockedA = targetA + TargetLocker.LockedSuffix;

        // 记录指向 A 里的目标；真身已被搬走，A 里两种文件都不在
        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Idle,
            RecordedTargetPath = targetA,
            RecordedLockedPath = lockedA,
        }.Save(statePath);

        // B 目录里有一份"同名的另一个文件"
        var lockedB = Path.Combine(dirB, "fm.exe" + TargetLocker.LockedSuffix);
        File.WriteAllText(lockedB, "another-file-with-the-same-name");

        var controller = new FocusController(
            new FocusConfig { TargetExe = targetA }, dirA, new HostsBlocker(hostsPath));

        var remedy = controller.UnlockInDirectory(dirB);
        AssertTrue(remedy.Success, "目录恢复失败：" + remedy.FailureText);
        AssertTrue(File.Exists(Path.Combine(dirB, "fm.exe")), "B 中的文件未被还原");

        var warning = remedy.Warnings.Select(w => w.Detail ?? string.Empty).FirstOrDefault();
        AssertTrue(warning is not null, "没有给出任何提示");
        AssertTrue(warning!.Contains("同名"), "没有说明同名不等于同一个文件");
        AssertTrue(warning.Contains(lockedA), "提示里没有原记录路径");
        AssertTrue(warning.Contains(dirB), "提示里没有本次恢复路径");

        AssertEqual(lockedA, FocusState.Load(statePath).RecordedLockedPath, "同名却清空了旧记录");
        AssertTrue(controller.UnlocatedRecord is not null, "同名却把旧记录判为已解决");
    }
    finally { Nuke(dirA); Nuke(dirB); }
});

Check("K2 恢复路径与记录完全一致时才清空线索", () =>
{
    var dir = NewTempDir("exact-path");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var locked = target + TargetLocker.LockedSuffix;

        // 真身就在记录路径上（改好名但还没还原）
        File.Move(target, locked);
        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Unlocking,
            RecordedTargetPath = target,
            RecordedLockedPath = locked,
        }.Save(Path.Combine(dir, "state.json"));

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var remedy = controller.UnlockInDirectory(dir);
        AssertTrue(remedy.Success, "目录恢复失败：" + remedy.FailureText);
        AssertTrue(File.Exists(target), "文件未还原");
        AssertTrue(FocusState.Load(Path.Combine(dir, "state.json")).RecordedLockedPath is null,
            "路径完全一致却没有清空线索");
        AssertTrue(controller.UnlocatedRecord is null, "已恢复却仍报告未定位");
    }
    finally { Nuke(dir); }
});

Check("K3 放弃记录时落盘失败必须报告失败并还原内存记录", () =>
{
    var dir = NewTempDir("discard-fail");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        var statePath = Path.Combine(dir, "state.json");
        var locked = target + TargetLocker.LockedSuffix;

        File.Move(target, locked);
        new FocusState
        {
            IsFocusing = false,
            Phase = FocusPhases.Unlocking,
            RecordedTargetPath = target,
            RecordedLockedPath = locked,
        }.Save(statePath);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.UnlocatedRecord is not null, "前置条件不成立");

        File.Delete(statePath);
        Directory.CreateDirectory(statePath);   // 让状态写入必然失败

        AssertTrue(!controller.DiscardUnlocatedRecord("测试"), "落盘失败却报告放弃成功");
        AssertTrue(controller.Inspect().Any(l => l.Contains(locked)), "内存里的记录被清掉了");
        AssertTrue(controller.UnlocatedRecord is not null, "内存里的未定位记录丢失了");
    }
    finally { Nuke(dir); }
});

Console.WriteLine();
Console.WriteLine("== M. 提权命令解析与虚假恢复记录（第六轮评审修复） ==");

Check("M1 裸命令名固定解析到系统目录的绝对路径", () =>
{
    var resolved = CommandRunner.Resolve("icacls");
    var sysDir = Environment.SystemDirectory;
    AssertTrue(Path.IsPathRooted(resolved), "解析结果不是绝对路径：" + resolved);
    AssertTrue(string.Equals(Path.GetDirectoryName(resolved), sysDir, StringComparison.OrdinalIgnoreCase),
        "解析结果不在系统目录：" + resolved);
    AssertTrue(File.Exists(resolved), "解析到的工具不存在：" + resolved);
});

Check("M2 未知裸命令直接失败，不落回 PATH / 当前目录搜索（fail-closed）", () =>
{
    var cwd = Environment.CurrentDirectory;
    var probe = NewTempDir("hijack-probe");
    try
    {
        // 在当前目录放一个同名诱饵：如果解析落回 PATH/当前目录搜索，它就会被执行或被选中
        var decoy = Path.Combine(probe, "fg-no-such-tool-xyz.exe");
        File.WriteAllText(decoy, "decoy");
        Environment.CurrentDirectory = probe;
        try
        {
            AssertThrows<InvalidOperationException>(() => CommandRunner.Run("fg-no-such-tool-xyz"),
                "未知工具没有抛异常，说明落回了 PATH 搜索");
        }
        finally { Environment.CurrentDirectory = cwd; }
    }
    finally { Nuke(probe); }
});

Check("M3 只封网站（LockTargetExe=false）+ 目标不存在：不产生虚假恢复记录，第二次开始不受阻", () =>
{
    var dir = NewTempDir("hosts-only-phantom");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        config.LockTargetExe = false;
        File.Delete(target);   // 评审复现条件：目标文件不存在

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var start1 = controller.Start();
        AssertTrue(start1.Success, "第一次开始失败：" + start1.FailureText);
        AssertTrue(start1.Steps.Any(s => s.Name == "阻止 FM 启动" && s.Status == StepStatus.Skipped),
            "关闭文件锁定时应有跳过步骤");

        var stop1 = controller.Stop();
        AssertTrue(stop1.Success, "第一次结束失败：" + stop1.FailureText);
        AssertTrue(!stop1.Warnings.Any(),
            "产生了虚假的未定位警告：" + string.Join("；", stop1.Warnings.Select(w => w.Detail)));
        AssertTrue(controller.UnlocatedRecord is null, "留下了指向从未被锁定文件的恢复记录");
        AssertTrue(controller.UnresolvedItems.Count == 0, "存在未恢复项");

        var start2 = controller.Start();
        AssertTrue(start2.Success, "第二次开始被上一次的虚假记录阻塞：" + start2.FailureText);
    }
    finally { Nuke(dir); }
});

Check("M4 只封网站 + 目标存在：目标文件完全不被触碰", () =>
{
    var dir = NewTempDir("hosts-only-keep");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        config.LockTargetExe = false;
        var before = File.ReadAllBytes(target);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        AssertTrue(controller.Start().Success, "开始失败");
        AssertBytesEqual(before, File.ReadAllBytes(target), "只封网站时目标文件被改动了");
        AssertTrue(!File.Exists(target + TargetLocker.LockedSuffix), "目标文件被改名了");

        AssertTrue(controller.Stop().Success, "结束失败");
        AssertBytesEqual(before, File.ReadAllBytes(target), "结束时目标文件被改动了");
        AssertTrue(controller.UnlocatedRecord is null, "留下了恢复记录");
    }
    finally { Nuke(dir); }
});

Check("M5 开启文件锁定但目标不存在：明确跳过且不留恢复记录", () =>
{
    var dir = NewTempDir("lock-missing");
    try
    {
        var (config, hostsPath, target) = SetupCycle(dir);
        config.LockTargetExe = true;
        File.Delete(target);

        var controller = new FocusController(config, dir, new HostsBlocker(hostsPath));
        var start = controller.Start();
        AssertTrue(start.Success, "目标不存在时开始却失败：" + start.FailureText);
        AssertTrue(start.Steps.Any(s => s.Name == "阻止 FM 启动" && s.Status == StepStatus.Skipped),
            "没有报告跳过");
        AssertTrue(controller.UnlocatedRecord is null, "留下了指向从未被锁定文件的恢复记录");

        var stop = controller.Stop();
        AssertTrue(stop.Success && !stop.Warnings.Any(),
            "结束时出现未定位警告：" + string.Join("；", stop.Warnings.Select(w => w.Detail)));
        var start2 = controller.Start();
        AssertTrue(start2.Success, "第二次开始被阻塞：" + start2.FailureText);
    }
    finally { Nuke(dir); }
});

Check("M6 RID-500 账户的 deny ACE 用 LA 别名表示，检测必须命中（CI 实测回归）", () =>
{
    // CI 诊断实测：runner 是内置 Administrator（RID-500），icacls /save 把它的 deny ACE
    // 写成 (D;;0x1200a9;;;LA) 而不是完整 SID —— 修复前 G1/G2 在 CI 上因此检测到 0 条。
    var ciDacl = "D:AI(D;;0x1200a9;;;LA)(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1d0156;;;LA)" +
                 "(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;LA)";
    var rid500 = "S-1-5-21-3699639565-2515463329-295617607-500";

    AssertEqual(1, LegacyAclCheck.CountDenyAcesForSid(ciDacl, rid500),
        "RID-500 账户在 CI 形态的 DACL 里应命中 1 条 deny（经 LA 别名）");
    AssertEqual(0, LegacyAclCheck.CountDenyAcesForSid(ciDacl, "S-1-5-21-3699639565-2515463329-295617607-1001"),
        "LA 别名不得被其他账户（非 RID-500）误匹配");

    var localDacl = "D:AI(A;ID;FA;;;BA)(D;;RX;;;S-1-5-21-1-2-3-1001)";
    AssertEqual(1, LegacyAclCheck.CountDenyAcesForSid(localDacl, "S-1-5-21-1-2-3-1001"),
        "普通账户的完整 SID 形态应照常命中");

    var me = LegacyAclCheck.CurrentUserSid();
    AssertTrue(me is not null && me.StartsWith("S-", StringComparison.Ordinal),
        "CurrentUserSid 应返回合法 SID，实际：" + (me ?? "null"));
});

Console.WriteLine();
Console.WriteLine($"====  通过 {passed} 项，失败 {failures.Count} 项  ====");
if (failures.Count > 0)
{
    Console.WriteLine("失败明细：");
    foreach (var f in failures) Console.WriteLine("  - " + f);
    Environment.Exit(1);
}
Environment.Exit(0);
