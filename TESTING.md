# Identity Vision Bridge 测试版

涉及地图导入、导出或运行时状态的手工测试必须使用独立的 `Test` 配置，不得启动正式版进行验收。

```powershell
dotnet build IDVBuff.csproj -c Test
.\bin\Test\net10.0-windows10.0.19041.0\IDVBuff.Test.exe
```

测试版具有以下隔离措施：

- 可执行文件名：`IDVBuff.Test.exe`
- 窗口标题：`Identity Vision Bridge（测试版）`
- 数据根目录：`%LocalAppData%\IDVBuff-Test`
- 地图、运行时设置、日志、研究数据、结构缓存和调试数据均位于测试根目录。

正式版继续使用 `%LocalAppData%\IDVBuff`。测试版不会读取、迁移或写入该目录。

自动化单元测试使用 `%TEMP%` 下的随机目录，并在测试结束后清理，不使用上述任一应用数据目录。
