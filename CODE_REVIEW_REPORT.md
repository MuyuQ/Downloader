# WYDownloader 深度代码审查报告

- 审查日期：2026-03-09
- 审查范围：`App.xaml*`、`MainWindow.xaml*`、`Core/*`、`Resources/Config/config.ini`、`WYDownloader.csproj`、`NLog.config`
- 审查方式：静态代码审查 + 构建可行性验证（受限环境）

## 一、结论总览

本次审查共识别 **9 项问题**：

- Critical：1
- High：2
- Medium：4
- Low：2

总体评价：

1. 核心下载/暂停/续传主流程结构完整，异常处理框架基本可用。
2. 存在明显的供应链安全与文件完整性风险，需优先修复。
3. 运行目录写入策略在实际安装场景（如 `Program Files`）下可靠性不足。
4. 解压流程在失败与取消路径上的用户可见状态不一致，存在误导性。

---

## 二、严重问题（Critical）

### C-01 下载产物缺少完整性/真实性校验，存在供应链风险

- 位置：
  - `Core/DownloadManager.cs:134-154`
  - `MainWindow.xaml.cs:508-511`
- 现象：下载完成后直接将 `.part` 重命名为最终文件，并可立即触发自动解压，流程中没有任何哈希（SHA-256）或签名校验。
- 影响：
  - 如果下载链路被污染（镜像站异常、DNS 劫持、中间人代理、源站内容被替换），应用会把篡改后的文件当作成功下载结果继续处理。
  - 对“下载器”场景属于高风险问题。
- 建议修复：
  1. 在 `config.ini` 为每个下载项增加 `SHA256`（或签名公钥信息）。
  2. 下载完成后先校验摘要，再执行“完成”与“自动解压”。
  3. 校验失败时删除产物并提示用户，禁止继续解压。

---

## 三、高优先级问题（High）

### H-01 断点续传仅校验字节起点，跨镜像/源变更时可能静默拼接错误文件

- 位置：
  - `MainWindow.xaml.cs:377-389`（多 URL 镜像回退）
  - `Core/DownloadManager.cs:116-120`（读取既有 `.part`）
  - `Core/DownloadManager.cs:223-227`（仅校验 `Content-Range.From`）
- 现象：续传判断只验证“服务端返回的起始偏移是否匹配”，未校验 ETag/Last-Modified/内容摘要。
- 影响：
  - 当镜像内容不同但文件名一致时，可能把不同版本拼接到同一文件中。
  - 该错误可能不会立刻抛异常，而是在后续解压/执行时暴露。
- 建议修复：
  1. 为 `.part` 旁路保存元数据（源 URL、ETag、Last-Modified、初始 Content-Length）。
  2. 续传前先做 `HEAD` 对比元数据；不匹配则清理 `.part` 全量重下。
  3. 配合 C-01 的最终哈希校验兜底。

### H-02 默认写入应用目录，安装到受限路径时下载/日志易失败

- 位置：
  - `Core/ConfigManager.cs:339`（默认下载目录 = `AppDomain.CurrentDomain.BaseDirectory`）
  - `Core/Logger.cs:98-102`（日志目录 = `${basedir}/logs`）
  - `MainWindow.xaml.cs:334`（运行时创建下载目录）
- 现象：默认将下载文件和日志写入应用安装目录。
- 影响：
  - 安装在 `C:\Program Files\...` 时普通用户通常无写权限，导致下载失败或日志静默丢失（`Logger` 内部吞异常）。
- 建议修复：
  1. 默认下载目录改为用户目录（如 `KnownFolders.Downloads`）。
  2. 日志改为 `%LocalAppData%\WYDownloader\logs`。
  3. 首次启动时探测可写性，不可写则引导用户选择目录。

---

## 四、中优先级问题（Medium）

### M-01 解压失败后仍继续弹“下载完成”，状态语义冲突

- 位置：
  - `MainWindow.xaml.cs:508-511`（调用解压）
  - `MainWindow.xaml.cs:688-696`（解压异常被吞并，不再抛出）
  - `MainWindow.xaml.cs:520-521`（继续显示下载完成提示）
- 现象：解压失败只在 `ExtractZipFile` 内弹错误，但不会中断后续“下载完成”提示。
- 影响：用户会同时看到“解压失败”和“下载完成”，难以判断最终状态。
- 建议修复：让解压失败显式向上抛出，并在主流程区分“下载成功但解压失败”的最终状态文案。

### M-02 解压取消后不回滚半成品目录

- 位置：
  - `MainWindow.xaml.cs:625-626`（取消时 break）
  - `MainWindow.xaml.cs:673-676`（任务后抛取消）
- 现象：取消解压时会留下已解出的部分文件。
- 影响：目录状态不一致，后续用户难以辨别是否可用。
- 建议修复：取消时提供策略：自动清理 / 保留并标记 `_partial` / 让用户确认。

### M-03 ZIP 解压实现重复（UI 层和 Core 层各一份）

- 位置：
  - `Core/DownloadManager.cs:355-411`
  - `MainWindow.xaml.cs:580-697`
- 现象：两份实现存在行为差异（取消、进度更新、异常处理策略不一致）。
- 影响：后续修复容易只改一处，产生行为漂移。
- 建议修复：收敛为单一 Core 服务，UI 仅负责展示。

### M-04 反射日志调用缺少调用级容错

- 位置：`Core/Logger.cs:46-49,58-61,70-73,84-87`
- 现象：`MethodInfo.Invoke` 未包裹调用级 `try/catch`。
- 影响：当日志库运行时异常（配置损坏、目标写入异常）时，可能反向影响业务流程。
- 建议修复：在每个 `Invoke` 周围捕获异常，降级到 `Debug.WriteLine + fallback file`。

---

## 五、低优先级问题（Low）

### L-01 速度显示更新策略会造成“旧值滞留”

- 位置：
  - `Core/DownloadManager.cs:339-345`
  - `MainWindow.xaml.cs:465-468`
- 现象：速度每秒才计算一次，且 UI 只在 `Speed > 0` 时更新。
- 影响：暂停或瞬时抖动时速度文本可能停留在旧值。
- 建议修复：在 `Speed == 0` 时主动显示 `0 B/s` 或“已暂停”。

### L-02 `BeginCloseAfterCancelAsync` 采用 `async void`（非事件处理器）

- 位置：`MainWindow.xaml.cs:747`
- 现象：该方法不是事件签名，但使用 `async void`。
- 影响：异常可观测性与可测试性较差（虽然当前已有局部 `try/catch`）。
- 建议修复：改为 `Task` 并在调用方统一等待/处理。

---

## 六、正向观察（Good Practices）

1. ZIP 解压已实现 Zip Slip 防护（路径归一化 + 根路径前缀检查）：
   - `Core/DownloadManager.cs:377-391`
   - `MainWindow.xaml.cs:630-635`
2. 下载事件在 `finally` 中解除订阅，避免典型事件泄漏：
   - `MainWindow.xaml.cs:538-540`
3. 配置具备本地文件 + 嵌入资源回退机制，提升可运行性：
   - `Core/ConfigManager.cs:25-26`

---

## 七、构建与验证说明

### 1) 构建执行情况

尝试命令：

- `msbuild WYDownloader.sln /p:Configuration=Release`（当前环境无独立 `msbuild.exe`）
- `dotnet build WYDownloader.sln -c Release`

结果：受当前沙箱权限限制，`dotnet build` 在访问 `C:\Users\Mcode\AppData\Local\Microsoft SDKs` 时被拒绝（`MSB4184`），无法完成编译验证。

### 2) 结论约束

本报告基于静态审查得出，未能在本环境完成可执行构建与运行回归。建议在具备本机 SDK 访问权限的开发机/CI 上复核。

---

## 八、建议修复路线图

### P0（本周）

1. 实现下载后哈希/签名校验（C-01）。
2. 增强续传一致性校验（ETag/Last-Modified + sidecar 元数据）（H-01）。
3. 调整默认下载与日志目录到用户可写路径（H-02）。

### P1（下周）

1. 修正解压失败与下载完成状态冲突（M-01）。
2. 规范取消解压的半成品处理策略（M-02）。
3. 合并双份解压实现（M-03）。

### P2（持续改进）

1. 增强 Logger 调用级容错（M-04）。
2. 优化速度显示与关闭流程异步签名（L-01, L-02）。
3. 建立最小自动化回归（下载、暂停/续传、镜像切换、解压取消）。

---

## 九、最小回归用例建议

1. 单源下载成功 + SHA-256 校验通过。
2. 镜像 A 中断后切镜像 B，校验续传策略正确触发“重下”或“继续”。
3. ZIP 下载成功后自动解压，校验路径穿越防护不回归。
4. 解压失败场景（磁盘满/权限不足）下最终状态提示一致。
5. 取消下载与取消解压后，产物清理策略符合预期。

---

审查完成。
