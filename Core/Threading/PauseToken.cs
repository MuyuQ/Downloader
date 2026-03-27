using System.Threading;
using System.Threading.Tasks;

namespace WYDownloader.Core.Threading
{
    /// <summary>
    /// 暂停令牌源 - 用于控制异步操作的暂停和恢复
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是一个协同暂停机制，需要被暂停的操作主动检查并等待。
    /// 与 <see cref="CancellationTokenSource"/> 类似的设计模式。
    /// </para>
    /// <para>
    /// 使用场景：
    /// - 下载任务的暂停/恢复
    /// - 后台任务的可控暂停
    /// - 需要手动控制的异步流程
    /// </para>
    /// </remarks>
    /// <example>
    /// 使用示例：
    /// <code>
    /// // 创建暂停令牌源
    /// var pauseSource = new PauseTokenSource();
    ///
    /// // 在工作线程中检查暂停
    /// async Task DoWorkAsync()
    /// {
    ///     while (true)
    ///     {
    ///         await pauseSource.Token.WaitWhilePausedAsync();
    ///         // 执行工作...
    ///     }
    /// }
    ///
    /// // 暂停工作
    /// pauseSource.Pause();
    ///
    /// // 恢复工作
    /// pauseSource.Resume();
    /// </code>
    /// </example>
    public class PauseTokenSource
    {
        #region 私有字段

        /// <summary>
        /// 暂停状态的 TaskCompletionSource
        /// 当暂停时创建，恢复时完成
        /// null 表示未暂停状态
        /// </summary>
        /// <remarks>
        /// 使用 volatile 确保多线程可见性
        /// </remarks>
        private volatile TaskCompletionSource<bool> _paused;

        #endregion

        #region 公共属性

        /// <summary>
        /// 获取暂停令牌
        /// 用于传递给需要支持暂停的操作
        /// </summary>
        public PauseToken Token => new PauseToken(this);

        /// <summary>
        /// 获取当前是否处于暂停状态
        /// </summary>
        public bool IsPaused => _paused != null;

        #endregion

        #region 公共方法

        /// <summary>
        /// 暂停操作
        /// 调用此方法后，等待 <see cref="Token"/> 的操作将被阻塞
        /// </summary>
        /// <remarks>
        /// 使用 <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
        /// 确保原子性，防止多次调用创建多个 TCS
        /// </remarks>
        public void Pause()
        {
            // 只有在未暂停时才创建新的 TCS
            Interlocked.CompareExchange(ref _paused, new TaskCompletionSource<bool>(), null);
        }

        /// <summary>
        /// 恢复操作
        /// 调用此方法后，等待的操作将继续执行
        /// </summary>
        /// <remarks>
        /// 使用 <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
        /// 确保原子性，防止恢复时状态不一致
        /// </remarks>
        public void Resume()
        {
            var tcs = _paused;

            // 只有在暂停状态时才恢复
            // 比较并交换：如果当前值等于 tcs，则设置为 null
            if (tcs != null && Interlocked.CompareExchange(ref _paused, null, tcs) == tcs)
            {
                // 完成 TCS，释放等待的任务
                tcs.SetResult(true);
            }
        }

        #endregion

        #region 内部方法

        /// <summary>
        /// 等待暂停解除
        /// 如果当前已暂停，则阻塞直到恢复；否则立即返回
        /// </summary>
        /// <returns>等待任务</returns>
        internal Task WaitWhilePausedAsync()
        {
            var tcs = _paused;
            // 如果未暂停，返回已完成的任务；否则返回等待任务
            return tcs?.Task ?? Task.CompletedTask;
        }

        #endregion
    }

    /// <summary>
    /// 暂停令牌 - 用于检查和等待暂停状态
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是一个值类型（struct），传递开销小。
    /// 通常由 <see cref="PauseTokenSource.Token"/> 创建。
    /// </para>
    /// <para>
    /// 使用 <see cref="WaitWhilePausedAsync"/> 方法在异步操作中等待暂停解除。
    /// </para>
    /// </remarks>
    public struct PauseToken
    {
        #region 私有字段

        /// <summary>
        /// 关联的暂停令牌源
        /// </summary>
        private readonly PauseTokenSource _source;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化暂停令牌
        /// </summary>
        /// <param name="source">暂停令牌源</param>
        public PauseToken(PauseTokenSource source)
        {
            _source = source;
        }

        #endregion

        #region 公共属性

        /// <summary>
        /// 获取当前是否处于暂停状态
        /// </summary>
        public bool IsPaused => _source?.IsPaused ?? false;

        #endregion

        #region 公共方法

        /// <summary>
        /// 异步等待暂停解除
        /// 如果当前已暂停，则阻塞直到恢复；否则立即返回
        /// </summary>
        /// <returns>等待任务</returns>
        /// <remarks>
        /// 此方法应在异步循环中定期调用，以响应暂停请求。
        /// 调用此方法不会阻塞线程，而是使用 async/await 模式。
        /// </remarks>
        /// <example>
        /// <code>
        /// while (true)
        /// {
        ///     // 检查并等待暂停解除
        ///     await pauseToken.WaitWhilePausedAsync();
        ///
        ///     // 执行工作...
        ///     await DoSomeWorkAsync();
        /// }
        /// </code>
        /// </example>
        public Task WaitWhilePausedAsync()
        {
            return _source?.WaitWhilePausedAsync() ?? Task.CompletedTask;
        }

        #endregion
    }
}