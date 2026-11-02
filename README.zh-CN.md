# CodexStatusbar — 安全加固分支

[English](README.md)

本项目源自 [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar)，
为 Windows 上的 Codex Desktop 提供状态浮条：显示 token 总量、缓存命中信息和模型阶段速度估算；
展开详情与无界面终端输出还显示上游上报的首 token 时间（TTFT）。

**目前只有源码改动，尚未构建或运行验证。** 没有安全认证或已验证的二进制发行版。
上游历史测试成绩、性能数据不代表本分支通过验证。

## 首轮改动

- 默认不开机启动，必须明确启用。旧版自动启用的配置不视为用户同意；迁移时会移除同名旧启动注册项，明确启用后才恢复。
- 自动主题只跟随 Windows，保留手动深色/浅色选择；不再为了主题读取 Codex 配置。
- 常规调试诊断隐藏会话标识、标题、文件路径及自由文本错误；自由文本生命周期消息不再原样写出。
- IPC 诊断不转储原始消息。Node IPC 探针只输出固定类别计数，默认不保存文件。
- 显式选择的 sessions 目录缺少数据库时，不回退其他用户配置目录；恢复的日志路径须通过根目录范围及 reparse point 检查。
- 会调用模型的开发探针必须传入 `--allow-live-inference`。这些工具并非只读，仍可能继承认证、发送提示词、产生费用和保存内容。
- 必需构建检查缺失或失败时停止发布；只有显式跳过检查才可绕过测试阶段，所得构建仍属未验证。

完整范围、验证记录及后续计划见[安全检查与改进记录](docs/HARDENING_PHASE1.zh-CN.md)。

## 构建与使用

构建 Windows 应用需要 Windows 和兼容的 .NET SDK；项目目标为 .NET 10。
请先审查源码，并使用隔离的测试配置，不要直接授权读取个人会话。
下面仅为后续验证说明，**本次审查没有执行构建或应用程序**。

```powershell
pwsh -File build.ps1
```

构建脚本会运行应用检查并生成可执行文件。`-SkipTest` 会明确跳过检查，得到未验证构建；
发布命令成功不等于通过安全评估。

自行完成构建与验收后，运行 `CodexStatusbar.exe` 使用托盘与浮条。
托盘提供主题、位置和开机启动控制。本分支与上游共享启动注册及应用状态约定，不建议同时运行。

| 参数 | 用途 |
| --- | --- |
| `--no-overlay` | 无浮窗监控，在终端显示指标；仍会访问会话数据。 |
| `--duration SECONDS` | 限制无浮窗监控时长。 |
| `--sessions DIRECTORY` | 指定 sessions 根目录；测试应使用隔离的合成数据。 |
| `--settings FILE` | 指定绝对路径的设置文件。 |
| `--debug` | 开启脱敏诊断；分享前仍应人工检查。 |
| `--background` | 使用后台/托盘启动流程。 |
| `--install-startup` | 明确启用当前用户的 Windows 开机启动。 |
| `--uninstall-startup` | 关闭当前用户的 Windows 开机启动。 |
| `--startup-status` | 查看启动状态；输出可能包含本机程序路径。 |
| `--self-test fixtures` | 构建后运行应用自测；本次未运行。 |
| `--ipc-probe` | 查看脱敏后的本地 IPC 连接与会话识别状态。 |

应用为了展示指标，需要读取 Codex 会话元数据与用量记录；它不是沙箱，也不能阻止同一用户身份的
其他进程访问文件。常规 debug/IPC 之外的专用开发探针仍可能输出敏感数据，不应随意运行或分享结果。

## 指标口径

- **TPS 是模型阶段估算，不是逐 token 流式测速。** 分子为上报的输出 token，分母为包含推理的模型阶段时间窗口。
  部分记录需要推导阶段边界，因此可能含准备或等待时间。紧凑浮条保留 `tok/s` 单位，展开名称注明估算口径。
- **TTFT 是上游上报值，不是本地猜测的首 token 到达事件。** 缺失或非法值显示未知，不当作零；新回合清除旧回合值。
  详情行区分“上报值”和“未上报”。
- token 与缓存指标依赖能读到的日志记录；缺失或不完整的数据不能当作独立测量结果。

## 验证与限制

已通过源码回退守卫、JavaScript 语法检查及 diff 空白检查。
尚未进行 .NET 构建、C# 自测、Windows UI、注册表迁移或真实 IPC 验证；新增 TTFT 详情行仍需视觉与行为验收。

待办包括超大日志资源硬上限、其他开发探针审计、文件系统竞争条件和发布供应链检查。
常规日志脱敏不会清理历史生成文件或仓库历史。

这两份 README 已重新编写，未复制上游真实运行记录、会话标题/标识、个人路径、进程标识或用量样例；
这不代表其他仓库文件或历史版本均已脱敏。

## 来源与许可

上游：[dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar)。
Copyright (c) 2026 CodexStatusbar contributors。

保留原有 [MIT 许可证](LICENSE)、[codex-token-overlay MIT 许可证](LICENSE-codex-token-overlay)
和[第三方声明](THIRD-PARTY-NOTICES.md)。相关依赖版权为 Copyright (c) 2026 soleillevant0125。
本分支不代表上游认可或背书。
