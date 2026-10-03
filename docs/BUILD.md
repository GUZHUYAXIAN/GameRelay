# 构建与测试

环境：Windows x64，.NET Framework 4.8，系统目录下的 C# 编译器，PowerShell。没有第三方 NuGet/npm/pip 依赖，不需要 .NET SDK。

```powershell
.\build.ps1 -Test
```

构建生成 `build/GameRelay.exe`、`GameRelay.Core.dll`；测试另生成可控的 `Simulation.exe` 和 `GameRelay.Tests.exe`。测试进程会使用随机临时目录并仅清理自己创建、持有句柄的模拟进程，不启停真实游戏或读取其账号任务。

```powershell
.\package.ps1
```

打包采用显式白名单，不包含 private、data、原始日志、测试程序和上游源码。最终产物为 `dist/GameRelay-0.1.0-win-x64/` 与 ZIP。包使用系统 .NET Framework，不分发运行时。

可选只读/合成诊断：

```powershell
.\build\GameRelay.exe --check <全新可写目录>
.\build\GameRelay.exe --render-preview <预览输出目录>
.\build\GameRelay.exe --measure-idle <性能输出目录>
```

`--check` 验证独立运行和状态文件往返，已有 state.json 时拒绝覆盖。`--render-preview` 仅使用虚构实例，生成离屏界面图片，不操作真实游戏。`--measure-idle` 运行相同的 WinForms 消息循环和后台调度循环，预热约 3 秒后测量约 30 秒，再退出；全部实例强制停用，计划与队列清空。可追加一份管理器 state.json 路径进行只读库存探测性能测量，输出目录因此含私有库存，不应公开。

自动化验证与用户交互验收分别记录。托盘实际交互、不同 DPI/多显示器、Windows 通知策略、真实游戏前台行为还需用户手动验收。
