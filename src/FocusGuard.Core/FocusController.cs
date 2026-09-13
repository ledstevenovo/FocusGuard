namespace FocusGuard.Core;

/// <summary>
/// 专注状态的唯一入口。五条铁律：
/// <list type="number">
/// <item><b>先可靠记录再操作</b> —— 恢复线索存不下来就中止，绝不动手；</item>
/// <item><b>开始失败不留半锁</b> —— 任何一步失败就把本次刚做的改动收回去；</item>
/// <item><b>解除失败不谎报成功</b> —— 尝试过但失败的项进 <see cref="UnresolvedItems"/>，阻塞并把窗口留给用户重试；</item>
/// <item><b>找不到的不能当没发生</b> —— 记录里有的对象定位不到时，明确报警并<b>保留记录线索</b>，不清空；</item>
/// <item><b>两条通道分开判定</b> —— hosts 与目标文件各自独立统计，任何一条有情况都不会被另一条掩盖。</item>
/// </list>
/// </summary>
public sealed class FocusController
{
    private readonly object _gate = new();
    private readonly FocusConfig _config;
    private readonly HostsBlocker _hosts;
    private readonly string _statePath;
    private bool _stateFileUsable;
    private FocusState _state;

    public event Action<string>? Log;

    public FocusController(FocusConfig config, string dataDirectory, HostsBlocker? hostsBlocker = null)
    {
        _config = config;
        _statePath = Path.Combine(dataDirectory, "state.json");
        _hosts = hostsBlocker ?? new HostsBlocker();
        _state = FocusState.TryLoad(_statePath, out var usable);
        _stateFileUsable = usable;
    }

    public FocusConfig Config => _config;

    public bool IsFocusing => _state.IsFocusing;

    public DateTimeOffset? StartedAt => _state.StartedAt;

    public string Phase => _state.Phase;

    /// <summary>尝试恢复但没成功的项。界面据此显示"部分解除失败"并允许重试。</summary>
    public IReadOnlyList<string> UnresolvedItems => _state.UnresolvedItems;

    /// <summary>
    /// 记录里存在、但当前<b>无法确认为已恢复</b>的对象（记录线索会保留）。
    /// 判定依据是记录中那个目标自身的状态：只有它回到 <see cref="LockState.Unlocked"/>
    /// （原文件在位、且没有锁定文件）才算解决。
    /// 不能因为"在别处找不到锁定文件"就认定无法定位 —— <b>那恰恰是"已恢复"的正常结果</b>。
    /// </summary>
    public string? UnlocatedRecord =>
        string.IsNullOrWhiteSpace(_state.RecordedLockedPath) || RecordedTargetResolved()
            ? null
            : _state.RecordedLockedPath;

    /// <summary>记录里的目标是否已确认回到正常状态（原文件在位、且没有锁定文件）。</summary>
    public bool RecordedTargetResolved()
    {
        if (string.IsNullOrWhiteSpace(_state.RecordedTargetPath))
        {
            return true;
        }
        return new TargetLocker(_state.RecordedTargetPath!).State == LockState.Unlocked;
    }

    public string HostsPath => _hosts.HostsPath;

    public IReadOnlyList<string> ListHostsBackups() => _hosts.ListBackups();

    // ---- 真实状态的探测（不看配置意图，只看系统里实际有什么） ----

    /// <summary>hosts 里存在我们的区块。<b>仅代表规则已写入，不代表浏览器访问一定已被阻止。</b></summary>
    public bool HostsBlockActive => _hosts.IsActive();

    public bool TargetLockActive => Candidates().Any(c => c.IsLocked);

    public bool TargetConflict => Candidates().Any(c => c.HasConflict);

    public bool TargetPresent => Candidates().Any(c => c.TargetExists);

    public bool AnyEnforcementActive => HostsBlockActive || TargetLockActive || TargetConflict;

    /// <summary>需要用户关注：仍有强制措施，或有没恢复成功的项。</summary>
    public bool NeedsAttention => AnyEnforcementActive || _state.UnresolvedItems.Count > 0;

    public bool IsLockedNow => NeedsAttention || IsFocusing;

    public IReadOnlyList<LegacyAclFinding> LegacyAclFindings { get; private set; } = Array.Empty<LegacyAclFinding>();

    public IReadOnlyList<string> RunningBlockedProcesses() => ProcessGuard.FindRunning(_config.BlockedProcessNames);

    public int KillBlockedProcesses() => ProcessGuard.KillAll(_config.BlockedProcessNames);

    /// <summary>精确候选路径：state 记录 → 当前配置 → 用户补充的恢复目录下的同名文件。不做通配。</summary>
    public IEnumerable<string> CandidateTargetPaths()
    {
        if (!string.IsNullOrWhiteSpace(_state.RecordedTargetPath))
        {
            yield return _state.RecordedTargetPath!;
        }

        if (!string.IsNullOrWhiteSpace(_config.TargetExe))
        {
            yield return _config.TargetExe;
        }

        var fileName = Path.GetFileName(_config.TargetExe);
        if (!string.IsNullOrWhiteSpace(fileName) && _config.RecoveryDirectories is not null)
        {
            foreach (var directory in _config.RecoveryDirectories)
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    yield return Path.Combine(directory, fileName);
                }
            }
        }
    }

    private IEnumerable<TargetLocker> Candidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidateTargetPaths())
        {
            if (seen.Add(path))
            {
                yield return new TargetLocker(path);
            }
        }
    }

    public void RefreshLegacyAclFindings()
    {
        var paths = CandidateTargetPaths().Concat(
            CandidateTargetPaths().Select(p => p + TargetLocker.LockedSuffix));
        LegacyAclFindings = LegacyAclCheck.Inspect(paths);
    }

    // ------------------------------------------------------------------ 开始

    public OperationResult Start()
    {
        lock (_gate)
        {
            var steps = new List<StepOutcome>();

            // 有未解决的旧记录时绝不开始 —— 否则会把它静默覆盖掉，线索就永久丢了
            if (UnlocatedRecord is not null)
            {
                steps.Add(new StepOutcome("检查未解决的记录", StepStatus.Failed,
                    "上一次有一条记录未能定位：" + UnlocatedRecord +
                    "。为避免覆盖这条恢复线索，本次未开始。" +
                    "可用 --unlock <目录> 找到它，或在界面上选择放弃该记录。"));
                Say("开始被拒绝：存在未能定位的旧记录");
                return new OperationResult(false, steps);
            }

            var plannedLockedPath = LockedPathFor(_config.TargetExe);

            // 1) 先可靠保存操作意图 —— 存不下来就绝不动手
            var savedPhase = _state.Phase;
            var savedTarget = _state.RecordedTargetPath;
            var savedLocked = _state.RecordedLockedPath;
            var savedHosts = _state.RecordedHostsPath;
            var savedUnresolved = _state.UnresolvedItems;

            _state.Phase = FocusPhases.Locking;
            _state.RecordedTargetPath = _config.TargetExe;
            _state.RecordedLockedPath = plannedLockedPath;
            _state.RecordedHostsPath = _hosts.HostsPath;
            _state.UnresolvedItems = new List<string>();

            if (!PersistState())
            {
                // 还原内存状态，避免把上一次留下的恢复线索一起抹掉
                _state.Phase = savedPhase;
                _state.RecordedTargetPath = savedTarget;
                _state.RecordedLockedPath = savedLocked;
                _state.RecordedHostsPath = savedHosts;
                _state.UnresolvedItems = savedUnresolved;

                steps.Add(new StepOutcome("记录恢复线索", StepStatus.Failed,
                    "状态文件写入失败，无法留下恢复线索，因此没有施加任何限制。请检查数据目录是否可写：" +
                    (_statePath)));
                Say("开始已中止：状态文件写入失败，未做任何改动");
                return new OperationResult(false, steps);
            }

            // 2) hosts 规则
            var hostsAppliedHere = false;
            try
            {
                _hosts.Apply(_config.BlockedDomains);
                hostsAppliedHere = true;
                steps.Add(new StepOutcome("写入网站规则", StepStatus.Ok,
                    $"{HostsFile.Normalize(_config.BlockedDomains).Count} 个域名"));
            }
            catch (Exception ex)
            {
                steps.Add(new StepOutcome("写入网站规则", StepStatus.Failed, ex.Message));
            }

            // 3) 目标文件改名
            var renamedHere = false;
            var lockedPath = plannedLockedPath;
            if (!_config.LockTargetExe)
            {
                steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Skipped, "配置里已关闭"));
            }
            else
            {
                var locker = new TargetLocker(_config.TargetExe);
                try
                {
                    switch (locker.State)
                    {
                        case LockState.Conflict:
                            steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Failed,
                                "原文件与锁定文件同时存在，已拒绝覆盖。请手工确认：" +
                                locker.TargetPath + " / " + locker.LockedPath));
                            break;

                        case LockState.Locked:
                            steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Ok, "此前已锁定"));
                            break;

                        case LockState.Missing:
                            lockedPath = null;
                            steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Skipped,
                                "目标不存在：" + _config.TargetExe));
                            break;

                        default:
                            locker.Lock();
                            renamedHere = true;
                            steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Ok,
                                Path.GetFileName(locker.LockedPath)));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    steps.Add(new StepOutcome("阻止 FM 启动", StepStatus.Failed, ex.Message));
                }
            }

            steps.Add(FlushDnsStep());

            // 4) 收尾
            if (steps.Any(s => s.Status == StepStatus.Failed))
            {
                var rollbackFailures = Rollback(hostsAppliedHere, renamedHere ? lockedPath : null);
                foreach (var failure in rollbackFailures)
                {
                    steps.Add(new StepOutcome("回滚", StepStatus.Failed, failure));
                }

                _state.UnresolvedItems = rollbackFailures.ToList();
                _state.IsFocusing = AnyEnforcementActive || _state.UnresolvedItems.Count > 0;
                _state.Phase = FocusPhases.Idle;
                _state.LastOperation = "开始失败：" + JoinNames(steps, StepStatus.Failed);
                PersistState();
                Say(_state.LastOperation);
                return new OperationResult(false, steps);
            }

            _state.IsFocusing = true;
            _state.StartedAt = DateTimeOffset.Now;
            _state.Phase = FocusPhases.Locked;
            _state.AppliedHostsBlock = HostsBlockActive;
            _state.AppliedTargetLock = TargetLockActive;
            _state.RecordedLockedPath = lockedPath;
            _state.UnresolvedItems = new List<string>();
            _state.LastOperation = "专注开始";

            if (!PersistState())
            {
                // 线索在动手前已经存好，这里只是没能更新阶段 —— 不阻塞，但要如实提示
                steps.Add(new StepOutcome("确认锁定状态", StepStatus.Warning,
                    "状态文件更新失败；恢复线索已在动手前保存，仍可用 --unlock 清理"));
            }

            Say(_state.LastOperation);
            return new OperationResult(true, steps);
        }
    }

    // ------------------------------------------------------------------ 结束

    public OperationResult Stop()
    {
        lock (_gate)
        {
            var steps = new List<StepOutcome>();
            var unresolved = new List<string>();   // 尝试过但失败 —— 阻塞
            var unlocated = new List<string>();     // 定位不到 —— 保留线索，不阻塞
            var foundAny = false;
            var stateWasUsable = _stateFileUsable;

            _state.Phase = FocusPhases.Unlocking;
            if (!PersistState())
            {
                steps.Add(new StepOutcome("记录解除意图", StepStatus.Warning,
                    "状态文件写入失败，本次解除仍会继续"));
            }

            // 1) 目标文件：逐个精确探测后还原（与 hosts 分开判定，互不掩盖）
            var targetRecorded = _state.AppliedTargetLock || !string.IsNullOrWhiteSpace(_state.RecordedLockedPath);
            var targetLocated = false;

            foreach (var locker in Candidates())
            {
                if (locker.HasConflict)
                {
                    targetLocated = true;
                    foundAny = true;
                    steps.Add(new StepOutcome("恢复 FM 文件", StepStatus.Failed,
                        "原文件与锁定文件同时存在，已保留两份、未做任何覆盖或删除：" +
                        locker.TargetPath + " / " + locker.LockedPath));
                    unresolved.Add("恢复 FM 文件（文件冲突，需手工确认）");
                    continue;
                }

                if (!locker.IsLocked)
                {
                    continue;
                }

                targetLocated = true;
                foundAny = true;
                try
                {
                    locker.Unlock();
                    steps.Add(new StepOutcome("恢复 FM 文件", StepStatus.Ok, locker.TargetFileName));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepOutcome("恢复 FM 文件", StepStatus.Failed, ex.Message));
                    unresolved.Add("恢复 FM 文件：" + locker.TargetFileName);
                }
            }

            // 记录里明明锁过，现在却确认不了 —— 报警并保留线索，不能当没发生
            if (targetRecorded && !targetLocated && !RecordedTargetResolved())
            {
                var where = _state.RecordedLockedPath ?? _state.RecordedTargetPath ?? "（路径未知）";
                steps.Add(new StepOutcome("定位残留", StepStatus.Warning,
                    $"记录显示曾把文件改名为 {where}，但当前找不到它，无法确认是否已恢复；" +
                    "已保留这条记录线索（未清空）。如知道它被移到哪里，可用 --unlock <目录> 指定目录。"));
                unlocated.Add(where);
            }
            else if (targetRecorded && !targetLocated)
            {
                // 锁定文件不在了、原文件又在位 —— 这正是"已恢复"的正常结果，不能报成未找到
                steps.Add(new StepOutcome("定位残留", StepStatus.Skipped,
                    "记录中的目标已确认回到正常状态（原文件在位、没有锁定文件）。"));
            }

            // 2) hosts 规则
            var hostsChanged = false;
            if (_hosts.IsActive())
            {
                foundAny = true;
                try
                {
                    _hosts.Clear();
                    hostsChanged = true;
                    steps.Add(new StepOutcome("移除 hosts 规则", StepStatus.Ok));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepOutcome("移除 hosts 规则", StepStatus.Failed, ex.Message));
                    unresolved.Add("移除 hosts 规则");
                }
            }

            if (hostsChanged)
            {
                steps.Add(FlushDnsStep());
            }

            // 3) 全局兜底提示
            if (!stateWasUsable)
            {
                steps.Add(new StepOutcome("定位残留", StepStatus.Warning,
                    "状态文件缺失或损坏，无法确认之前改过哪些文件；如你知道目录，可用 --unlock <目录> 指定。"));
            }
            else if (!foundAny && unlocated.Count == 0)
            {
                steps.Add(new StepOutcome("定位残留", StepStatus.Skipped, "未发现任何残留的强制措施。"));
            }

            _state.UnresolvedItems = unresolved;

            if (unresolved.Count > 0)
            {
                // 解除没成功就不能谎报成功：保持锁定态，失败项留给用户重试
                _state.IsFocusing = true;
                _state.Phase = FocusPhases.UnlockIncomplete;
                _state.LastOperation = "部分解除失败：" + string.Join("；", unresolved);
                PersistState();
                Say(_state.LastOperation);
                return new OperationResult(false, steps);
            }

            _state.IsFocusing = false;
            _state.StartedAt = null;
            _state.Phase = FocusPhases.Idle;
            _state.AppliedHostsBlock = false;
            _state.AppliedTargetLock = false;
            _state.UnresolvedItems = new List<string>();

            if (unlocated.Count == 0 && RecordedTargetResolved())
            {
                // 一切都确认还原了，记录可以安全清空
                _state.RecordedTargetPath = null;
                _state.RecordedLockedPath = null;
            }
            // 否则保留记录线索，供 --unlock <目录> 继续定位

            _state.LastOperation = unlocated.Count > 0
                ? "已结束，但有记录未能定位"
                : foundAny ? "专注结束" : "没有需要解除的限制";

            if (!PersistState())
            {
                steps.Add(new StepOutcome("保存状态", StepStatus.Warning,
                    "状态文件写入失败，重启后可能仍显示专注中"));
            }

            Say(_state.LastOperation);
            return new OperationResult(true, steps);
        }
    }

    /// <summary>
    /// 用户显式确认后放弃无法定位的记录线索（会写进日志，绝不静默丢弃）。
    /// 存在的意义：界面需要一条"放弃这条线索并开始新会话"的出口，而不是让用户被卡住。
    /// </summary>
    public bool DiscardUnlocatedRecord(string reason)
    {
        lock (_gate)
        {
            var discardedLocked = _state.RecordedLockedPath;
            var discardedTarget = _state.RecordedTargetPath;
            var previousOperation = _state.LastOperation;

            if (discardedLocked is null && discardedTarget is null)
            {
                return false;
            }

            _state.RecordedTargetPath = null;
            _state.RecordedLockedPath = null;
            _state.LastOperation = "已放弃未能定位的记录：" + (discardedLocked ?? discardedTarget) + "（" + reason + "）";

            if (!PersistState())
            {
                // 落盘失败就还原内存，避免"界面说已放弃、重启后又被同一条记录挡住"
                _state.RecordedTargetPath = discardedTarget;
                _state.RecordedLockedPath = discardedLocked;
                _state.LastOperation = previousOperation;
                Say("放弃记录失败：状态文件写入失败，记录已保留");
                return false;
            }

            Say(_state.LastOperation);
            return true;
        }
    }

    /// <summary>
    /// 用户显式指定目录时的补救：在该目录里找 <c>*.focusguard-locked</c> 并还原。
    /// 只有走到这一步才允许通配 —— 因为是用户亲手指定的目录。
    /// </summary>
    public OperationResult UnlockInDirectory(string directory)
    {
        lock (_gate)
        {
            var steps = new List<StepOutcome>();
            var unresolved = new List<string>();

            if (!Directory.Exists(directory))
            {
                steps.Add(new StepOutcome("定位残留", StepStatus.Failed, "目录不存在：" + directory));
                return new OperationResult(false, steps);
            }

            var candidates = TargetLocker.ListCandidatesIn(directory);
            if (candidates.Count == 0)
            {
                steps.Add(new StepOutcome("定位残留", StepStatus.Skipped, "该目录下没有锁定文件：" + directory));
                return new OperationResult(true, steps);
            }

            foreach (var lockedPath in candidates)
            {
                var locker = TargetLocker.FromLockedPath(lockedPath);
                try
                {
                    locker.Unlock();
                    steps.Add(new StepOutcome("恢复 FM 文件", StepStatus.Ok, locker.TargetFileName));
                }
                catch (Exception ex)
                {
                    steps.Add(new StepOutcome("恢复 FM 文件", StepStatus.Failed, ex.Message));
                    unresolved.Add("恢复 FM 文件：" + locker.TargetFileName);
                }
            }

            _state.UnresolvedItems = unresolved;

            // 只有"恢复的路径与记录中的路径完全一致"才清空线索。
            // 路径不同就只是同名（或干脆是别的文件），不能据此认为记录已解决 ——
            // 把两个路径都摆出来让用户核对。
            if (unresolved.Count == 0 && RecordedPathRecoveredExactly(candidates))
            {
                _state.RecordedTargetPath = null;
                _state.RecordedLockedPath = null;
            }
            else if (unresolved.Count == 0 && _state.RecordedLockedPath is not null)
            {
                var recordedName = Path.GetFileName(_state.RecordedLockedPath);
                var sameName = candidates.Any(p =>
                    string.Equals(Path.GetFileName(p), recordedName, StringComparison.OrdinalIgnoreCase));

                var reason = sameName
                    ? "它与记录中的目标同名但路径不同 —— 同名不等于同一个文件"
                    : "它与记录中的目标文件名不同，不是同一个文件";

                steps.Add(new StepOutcome("定位残留", StepStatus.Warning,
                    "本次在 " + directory + " 恢复了 " + string.Join("、", candidates) +
                    "；记录中的目标是 " + _state.RecordedLockedPath + "。" +
                    reason + "，因此记录线索已保留。请核对两者是否对应：" +
                    "若确认已恢复，可用 --discard-record 清除该记录；否则请到记录中的路径再处理。"));
            }

            _state.LastOperation = unresolved.Count == 0
                ? "按指定目录完成恢复"
                : "部分解除失败：" + string.Join("；", unresolved);
            PersistState();
            Say(_state.LastOperation);
            return new OperationResult(unresolved.Count == 0, steps);
        }
    }

    /// <summary>
    /// 用户显式要求时，移除旧版本留下的 icacls 拒绝规则。
    /// 注意：这会删掉该用户在目标文件上的<b>全部</b>拒绝规则，且旧版本没有保存原始 ACL，无法还原。
    /// 所以它只在用户明确要求时执行，绝不自动触发。
    /// </summary>
    public OperationResult RemoveLegacyDenyRules()
    {
        lock (_gate)
        {
            var steps = new List<StepOutcome>();
            var targets = CandidateTargetPaths()
                .Concat(CandidateTargetPaths().Select(p => p + TargetLocker.LockedSuffix))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();

            if (targets.Count == 0)
            {
                steps.Add(new StepOutcome("移除旧版拒绝规则", StepStatus.Skipped, "没有可处理的目标文件"));
                return new OperationResult(true, steps);
            }

            foreach (var path in targets)
            {
                var result = CommandRunner.Run("icacls", path, "/remove:d", Environment.UserName);
                steps.Add(result.Ok
                    ? new StepOutcome("移除旧版拒绝规则", StepStatus.Ok, Path.GetFileName(path))
                    : new StepOutcome("移除旧版拒绝规则", StepStatus.Failed,
                        Path.GetFileName(path) + "（icacls 退出码 " + result.ExitCode + "）"));
            }

            var success = steps.All(s => s.Status != StepStatus.Failed);
            Say("手动移除旧版拒绝规则：" + (success ? "成功" : "失败"));
            return new OperationResult(success, steps);
        }
    }

    // ------------------------------------------------------------------ 描述

    /// <summary>
    /// 界面用的真实状态描述。措辞刻意保守：hosts 里有区块只说明"规则已写入"，
    /// 不能断言浏览器访问一定已被阻止（缓存、DoH 等都可能影响实际效果）。
    /// </summary>
    public IReadOnlyList<string> EnforcementSummary()
    {
        var lines = new List<string>
        {
            HostsBlockActive
                ? $"网站规则：已写入 hosts（{HostsFile.Normalize(_config.BlockedDomains).Count} 个域名）"
                : "网站规则：未写入 hosts",
        };

        var lockers = Candidates().ToList();
        var conflict = lockers.FirstOrDefault(l => l.HasConflict);
        var locked = lockers.FirstOrDefault(l => l.IsLocked);

        if (conflict is not null)
        {
            lines.Add("FM 文件：原文件与锁定文件同时存在，需手工确认");
        }
        else if (locked is not null)
        {
            lines.Add("FM 文件：已改名为 " + Path.GetFileName(locked.LockedPath));
        }
        else if (UnlocatedRecord is not null)
        {
            lines.Add("FM 文件：记录中的锁定文件未找到（" + UnlocatedRecord + "）");
        }
        else if (lockers.Any(l => l.TargetExists))
        {
            lines.Add("FM 文件：未改名");
        }
        else
        {
            lines.Add("FM 文件：原文件不存在，未做改动");
        }

        foreach (var item in _state.UnresolvedItems)
        {
            lines.Add("恢复失败：" + item);
        }

        if (LegacyAclFindings.Count > 0)
        {
            lines.Add("检测到旧版本残留的权限规则，请用 --diag 查看处理方式");
        }

        return lines;
    }

    public IEnumerable<string> Inspect()
    {
        yield return $"状态文件：{(_stateFileUsable ? "可用" : "缺失或损坏")}";
        yield return $"是否专注中：{(IsFocusing ? "是" : "否")}";
        yield return $"操作阶段：{Phase}";
        yield return $"开始时间：{_state.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无"}";
        yield return $"实际强制措施：{(AnyEnforcementActive ? "存在" : "无")}";
        yield return $"未恢复项：{(_state.UnresolvedItems.Count == 0 ? "无" : string.Join("；", _state.UnresolvedItems))}";
        yield return $"未定位到的记录：{UnlocatedRecord ?? "无"}";
        yield return $"hosts 路径：{_hosts.HostsPath}";
        yield return $"hosts 屏蔽区块：{(HostsBlockActive ? "存在" : "不存在")}";
        yield return $"hosts 备份：{(_hosts.ListBackups().Count == 0 ? "无" : string.Join(", ", _hosts.ListBackups().Select(Path.GetFileName)))}";
        yield return $"记录的目标路径：{_state.RecordedTargetPath ?? "无"}";
        yield return $"记录的锁定文件：{_state.RecordedLockedPath ?? "无"}";
        yield return $"当前目标文件：{_config.TargetExe}（{DescribeState(new TargetLocker(_config.TargetExe).State)}）";
        foreach (var finding in LegacyAclFindings)
        {
            yield return "旧版权限残留：" + finding.FilePath + " —— " + finding.Detail;
        }
        yield return $"上次操作：{_state.LastOperation ?? "无"}";
    }

    private static string DescribeState(LockState state) => state switch
    {
        LockState.Locked => "已改名锁定",
        LockState.Conflict => "原文件与锁定文件同时存在",
        LockState.Missing => "不存在",
        _ => "正常",
    };

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 目录恢复处理的，是否<b>就是记录中那一个文件</b>。
    /// 判据故意收紧到"路径完全相同"：<b>同名不代表同一个文件</b> —— 别处完全可能存在另一个
    /// 同名的锁定文件（评审复现场景：真身被搬到 A，B 里是另一份同名的）。
    /// 路径不同就是无法确认，此时保留线索并把两个路径都报出来，由用户核对。
    /// </summary>
    private bool RecordedPathRecoveredExactly(IReadOnlyList<string> recoveredLockedPaths)
    {
        if (string.IsNullOrWhiteSpace(_state.RecordedLockedPath))
        {
            return true; // 本来就没有记录
        }

        return recoveredLockedPaths.Any(p => SamePath(p, _state.RecordedLockedPath!));
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? LockedPathFor(string targetPath) =>
        string.IsNullOrWhiteSpace(targetPath) ? null : targetPath + TargetLocker.LockedSuffix;

    private static string JoinNames(IEnumerable<StepOutcome> steps, StepStatus status) =>
        string.Join("；", steps.Where(s => s.Status == status).Select(s => s.Name));

    /// <summary>把本次刚做的改动原路收回，返回收不回来的项。</summary>
    private List<string> Rollback(bool hostsAppliedHere, string? renamedHere)
    {
        var failures = new List<string>();

        if (renamedHere is not null)
        {
            try
            {
                TargetLocker.FromLockedPath(renamedHere).Unlock();
                Say("已回滚本次的文件改名");
            }
            catch (Exception ex)
            {
                failures.Add("回滚文件改名失败：" + ex.Message);
            }
        }

        if (hostsAppliedHere)
        {
            try
            {
                _hosts.Clear();
                Say("已回滚本次的 hosts 改动");
            }
            catch (Exception ex)
            {
                failures.Add("回滚 hosts 改动失败：" + ex.Message);
            }
        }

        return failures;
    }

    private StepOutcome FlushDnsStep()
    {
        try
        {
            var flush = HostsBlocker.FlushDns();
            return flush.Ok
                ? new StepOutcome("刷新 DNS 缓存", StepStatus.Ok)
                : new StepOutcome("刷新 DNS 缓存", StepStatus.Warning, "ipconfig 退出码 " + flush.ExitCode);
        }
        catch (Exception ex)
        {
            return new StepOutcome("刷新 DNS 缓存", StepStatus.Warning, ex.Message);
        }
    }

    /// <summary>落盘状态。<b>返回 false 表示没写成</b> —— 关键路径必须据此中止，不能只写日志。</summary>
    private bool PersistState()
    {
        try
        {
            _state.Save(_statePath);
            _stateFileUsable = true;
            return true;
        }
        catch (Exception ex)
        {
            Say("状态文件写入失败：" + ex.Message);
            return false;
        }
    }

    private void Say(string message)
    {
        Log?.Invoke(message);
        AppPaths.Log(message);
    }
}
