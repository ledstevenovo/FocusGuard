namespace FocusGuard.Core;

/// <summary>目标文件的当前状态。</summary>
public enum LockState
{
    /// <summary>原文件在、改名文件不在 —— 可加锁。</summary>
    Unlocked,

    /// <summary>改名文件在、原文件不在 —— 已锁定，可解锁。</summary>
    Locked,

    /// <summary>两份文件同时存在 —— 冲突态，拒绝任何自动操作。</summary>
    Conflict,

    /// <summary>两份都不在。</summary>
    Missing,
}

/// <summary>
/// 通过给目标程序改名来阻止启动：<c>fm.exe</c> → <c>fm.exe.focusguard-locked</c>。
///
/// <b>固定后缀 + 绝不通配</b>：只认 <c>&lt;原文件名&gt;.focusguard-locked</c> 这一个确定的名字，
/// 不做 <c>*-locked</c> 通配扫描 —— 避免误伤用户自己命名的其它文件。
///
/// <b>冲突即拒绝</b>：当原文件与锁定文件同时存在（例如游戏更新/修复工具重新生成了 fm.exe，
/// 或用户手工把文件放了回去），<see cref="Lock"/> 与 <see cref="Unlock"/> 都会抛出异常，
/// 两份文件都不覆盖、不删除，交由用户确认。
///
/// <b>为什么不用文件权限（icacls Deny）</b>：施加是 <c>/deny user:(RX)</c>，解除只能用
/// <c>/remove:d user</c> —— 后者会删掉该用户在这个文件上的<b>全部</b>拒绝规则。如果文件原本就
/// 有拒绝规则（哪怕与启动无关），解锁时会一并抹掉，等于"解锁时放宽了原有限制"。改名只动文件名，
/// 不碰任何权限位，不存在这个问题。
/// </summary>
public sealed class TargetLocker
{
    public const string LockedSuffix = ".focusguard-locked";

    public TargetLocker(string targetPath)
    {
        TargetPath = targetPath ?? string.Empty;
    }

    public string TargetPath { get; }

    public string LockedPath => TargetPath + LockedSuffix;

    public string TargetFileName => Path.GetFileName(TargetPath);

    public bool TargetExists => TargetPath.Length > 0 && File.Exists(TargetPath);

    public bool LockedExists => TargetPath.Length > 0 && File.Exists(LockedPath);

    public LockState State
    {
        get
        {
            if (TargetPath.Length == 0)
            {
                return LockState.Missing;
            }

            var hasTarget = TargetExists;
            var hasLocked = LockedExists;

            if (hasTarget && hasLocked) return LockState.Conflict;
            if (hasLocked) return LockState.Locked;
            if (hasTarget) return LockState.Unlocked;
            return LockState.Missing;
        }
    }

    /// <summary>锁是否处于生效状态（原文件已被移走）。</summary>
    public bool IsLocked => State == LockState.Locked;

    /// <summary>是否需要用户介入（两份文件同时存在）。</summary>
    public bool HasConflict => State == LockState.Conflict;

    /// <summary>幂等；冲突或目标不存在时抛异常，绝不覆盖已有文件。</summary>
    public void Lock()
    {
        switch (State)
        {
            case LockState.Conflict:
                throw new InvalidOperationException(
                    "锁定目标已存在，为保证不覆盖任何文件，已拒绝本次操作。\r\n" +
                    "原文件：" + TargetPath + "\r\n" +
                    "锁定文件：" + LockedPath);

            case LockState.Locked:
                return; // 已经锁好了

            case LockState.Missing:
                throw new FileNotFoundException("目标程序不存在：" + TargetPath, TargetPath);
        }

        try
        {
            File.Move(TargetPath, LockedPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                "无法重命名 " + Path.GetFileName(TargetPath) +
                "：文件可能正被占用，或它上面存在旧的权限限制（后者可用 --remove-legacy-deny 处理）。", ex);
        }
    }

    /// <summary>幂等；冲突时抛异常，两份文件都保留、不做删除或覆盖。</summary>
    public void Unlock()
    {
        switch (State)
        {
            case LockState.Conflict:
                throw new InvalidOperationException(
                    "原文件与锁定文件同时存在，为避免覆盖或删除，本次未做任何改动，请手工确认：\r\n" +
                    "原文件：" + TargetPath + "\r\n" +
                    "锁定文件：" + LockedPath);

            case LockState.Unlocked:
            case LockState.Missing:
                return; // 没什么可还原的
        }

        File.Move(LockedPath, TargetPath);
    }

    public static bool IsLockedFileName(string path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(LockedSuffix, StringComparison.OrdinalIgnoreCase);

    public static TargetLocker FromLockedPath(string lockedPath)
    {
        if (!IsLockedFileName(lockedPath))
        {
            throw new ArgumentException("不是 FocusGuard 的锁定文件：" + lockedPath, nameof(lockedPath));
        }
        return new TargetLocker(lockedPath[..^LockedSuffix.Length]);
    }

    /// <summary>
    /// 在<b>用户显式指定</b>的目录里列出候选锁定文件，用于 state 丢失且路径被改动后的补救。
    /// 仅在这个目录由用户明确指出时才调用；程序自身的自动恢复不使用通配扫描。
    /// </summary>
    public static IReadOnlyList<string> ListCandidatesIn(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory
                .GetFiles(directory, "*" + LockedSuffix, SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
