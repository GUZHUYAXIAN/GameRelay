# 配置示例

安全的首运行示例是空库存、空计划、普通上限 3、保护上限 1。程序首次启动自动生成，不随开源仓库分发任何本机库存。

自定义进程的概念示例：

```json
{
  "Name": "示例脚本（默认停用）",
  "Directory": "D:\\ExampleScripts\\Account-A",
  "Entry": "D:\\ExampleScripts\\Account-A\\Runner.exe",
  "Adapter": "Custom",
  "Profile": "daily",
  "Arguments": ["--profile", "daily"],
  "Enabled": false,
  "AutoRunVerified": false,
  "ResourceVerified": false,
  "TimeoutMinutes": 120
}
```

这是字段说明，不是可直接导入的运行状态文件；请通过界面创建，以生成实例 ID、指纹和正确的数据关系。参数仅示意，必须以实际程序接口为准。
