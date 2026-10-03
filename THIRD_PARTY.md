# 第三方组件与来源

当前 GameRelay 源码及便携包没有第三方包依赖，没有复制上游源码。

| 使用对象 | 方式 | 是否随包分发 |
|---|---|---|
| Microsoft .NET Framework 4.8 / WinForms / System.Web.Extensions | 使用系统运行时和类库 | 否 |
| Windows Kernel32 进程与 Job API | 操作系统接口 | 否 |
| Windows 系统图标 | 运行时系统资源 | 否 |
| MAA、MaaFramework 客户端、Alas、AALC | 用户自行安装；路径/元信息/配置检查，核实参数后独立运行 | 否 |
| MuMuManager | 用户原安装中的只读状态接口 | 否 |

GameRelay 不链接或打包 MaaCore、MaaFramework、Python、OCR、模型和游戏资源。上游各自许可证仅适用于其自身项目；GameRelay 没有获得它们的官方背书。

参考上游链接见兼容表。研究下载的源码片段保存在被忽略的 private 目录，禁止放入公开提交或便携包。发布前对最终文件清单再次核对。
