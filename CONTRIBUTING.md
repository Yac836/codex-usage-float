# 贡献说明

先阅读 README、docs/ARCHITECTURE.md 和 REGRESSION_CHECKLIST.md。项目使用 C# 5 / .NET Framework，避免引入不必要的运行时、常驻服务和依赖。

提交 Pull Request 前：

1. 为修复添加能重现问题的回归用例。测试使用 `FormForTest` 的独立配置和模拟读取；不得调用真实 Codex 账户。
2. 执行 `tests\run.ps1`。修改窗口、置顶或贴边行为时，在交互桌面上另跑 `tests\run.ps1 -Desktop` 并完成人工清单。
3. 运行 `tools\check-source.ps1`。新增公开文件时更新 `tools\public-files.txt`；新增功能时更新回归清单和文档。
4. 描述问题、最终行为、测试结果和未验证的平台。不要把本机配置、认证文件、聊天记录或真实额度快照放进 PR。

报告问题请给出系统版本、系统缩放、程序版本、所选样式、重现步骤，以及预期和实际行为。截图先遮挡个人信息。不需要提供账号、API Key、登录令牌或认证文件。

GitHub Actions 仅对代码进行构建与离线测试。维护者在首次发布前应在 GitHub 上确认工作流通过，并完成实机清单；本地通过不能替代云端首次运行。

提交的贡献按项目 MIT 许可证提供。
