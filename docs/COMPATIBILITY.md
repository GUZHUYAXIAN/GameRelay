# 兼容与验证边界

“源码/本地文件已核对”不能标为“实际运行通过”。以下尚无真实游戏运行验收通过项。

| 客户端/安装特征 | 核对版本 | 已实现 | 接入条件与限制 |
|---|---|---|---|
| 明日方舟 MAA，MAA.exe + MaaCore.dll | 6.18.0 | 按安装目录独立识别；`--config` 选择；读取 gui.new.json；检测自动执行、ADB风险及内置定时 | 原界面开启 RunDirectly、结束 ExitSelf；关闭设备自动检测与全局 ADB 恢复；核实每份独占资源 |
| MFAAvalonia 旧客户端 | 1.8.7、2.2.7 | interface.json + 程序产品元信息；`-c` 配置选择；检查 BeforeTask、AfterTask | 1.8.7 使用 StartupSoftwareAndScript；2.2.7 也有 StartupScriptOnly；需配置完成退出；不传 MaaPiCli 的 -d |
| MFAAvalonia 实例客户端 | 提交 `4f11c8122de4f43eafc818a368c9956e3b06249c` | 按程序集产品提交识别；`--instance`、`--autostart`、`--quit-after-run` | 需真实存在的实例 JSON；不允许 force-start；可能由客户端主动抢前台 |
| MFW 旧参数客户端 | 资源包 v3.2.8 的本地说明 | 分开处理 `-r 资源 -c 配置 -d` 与嵌套配置目录 | 程序未声明版本，不能由资源版本断定 GUI 版本；启动前再次核对指纹与本地参数说明；完成动作需原界面核对 |
| MaaPiCli | 按安装包单独核对 | 独立适配分支、配置存在性检查 | GUI 的 config.json 不能替代 maa_pi_config.json；仅有 EXE 不代表可以无人值守执行；参数/结果仍需对应版本验证 |
| Alas | 安装结构识别 | 只观察 | 不启动、停止、恢复或占普通名额；Python环境与进程身份不充分时显示未确认 |
| AALC | 原包 V1.2.13，入口源码同版本 | 发现、独立登记、外部进程观察 | 源码入口初始化 GUI 并可能请求管理员权限，未核实可供调度的自动执行参数；默认停用并阻止自动启动 |
| OKWW / 鸣潮 | 不负责执行 | 手动鸣潮保护开关 | 没有虚构本机安装路径，不启动、停止或重试 |
| MuMu Player 12 | 本机 MuMuManager `info -v all` 已只读调用 | 读取实例索引和进程/Android状态，资源残留占位 | 不使用全局关闭命令；通过服务创建、无法确认归属的模拟器不自动强关 |

备用 BD2 可通过对应旧 MFA 客户端接入，但首次使用须在原界面完成设备和任务配置。其安装包未有可直接复用的 CLI 用户配置时，管理器不生成猜测配置。AALC 的 GUI/权限/前台操作限制不能通过虚假参数或模拟点击掩盖。

## 主要核对来源

- [MAA v6.18.0 Bootstrapper](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/v6.18.0/src/MaaWpfGui/Main/Bootstrapper.cs)
- [MAA v6.18.0 完成动作](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/v6.18.0/src/MaaWpfGui/Models/PostActionSetting.cs)
- [MFA v1.8.7 配置管理](https://github.com/SweetSmellFox/MFAAvalonia/blob/v1.8.7/MFAAvalonia/Configuration/ConfigurationManager.cs)
- [MFA v1.8.7 启动逻辑](https://github.com/SweetSmellFox/MFAAvalonia/blob/v1.8.7/MFAAvalonia/Views/Windows/RootView.axaml.cs)
- [MFA v2.2.7 启动逻辑](https://github.com/SweetSmellFox/MFAAvalonia/blob/v2.2.7/MFAAvalonia/Views/Windows/RootView.axaml.cs)
- [MFA 实例命令行接口](https://github.com/SweetSmellFox/MFAAvalonia/blob/4f11c8122de4f43eafc818a368c9956e3b06249c/MFAAvalonia/AppRuntime.cs)
- [MaaFramework](https://github.com/MaaXYZ/MaaFramework)、[MFW](https://github.com/overflow65537/MFW-PyQt6)
- [AALC V1.2.13 入口](https://github.com/KIYI671/AhabAssistantLimbusCompany/blob/V1.2.13/main.py)
- [Alas](https://github.com/LmeSzinc/AzurLaneAutoScript)、[OKWW](https://github.com/ok-oldking/ok-wuthering-waves)、[ok-script](https://github.com/ok-oldking/ok-script)

本地 MFW 参数说明用于旧版适配，上游当前主分支已经演进，不能据新文档替换旧参数。
