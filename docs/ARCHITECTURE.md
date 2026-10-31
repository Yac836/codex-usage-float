# 实现说明

程序为 .NET Framework WinForms x64 单文件应用。图形界面与用户设置由 `UsageForm` 管理，`UsageReader` 负责后台读取，`UsageSnapshot` 和 `DisplayText` 负责数据映射和显示文本。

## 查询

`UsageReader` 启动独立的 Codex app-server，通过标准输入/输出交换换行分隔的 JSON。调用顺序固定为 initialize、initialized、account/rateLimits/read。没有使用重置卡、发送对话或直接读取认证文件的代码。

优先选择 `rateLimitsByLimitId.codex`，兼容旧版 `rateLimits`。根据 `windowDurationMins` 区分 300 分钟和 10080 分钟；`usedPercent` 为已用比例，`resetsAt` 为 UTC Unix 秒。未返回字段保留为空。

重置卡数量使用 `availableCount`。只处理 status=available、resetType=codexRateLimits 的明细。主界面显示最早的已知 expiresAt；明细不全时明确标注“已知到期”。在服务没有提供完整明细时，不能保证已知日期就是全部卡片中的最早日期。

读取有超时和取消。stderr 被排空而不保存，错误不回显后端私有消息。待处理 stdout 队列有容量限制。辅助进程分配到独立 Job Object，关闭 job 清理自己创建的进程及其后代。

UI 在后台读取结果提交完成前保持刷新锁，防止交错读取覆盖数据；失败保留上次快照。窗口关闭时取消读取并释放资源，不在 UI 线程等待网络。

## 绘制与窗口

圆形采用 `UpdateLayeredWindow`、预乘 Alpha、2 倍采样和缩小；透明角落与实体主体分别处理。原生绘制失败时回退到卡片。卡片使用普通 WinForms 控件。字体按字号和样式复用，所有自有资源在 Dispose 中释放。

置顶同步 WinForms 偏好和原生 WS_EX_TOPMOST，使用 SWP_NOACTIVATE 修复状态；每秒的显示时钟仅在状态不符时调用修复。

贴边分为展开与收起状态。保存展开窗口坐标，小条放在当前工作区内。拖动、按键和菜单展开时不收起。再次启动通过本用户会话的互斥体和窗口消息展开、恢复并居中已有窗口。

找回入口包括系统 `RegisterHotKey` 注册的 Ctrl+Alt+U、NotifyIcon 托盘双击/菜单和重复启动消息，共用展开、恢复最小化和鼠标所在屏幕居中的逻辑。热键设置 MOD_NOREPEAT；占用/注册失败时不宣称注册成功，提供菜单重试。句柄重建时重新绑定，关闭时注销并释放托盘图标资源，没有键盘钩子。

默认组合可在小型原生设置窗口中修改/关闭；分别保存启用标志和最后键位。候选组合在另一个窗口内热键 ID 上注册成功后才释放旧绑定，失败保留旧状态。设置界面直接选择修饰键和按键，避免捕获组合时触发其他全局快捷键；至少需要 Ctrl/Alt，F12 作为系统保留键不提供。编辑时暂停贴边收起。

秒级时钟继续检查置顶和工作区，只在可见倒计时文字或数据失效状态变化时执行完整显示更新，后台读取、设置修改仍立即更新。临时诊断模式和旧自检不进入发布 EXE，自检保留在测试工程。

处理 WM_DISPLAYCHANGE、WM_SETTINGCHANGE 和本会话注册的 WM_WTSSESSION_CHANGE，合并消息后检查工作区。现有一秒显示时钟同时比较工作区列表，在消息遗漏或屏幕信息延迟更新时兜底，不新增高频定时器。按收起前的完整窗口大小计算位置；优先保留仍可完整显示的位置，否则选择最大重叠工作区，完全丢失时优先鼠标屏幕。自动修复使用不激活窗口的移动；主动找回才请求前台。

Windows API 参考：[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[WM_DISPLAYCHANGE](https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange)、[WM_WTSSESSION_CHANGE](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change)。

## 测试边界

测试可注入配置路径、读取函数和圆形上传函数，避免接触真实账户。数据与进程测试使用自有模拟服务；离线布局测试使用同一套实际绘制代码。真实鼠标、不同应用之间的覆盖关系和不同 DPI 显示器仍要实机检查。
