# EOS 数据字典 V6 验证记录

验证日期：2026-09-17

## 完整回归

| 验证项 | 命令 | 结果 | 耗时 |
|---|---|---:|---:|
| .NET 全量测试 | `dotnet test tests\SHB.EosDataDictionary.Tests\SHB.EosDataDictionary.Tests.csproj -c Release --no-restore` | 1020/1020 通过 | 测试 33 秒，墙钟 39.207 秒 |
| Python 工作流测试 | `python -m unittest discover -s tools/tests -p "test_*.py"` | 33/33 通过 | 测试 1.723 秒，墙钟 1.961 秒 |
| 桌面应用 Release 构建 | `dotnet build src\SHB.EosDataDictionary\SHB.EosDataDictionary.csproj -c Release --no-restore` | 0 警告，0 错误 | 1.088 秒 |
| Enricher Release 构建 | `dotnet build tools\SHB.EosDataDictionary.Enricher\SHB.EosDataDictionary.Enricher.csproj -c Release --no-restore` | 0 警告，0 错误 | 1.234 秒 |
| Promoter Release 构建 | `dotnet build tools\SHB.EosDataDictionary.Promoter\SHB.EosDataDictionary.Promoter.csproj -c Release --no-restore` | 0 警告，0 错误 | 1.231 秒 |

全量测试期间 NuGet 漏洞信息服务暂时不可访问，`dotnet test` 输出了 `NU1900` 环境警告；三个 `--no-restore` Release 构建均为 0 警告、0 错误，测试与编译结果不受影响。

## Revision 11 双向兼容

以下 6 个精准兼容测试全部通过，VSTest 总耗时 2.7676 秒：

- V6 新名称层往返后仍保持 `FormatVersion = 1`。
- 新客户端读取不含 V6 可选成员的 Revision 11 快照，并初始化新增集合。
- 新客户端读取显式为 `null` 的新增集合。
- 旧 DataContract 本地载荷无损迁移。
- 匿名读取规范 manifest。
- manifest、完整快照解码及 UI 复用链路可用。

另外使用发布前保存的 Revision 11 客户端程序集，直接调用其原版 `SnapshotCodec.DecodeAndValidate` 读取包含 V6 可选成员的真实候选载荷。读取成功：客户端程序集版本 `1.0.0.0`、`FormatVersion = 1`、`Revision = 11`、表数量 71,953。由此确认旧客户端会忽略未知可选成员，不会因 V6 名称层而无法加载快照。

发布器另有 1 个 linked worktree 回归测试通过，确认 `.git` 为文件或目录时都能执行发布前路径校验。

## 安全与内容扫描

执行了以下扫描：

```powershell
rg -n --hidden '(github_pat_|ghp_|Password\s*=|Pwd\s*=|User ID\s*=|密码\s*[:=])' src tests tools README.md config glossary
rg -n --hidden '[A-Za-z]:\\' src tests tools README.md config glossary
rg -n --hidden '�|鏄|鍙|缂' src tests tools README.md config glossary
rg -n --hidden '\bTODO\b|\bFIXME\b|NotImplementedException' src tests tools README.md config glossary
git diff --check
```

审计结论：

- 未发现真实 GitHub 令牌或硬编码数据库密码。令牌前缀唯一业务范围命中是 DPAPI 加密测试使用的合成值；密码相关生产代码命中均为属性传递或连接构造，不含密码字面量。
- 绝对盘符路径仅存在于测试夹具，用于验证路径脱敏、路径规范化及 Windows 路径兼容；生产源码、工具、配置和 README 无硬编码本机绝对路径。
- 乱码特征仅存在于主动检测乱码的常量和对应回归测试夹具；未发现用户可见的意外乱码。
- `TODO`、`FIXME`、`NotImplementedException` 均无命中，不存在占位实现。
- `git diff --check` 无空白错误；仅有 Git 行尾规范提示，不影响内容。

结论：V6 代码、兼容协议和发布工具通过本阶段验证，可以进入全量候选生成与质量门禁；候选数据仍必须单独通过金标准与覆盖率门禁后才能发布。
