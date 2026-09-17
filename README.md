# EOS 共享数据字典事件仓库

本仓库使用 GitHub Issue 收集公开数据字典修订，并由受控工作流生成不可变事件、逐修订索引和规范快照。

## 应用源码

仓库同时包含数据字典桌面应用源码 `src/`、测试 `tests/`，以及字典增强与快照晋升两个命令行工具：`tools/SHB.EosDataDictionary.Enricher/` 和 `tools/SHB.EosDataDictionary.Promoter/`。`tools/IconGenerator/` 是应用图标生成辅助工具。

EOS 业务系统源码不在本仓库中，也不得复制到本仓库。

在 Windows 和 .NET Framework 4.8 开发环境中执行：

```powershell
dotnet restore tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --no-restore
dotnet build src/SHB.EosDataDictionary/SHB.EosDataDictionary.csproj -c Release --no-restore
dotnet build tools/SHB.EosDataDictionary.Enricher/SHB.EosDataDictionary.Enricher.csproj -c Release
dotnet build tools/SHB.EosDataDictionary.Promoter/SHB.EosDataDictionary.Promoter.csproj -c Release
```

## 权限边界

- 普通公开用户只能维护表和字段的业务元数据，例如中文名、业务含义、模块、用途和备注。
- “正式中文名”是已确认或有可靠依据的名称；橙色“参考译名”只用于人工复核，不会自动冒充正式名称。
- 数据库物理结构只能由 `config/publishers.json` 中登记的不可变数字 GitHub 用户 ID 发布。
- 同一对象、字段和属性按成功处理的 Issue 编号顺序应用，最后一个成功事件覆盖先前值；每个事件仍保留服务端读取到的真实前后值供审计。
- `OperationId` 是全局幂等键，重复提交不会重复写入事件或增加修订号。

## Issue 协议

标题必须以 `[EOS-DICTIONARY-EVENT]` 开头。正文必须且只能包含一个 `json-v1` 代码块；代码块外不能有说明文字，也不能携带 `Overrides` 或未知成员。作者身份只取 GitHub Issue API 返回的数字用户 ID，正文中的作者和客户端时间不参与授权或排序。

单个 Issue 最多包含 100 个操作，单个字符串最长 2000 个字符，规范 JSON 最大 61425 字节，最终 Issue 正文最大 60 KiB。对象键、字段键和操作号只接受协议规定的安全字符，不能包含路径片段。

普通业务属性使用 `ChangeKind = Set`。拒绝当前参考译名使用 `ChangeKind = RejectSuggestion`、`PropertyName = SuggestedChineseName`，`NewValue` 必须是桌面端生成的 64 位小写 SHA-256 候选版本指纹。否决只隐藏文本与证据完全匹配的候选；证据变化会形成新版本，不会被旧指纹误伤。表或字段不存在、指纹格式错误、属性组合错误时，工作流拒绝整个 Issue 且不改变规范快照。

## 日常维护

维护人员克隆仓库并运行桌面应用即可编辑字典。应用先把修改保存在本机 SQLite 队列，再通过 GitHub Issue 提交；仓库工作流校验成功后生成新修订。客户端不会定时强制推送 Git 分支。其他维护人员点击“更新字典”即可拉取已经发布的规范快照；尚未通过工作流的本机修改继续显示为待生效。

## 数据安全

数据库访问永远只读。仓库、Issue、事件、快照和工作流不得保存数据库地址、连接配置、认证密钥、本机目录或其他环境私有信息。结构发布事件只描述公开的数据字典事实，不执行数据库写入。

## 发布顺序

工作流按 Issue number 升序处理所有尚未关闭的事件 Issue。单个 Issue 校验或应用失败时，不修改该 Issue 对应的快照、事件或清单；失败 Issue 只标记并评论，不关闭。成功事件在文件全部生成后提交，推送成功后才评论并关闭对应 Issue。推送冲突时，工作流拉取最新 `main` 并重新扫描尚未关闭的队列。
