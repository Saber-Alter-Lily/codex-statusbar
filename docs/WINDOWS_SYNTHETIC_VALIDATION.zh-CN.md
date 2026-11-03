# Windows 合成测试验证记录

日期：2026-11-03。用户明确批准在 GitHub 临时 Windows 环境中编译和运行合成自测；不连接个人电脑、不读取真实会话、不调用模型，也不自动发布或安装。

## 本轮结果

- 被测 PR head：`aec6080016c6ed8c787819a7415f4f41b952d65a`。
- 环境：GitHub 托管 Windows Server 2025，.NET SDK `10.0.403`。
- 工作流：[成功运行记录](https://github.com/Saber-Alter-Lily/codex-statusbar/actions/runs/41551131137)。
- 具体 job：[构建与合成自测日志](https://github.com/Saber-Alter-Lily/codex-statusbar/actions/runs/41551131137/job/125638699518)。
- `dotnet build`：成功，**0 warnings、0 errors**。
- 合成自测：**checks: 326、failures: 0、RESULT: PASS**。

这里只记录上述被测版本与对应运行，不把结果自动推广到之后的代码改动。PR 工作流默认检出 GitHub 生成的合并测试版本；上面的标识是该次运行对应的 PR head。

## 执行范围

工作流使用只读仓库权限和固定 SHA 的官方 checkout/setup-dotnet Action，不持久化 checkout 凭据，不传入仓库或用户 secret。编译后调用应用的 `--self-test` 入口，显式指定复制到隔离目录中的两份合成 fixture，以及空的 sessions 目录；`CODEX_HOME`、`TEMP`、`TMP`、`LOCALAPPDATA` 指向本次临时测试目录，结束后清理。

自测入口在启动注册、UI 和 IPC 监控初始化之前返回。覆盖原有纯逻辑检查，以及多会话状态隔离、有界读取、半行续写、文件替换、TTL、资源队列、TTFT 等合成回归；这些断言通过不等于真实桌面交互已经验收。

没有运行 `build.ps1`、live probe、模型调用脚本、安装或发布命令；没有上传可执行文件为 release，没有安装到用户电脑。

## 首轮工作流问题及修复

[首轮运行](https://github.com/Saber-Alter-Lily/codex-statusbar/actions/runs/41550817839)已经编译通过，但测试准备脚本使用 `$home` 变量名。PowerShell 变量名不区分大小写，该名称与只读自动变量 `$HOME` 冲突，因此准备阶段失败，**自测当时尚未启动**。

随后将脚本变量改为 `$codexHome`；第二轮准备成功并完整运行了上述 326 项检查。第一轮失败不被掩盖，也不计作测试通过。

## 仍未验收

- 真实 Codex Desktop 客户端、真实 IPC、实际会话日志和模型服务兼容性。
- 浮条、TTFT 详情与多会话菜单的真实视觉/交互表现、多 DPI 和窗口切换。
- 实际注册表启动注册/迁移、热键或其他桌面集成。
- 全进程长期资源占用、其他开发探针、同用户文件竞争条件及正式发布供应链。

没有安全认证或正式二进制发行承诺。首轮及第二轮报告保留各自写作时的“尚未运行”历史状态，本记录补充之后取得的运行证据，而不改写历史。
