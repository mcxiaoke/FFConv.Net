using System.Runtime.InteropServices;
using MediaCli.Transcode.Run;
using Xunit;

namespace MediaCli.Transcode.Tests;

/// <summary>
/// <see cref="SleepGuard"/> 的行为测试。
///
/// 这些用例能直接断言「系统保持唤醒」的请求<b>真的挂上了、又真的摘掉了</b>，
/// 依据是 <c>SetThreadExecutionState</c> 的返回值语义：它返回的是<b>调用前</b>的
/// 线程执行状态。因此再调用一次即可读出当前状态，无需管理员权限查
/// <c>powercfg /requests</c>。
///
/// 覆盖的契约：配对释放、嵌套不提前解除、重复释放不越界、线程间不串扰。
/// 任何一条破坏，都可能让用户离开电脑后机器再也无法入睡。
/// </summary>
public class SleepGuardTests
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [Fact]
    public void Acquire_ReallyRequestsSystemAwake_AndDisposeRestores()
    {
        var baseline = Power.Peek();
        try
        {
            using (SleepGuard.Acquire())
            {
                // Acquire 之后读出的状态必须精确等于「连续 + 要求系统保持唤醒」
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
            }
            // 释放后必须逐位还原 Acquire 之前的真实状态
            Assert.Equal(baseline, Power.Peek());
        }
        finally
        {
            // 断言失败时也不能把测试机留在「永不睡眠」的状态
            Power.Apply(baseline);
        }
    }

    [Fact]
    public void NestedGuards_InnerReleaseDoesNotLiftSystemRequest()
    {
        var baseline = Power.Peek();
        try
        {
            using (SleepGuard.Acquire())
            {
                using (SleepGuard.Acquire())
                {
                    Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
                }
                // 关键回归点：内层释放后，外层仍在跑 ffmpeg，标志必须依然生效。
                // 若内层把状态还回去了，转码中途机器就会睡着。
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
            }
            Assert.Equal(baseline, Power.Peek());
        }
        finally
        {
            Power.Apply(baseline);
        }
    }

    [Fact]
    public void NestedGuards_ThreeLevels_RestoreOnlyAtOutermost()
    {
        var baseline = Power.Peek();
        try
        {
            using (SleepGuard.Acquire())
            using (SleepGuard.Acquire())
            using (SleepGuard.Acquire())
            {
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
            }
            Assert.Equal(baseline, Power.Peek());
        }
        finally
        {
            Power.Apply(baseline);
        }
    }

    [Fact]
    public void DoubleDispose_DoesNotThrowOrCorruptState()
    {
        var baseline = Power.Peek();
        try
        {
            var guard = SleepGuard.Acquire();
            guard.Dispose();
            guard.Dispose();
            guard.Dispose();

            // 释放后必须还能重新获取（说明 ThreadStatic 深度计数已归零）
            using (SleepGuard.Acquire())
            {
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
            }
            Assert.Equal(baseline, Power.Peek());
        }
        finally
        {
            Power.Apply(baseline);
        }
    }

    [Fact]
    public async Task DepthIsPerThread_NotLeakedAcrossThreads()
    {
        // A 线程 Acquire 后同线程 Dispose，不应影响 B 线程的独立计数：
        // B 在 A 完成之后才取到守卫，必须是一次全新的（最外层）持有，而非续用 A 的计数。
        var a = Task.Run(() =>
        {
            using (SleepGuard.Acquire())
            {
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
            }
            Assert.NotEqual(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
        });
        await a;

        var b = Task.Run(() =>
        {
            using (SleepGuard.Acquire())
            {
                Assert.Equal(ES_CONTINUOUS | ES_SYSTEM_REQUIRED, Power.Peek());
                // B 线程独立计数：再次获取算嵌套，不应触发新的系统请求
                using (SleepGuard.Acquire()) { }
            }
        });
        await b;
    }

    /// <summary>
    /// 直接调用 kernel32 以读取/设置当前线程的执行状态。
    ///
    /// 关键陷阱：<c>SetThreadExecutionState</c> 是<b>替换</b>语义而非叠加 ——
    /// 传 <c>ES_CONTINUOUS</c> 去读数，副作用就是把线程状态改成「只有连续模式」，
    /// 从而抹掉正在生效的 <c>ES_SYSTEM_REQUIRED</c>。因此读完必须立刻写回原值，
    /// 否则探针本身会破坏被观测的状态（嵌套用例就是这么暴露问题的）。
    /// </summary>
    private static class Power
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        /// <summary>无副作用地读取当前线程的执行状态。</summary>
        internal static uint Peek()
        {
            var before = SetThreadExecutionState(ES_CONTINUOUS);
            SetThreadExecutionState(before);
            return before;
        }

        /// <summary>把线程状态强制还原为指定值（测试收尾兜底）。</summary>
        internal static void Apply(uint state) => SetThreadExecutionState(state);
    }
}