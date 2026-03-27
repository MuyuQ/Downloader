using System.Threading;

namespace WYDownloader.Core.Threading
{
    /// <summary>
    /// 线程安全的布尔值包装器
    /// 使用原子操作确保多线程环境下的安全读写
    /// </summary>
    /// <remarks>
    /// <para>
    /// 使用场景：当需要在多个线程间共享一个布尔状态时，
    /// 使用此类可以避免竞态条件。
    /// </para>
    /// <para>
    /// 内部使用 <see cref="Interlocked"/> 类的原子操作实现，
    /// 比使用 lock 关键字更高效。
    /// </para>
    /// </remarks>
    /// <example>
    /// 使用示例：
    /// <code>
    /// var isRunning = new ThreadSafeBoolean(false);
    ///
    /// // 在线程 A 中设置值
    /// isRunning.Value = true;
    ///
    /// // 在线程 B 中读取值
    /// if (isRunning.Value)
    /// {
    ///     // 执行操作
    /// }
    /// </code>
    /// </example>
    public class ThreadSafeBoolean
    {
        #region 私有字段

        /// <summary>
        /// 内部存储值
        /// 使用 int 类型以便使用 Interlocked 操作
        /// 0 = false, 1 = true
        /// </summary>
        private int _value;

        #endregion

        #region 公共属性

        /// <summary>
        /// 获取或设置布尔值
        /// 使用原子操作确保线程安全
        /// </summary>
        public bool Value
        {
            get => Interlocked.CompareExchange(ref _value, 0, 0) != 0;
            set => Interlocked.Exchange(ref _value, value ? 1 : 0);
        }

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化线程安全布尔值
        /// </summary>
        /// <param name="initialValue">初始值，默认为 false</param>
        public ThreadSafeBoolean(bool initialValue = false)
        {
            _value = initialValue ? 1 : 0;
        }

        #endregion
    }
}