using System.Runtime.InteropServices;

namespace MediaCli.Transcode.Run;

/// <summary>
/// 转码期间的防睡眠守卫：ffmpeg 跑到一半被系统睡眠挂起，会让子进程冻结、
/// 进度停滞、并留下损坏的临时产物，因此在 ffmpeg 存活期间强制声明「保持唤醒」。
///
/// 封装 kernel32 的 <c>SetThreadExecutionState</c>。两条必须遵守的约束：
/// <list type="number">
/// <item>该 API 是<b>线程级</b>的，标志只对调用它的线程生效，因此 Acquire 与 Dispose
/// 必须在同一线程成对调用。这里用 <see cref="depth"/>（ThreadStatic 持有深度）
/// 兜住嵌套与异常路径：只有最外层那次真正触碰 API，内层只增减计数，
/// 避免内层把外层记录的「上一状态」覆盖成中间值。</item>
/// <item>进程被强杀（任务管理器 End Task / End Process）时，内核会自动清除该进程
/// 持有的全部 execution state request，因此不会跨进程残留「阻止睡眠」。
/// 优雅取消（CLI Ctrl+C、GUI 关窗/取消）则由 finally 显式还原上一状态。</item>
/// </list>
///
/// 只声明 <c>ES_SYSTEM_REQUIRED</c>：阻止系统进入睡眠，但<b>不</b>阻止屏幕熄灭。
/// 合盖休眠需要 <c>ES_AWAYMODE_REQUIRED</c> 且系统电源设置里放开「允许此设备唤醒电脑」，
/// 属于用户可见的策略变更，不在「强制开启」的默认范围内。
/// </summary>
public sealed class SleepGuard : IDisposable
{
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_CONTINUOUS = 0x80000000;

    /// <summary>当前线程的持有深度，0 表示未持有。ThreadStatic 避免跨线程串扰。</summary>
    [ThreadStatic]
    private static int depth;

    /// <summary>是否是最外层持有者：只有它负责设置与还原标志。</summary>
    private readonly bool owner;

    /// <summary>设置前的原始状态，Dispose 时原样写回。</summary>
    private readonly uint previous;

    private bool disposed;

    private SleepGuard(bool owner, uint previous)
    {
        this.owner = owner;
        this.previous = previous;
    }

    /// <summary>
    /// 取得防睡眠守卫。调用方必须放在 <c>using</c> 或 try-finally 中释放，
    /// 否则该线程会一直阻止系统睡眠。
    /// </summary>
    public static SleepGuard Acquire()
    {
        if (depth > 0)
        {
            depth++;
            return new SleepGuard(owner: false, previous: 0);
        }
        var prev = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        depth = 1;
        return new SleepGuard(owner: true, previous: prev);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (depth > 0) depth--;
        // 内层释放或还有外层持有时，标志继续生效，什么都不做。
        if (!owner || depth > 0) return;
        SetThreadExecutionState(previous);
    }

    // 用 DllImport 而非 LibraryImport：后者要求开启 AllowUnsafeBlocks，
    // 而本项目未启用（现有 FlashWindowEx 同样用 DllImport），此处保持一致。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}