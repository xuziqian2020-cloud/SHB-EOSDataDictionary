# EOS 数据字典 V6 精准语义提升 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 EOS 新人看到的“正式中文表名、正式中文字段名、枚举、模块归属和关联关系”都能追溯到知识库或真实业务代码；不够可靠的结果只作为参考译名展示，并生成通过质量门禁的新规范快照。

**Architecture:** 以 Revision 11 为不可变基线，在源码采集层提取 SQL 别名、界面标题、资源文本、DataMap、CASE、实体和调用路径证据；在语义层以“正式名称/参考译名”双层模型处理证据、冲突和拒绝；在发布层只提升通过金标准、覆盖率、冲突率和兼容性门禁的候选快照。现有 format 1 协议保持不变，只新增可选成员，确保 Revision 11 客户端仍能读取。

**Tech Stack:** C# 7.3、WPF、.NET Framework 4.8、System.Data.SQLite、MSTest、Python 3.12、GitHub Actions、Git、EOS VB.NET 源码只读分析。

---

## 工作目录与不可突破的边界

- 所有 Git 命令均从数据字典仓库根目录执行，使用 `git rev-parse --show-toplevel` 获取 `$repo`。
- 当前应用源码根目录由 `$source = Split-Path (Split-Path $repo -Parent) -Parent` 得到。
- EOS 只读源码根目录通过仅存在于当前进程的 `$env:EOS_SOURCE_ROOT` 提供，知识库为其 `docs_knowledge` 子目录；该环境变量不得写入仓库。
- 本机活动字典由 `Join-Path $env:LOCALAPPDATA 'SHB\EosDataDictionary\dictionary.db'` 得到。
- 新增或重写的类、方法和函数必须有 `/// <summary>XMZADD 20260910 中文意图</summary>`；关键业务注释只说明 Why。
- 不修改 EOS 源码根目录，不连接生产库写入，不提交 EOS 源文件、数据库连接、令牌、本机绝对路径或长源码片段。
- 所有候选先写临时数据库；任一门禁失败时保留 Revision 11、本机活动数据库和远程 `main` 不变。
- 每一项任务都先写失败测试、再做最小实现、再跑相关测试；不得用批量替换覆盖用户已有改动。

## 完成定义

- 1,287 张有 EOS 实体的表全部完成正式名/参考名/冲突分类。
- 16,039 个业务代码实际使用字段全部完成正式名/参考名/冲突分类，不能再出现无解释的全英文正式中文名。
- `op_createtime` 的正式名为“操作记录创建时间”；`DA_Acceptance.Owner_Company_ID` 的正式名为“货主公司ID”。
- `Account_Pallet_FA_Not_IO` 为“非托盘出入库流水账”，不得因 `Account` 归入财务；`DA_Account` 为“财务流水账”。
- 正式名称金标准准确率不低于 99%，参考译名金标准准确率不低于 95%，关系方向准确率不低于 98%。
- .NET 全量测试、Python 工作流测试、72,000 表搜索性能测试、旧客户端兼容测试全部通过。
- 新快照、本机数据库、构建产物和 GitHub `main` 使用同一 revision 与同一 payload SHA-256。

## Task 1：把数据字典应用源码纳入同一个受控 Git 仓库

**Files:**
- Modify: `.gitignore`
- Add: `global.json`
- Add: `src/SHB.EosDataDictionary/**`
- Add: `tests/SHB.EosDataDictionary.Tests/**`
- Add: `tests/SHB.EosDataDictionary.Tests/TestData/evidence_rejection_vectors.txt`
- Add: `tools/IconGenerator/**`
- Add: `tools/SHB.EosDataDictionary.Enricher/**`
- Add: `tools/SHB.EosDataDictionary.Promoter/**`
- Modify: `tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj`
- Modify: `README.md`

- [ ] **Step 1: 确认实施分支和导入边界**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
git -C $repo fetch origin
git -C $repo branch --show-current
git -C $repo status --short
```

Expected: 当前分支为 `codex/eos-dictionary-accuracy-v6` 且工作区干净；不得出现数据库、二进制或连接配置。

- [ ] **Step 2: 先扩展忽略清单**

```gitignore
__pycache__/
*.py[cod]
bin/
obj/
TestResults/
artifacts/
logs/
*.db
*.db-shm
*.db-wal
*.exe
*.dll
*.pdb
*.user
*.suo
.vs/
```

- [ ] **Step 3: 只复制可公开的应用源码**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$source = Split-Path (Split-Path $repo -Parent) -Parent
Copy-Item -LiteralPath (Join-Path $source 'global.json') -Destination $repo -Force
& robocopy (Join-Path $source 'src') (Join-Path $repo 'src') /E /XD bin obj .vs TestResults /XF *.user *.suo *.db *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '复制 src 失败。' }
& robocopy (Join-Path $source 'tests') (Join-Path $repo 'tests') /E /XD bin obj TestResults /XF *.user *.suo *.db *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '复制 tests 失败。' }
& robocopy (Join-Path $source 'tools\IconGenerator') (Join-Path $repo 'tools\IconGenerator') /E /XD bin obj /XF *.user *.suo *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '复制 IconGenerator 失败。' }
& robocopy (Join-Path $source 'tools\SHB.EosDataDictionary.Enricher') (Join-Path $repo 'tools\SHB.EosDataDictionary.Enricher') /E /XD bin obj /XF *.user *.suo *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '复制 Enricher 失败。' }
& robocopy (Join-Path $source 'tools\SHB.EosDataDictionary.Promoter') (Join-Path $repo 'tools\SHB.EosDataDictionary.Promoter') /E /XD bin obj /XF *.user *.suo *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '复制 Promoter 失败。' }
$fixtureDirectory = Join-Path $repo 'tests\SHB.EosDataDictionary.Tests\TestData'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'tools\tests\evidence_rejection_vectors.txt') -Destination $fixtureDirectory -Force
```

把测试项目中的共享向量路径改为仓库现有文件：

```xml
<None Include="TestData\evidence_rejection_vectors.txt" Link="evidence_rejection_vectors.txt">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

- [ ] **Step 4: 扫描敏感信息和误导入文件**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
git -C $repo status --short
rg -n --hidden --glob '!snapshot/**' --glob '!events/**' --glob '!.git/**' '(github_pat_|ghp_|Password\s*=|Pwd\s*=|User ID\s*=|密码\s*[:=]|服务器\s*[:=]\s*[0-9]{1,3}(\.[0-9]{1,3}){3})' $repo
git -C $repo ls-files --others --exclude-standard | rg '(\.db($|-)|\.exe$|\.dll$|\.pdb$|appsettings.*\.json$|\.config$)'
```

Expected: 两次扫描均无命中；如果有命中，先从导入集移除并重新扫描。

- [ ] **Step 5: 构建基线并提交**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
dotnet restore (Join-Path $repo 'tests\SHB.EosDataDictionary.Tests\SHB.EosDataDictionary.Tests.csproj')
dotnet test (Join-Path $repo 'tests\SHB.EosDataDictionary.Tests\SHB.EosDataDictionary.Tests.csproj') -c Release --no-restore
git -C $repo add .gitignore README.md global.json src tests tools/IconGenerator tools/SHB.EosDataDictionary.Enricher tools/SHB.EosDataDictionary.Promoter
git -C $repo commit -m "build: version EOS dictionary application source"
```

Expected: 当前 838 个 .NET 测试全部通过，提交不包含 EOS 业务源码、数据库或凭据。

## Task 2：增加正式名称与参考译名双层数据模型，并保持 format 1 兼容

**Files:**
- Modify: `src/SHB.EosDataDictionary/Models/MetadataModels.cs`
- Modify: `src/SHB.EosDataDictionary/Services/SnapshotCodec.cs`
- Modify: `src/SHB.EosDataDictionary/Services/SnapshotPruningService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/PublicSnapshotEvidenceSanitizer.cs`
- Modify: `src/SHB.EosDataDictionary/Services/DictionaryEventApplyService.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/MetadataModelTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/SnapshotCodecTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/SnapshotPruningServiceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/StructurePublishServiceTests.cs`

- [ ] **Step 1: 写模型默认值和往返失败测试**

```csharp
/// <summary>XMZADD 20260910 验证新增参考译名成员默认可用且不破坏旧快照。</summary>
[TestMethod]
public void MetadataModels_NewNameLayers_AreInitialized()
{
    TableMetadata table = new TableMetadata();
    FieldMetadata field = new FieldMetadata();
    Assert.IsNotNull(table.AlternativeChineseNames);
    Assert.IsNotNull(table.RejectedSuggestionFingerprints);
    Assert.IsNotNull(table.UsedByModules);
    Assert.IsNotNull(field.AlternativeChineseNames);
    Assert.IsNotNull(field.RejectedSuggestionFingerprints);
}
```

另在 `SnapshotCodecTests` 增加一次 encode/decode 往返，断言 `FormatVersion == 1` 且新增成员值保持不变；增加一份不含新成员的 Revision 11 JSON，断言反序列化后列表不为 null。

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~MetadataModelTests|FullyQualifiedName~SnapshotCodecTests" --no-restore
```

Expected: FAIL，新增成员尚不存在。

- [ ] **Step 3: 添加可选成员，不提升协议版本**

```csharp
public MetadataValue SuggestedChineseName { get; set; }
public IList<MetadataValue> AlternativeChineseNames { get; set; }
public IList<string> RejectedSuggestionFingerprints { get; set; }
```

`TableMetadata` 再增加：

```csharp
public IList<MetadataValue> UsedByModules { get; set; }
```

构造函数必须初始化全部列表；`SnapshotCodec`、裁剪、公开净化、事件应用中的手工克隆和确定性排序同步复制这些成员。正式名称继续使用 `ChineseName`，不得复用 `OriginalAutomaticValue` 存参考译名。

- [ ] **Step 4: 验证兼容、裁剪和公开安全**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~MetadataModelTests|FullyQualifiedName~SnapshotCodecTests|FullyQualifiedName~SnapshotPruningServiceTests|FullyQualifiedName~StructurePublishServiceTests" --no-restore
```

Expected: PASS；format 仍为 1；公开快照不含绝对路径、凭据和长源码正文。

- [ ] **Step 5: 提交**

```powershell
git add src/SHB.EosDataDictionary/Models/MetadataModels.cs src/SHB.EosDataDictionary/Services/SnapshotCodec.cs src/SHB.EosDataDictionary/Services/SnapshotPruningService.cs src/SHB.EosDataDictionary/Services/PublicSnapshotEvidenceSanitizer.cs src/SHB.EosDataDictionary/Services/DictionaryEventApplyService.cs tests/SHB.EosDataDictionary.Tests
git commit -m "feat: add official and suggested name layers"
```

## Task 3：建立可靠的 EOS 源码解码和证据分级

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/SourceTextDecoder.cs`
- Modify: `src/SHB.EosDataDictionary/Services/EosSourceAnalyzer.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/EosSourceAnalyzerTests.cs`

- [ ] **Step 1: 写 UTF-8 BOM、UTF-8、GB18030 和乱码拒绝测试**

```csharp
/// <summary>XMZADD 20260910 验证老 EOS 中文源码按确定性编码读取。</summary>
[TestMethod]
public void Decode_Gb18030Source_PreservesChineseCaption()
{
    byte[] bytes = Encoding.GetEncoding("GB18030").GetBytes(".Cols(\"Owner_Company_ID\").Caption = \"货主公司ID\"");
    SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);
    Assert.AreEqual("货主公司ID", result.Text.Substring(result.Text.IndexOf("货主公司ID", StringComparison.Ordinal), 6));
    Assert.AreEqual("GB18030", result.EncodingName);
}
```

另加测试：严格 UTF-8 优先；含 `�`、`鏄`、`鍙`、`缂` 的候选降低可信度且不得产出正式中文名。

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EosSourceAnalyzerTests" --no-restore
```

Expected: FAIL，当前仅严格 UTF-8 后回退系统默认编码。

- [ ] **Step 3: 最小实现解码顺序和证据强度**

```csharp
public enum SourceEvidenceStrength
{
    NamingOnly = 0,
    Contextual = 1,
    DirectBusinessCode = 2,
    Authoritative = 3
}

public enum SourceUsageKind
{
    Unknown = 0,
    Read = 1,
    Write = 2,
    Display = 3,
    Relation = 4,
    Enumeration = 5
}
```

`SourceTextDecoder.Decode` 顺序固定为 BOM、严格 UTF-8、GB18030；多编码都成功时使用中文有效字符、控制字符和典型乱码片段评分。`EosSourceAnalyzer` 排除 `bin/obj/packages/logs/log/backup/.vs/TestResults`，并标记生成实体文件、Designer 文件和真实业务文件，生成文件只证明物理映射，不能单独证明业务中文名。

- [ ] **Step 4: 运行分析器测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EosSourceAnalyzerTests" --no-restore
git add src/SHB.EosDataDictionary/Services/SourceTextDecoder.cs src/SHB.EosDataDictionary/Services/EosSourceAnalyzer.cs tests/SHB.EosDataDictionary.Tests/EosSourceAnalyzerTests.cs
git commit -m "feat: classify and decode EOS source evidence"
```

Expected: PASS，乱码文本只进入冲突报告，不污染正式名称。

## Task 4：从 SQL 别名、界面标题和资源文本提取字段中文名

**Files:**
- Modify: `src/SHB.EosDataDictionary/Services/EosBusinessUsageEvidenceExtractor.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessCodeNameInferenceService.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/EosBusinessUsageEvidenceExtractorTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/BusinessCodeNameInferenceServiceTests.cs`

- [ ] **Step 1: 写直接列别名、计算别名、资源后备文本的失败测试**

```csharp
/// <summary>XMZADD 20260910 验证 SQL 直接列中文别名可作为字段业务证据。</summary>
[TestMethod]
public void Extract_SelectDirectChineseAlias_MapsToPhysicalField()
{
    string[] lines = { "Dim sql = \"SELECT A.Owner_Company_ID AS 货主公司ID FROM DA_Acceptance A\"" };
    IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
        "ERP/DA/AcceptanceQuery.vb", "DA", lines);
    SourceEvidence caption = FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlColumnAlias");
    Assert.IsNotNull(caption);
    Assert.AreEqual("货主公司ID", caption.ChineseNameCandidate);
}

/// <summary>XMZADD 20260910 防止计算列标题误写回任一物理字段。</summary>
[TestMethod]
public void Extract_SelectComputedAlias_DoesNotNamePhysicalField()
{
    string[] lines = { "Dim sql = \"SELECT A.Price * A.Quantity AS 金额 FROM DA_Acceptance A\"" };
    IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
        "ERP/DA/AcceptanceQuery.vb", "DA", lines);
    Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Price", "SqlColumnAlias"));
    Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Quantity", "SqlColumnAlias"));
}
```

再增加 `.Cols("op_createtime").Caption = GetResourceText("OperationTime", "操作记录创建时间")`、`DataColumn.Caption` 和续行 SQL 测试。

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EosBusinessUsageEvidenceExtractorTests|FullyQualifiedName~BusinessCodeNameInferenceServiceTests" --no-restore
```

Expected: 至少直接中文别名和资源后备文本测试失败。

- [ ] **Step 3: 实现无表达式污染的别名解析**

解析 `SELECT` 投影时只接受单一 `alias.field` 或可唯一归属的裸字段；通过 `FROM/JOIN` 别名表解析回物理表。中文 `AS` 和隐式中文别名记为 `DirectBusinessCode + Display`。包含函数、运算符、字符串拼接、聚合或多个字段的表达式只记“派生显示列”，不得命名物理字段。

资源函数只取代码中显式中文后备参数：

```csharp
private static readonly Regex ResourceFallbackRegex = new Regex(
    @"GetResourceText\s*\(\s*[^,]+,\s*[""'](?<caption>[^""']*[\u4e00-\u9fff][^""']*)[""']\s*\)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

同一字段在两个独立业务文件得到相同标题时升级为正式候选；单文件标题保持直接代码候选并接受冲突检查。

- [ ] **Step 4: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EosBusinessUsageEvidenceExtractorTests|FullyQualifiedName~BusinessCodeNameInferenceServiceTests" --no-restore
git add src/SHB.EosDataDictionary/Services/EosBusinessUsageEvidenceExtractor.cs src/SHB.EosDataDictionary/Services/BusinessCodeNameInferenceService.cs tests/SHB.EosDataDictionary.Tests/EosBusinessUsageEvidenceExtractorTests.cs tests/SHB.EosDataDictionary.Tests/BusinessCodeNameInferenceServiceTests.cs
git commit -m "feat: infer field captions from business code"
```

Expected: PASS；SQL 输出标题不会错误污染输入字段。

## Task 5：从 DataMap、CASE 和枚举定义补全枚举值

**Files:**
- Modify: `src/SHB.EosDataDictionary/Services/EosBusinessUsageEvidenceExtractor.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessCodeNameInferenceService.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/EosBusinessUsageEvidenceExtractorTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/BusinessCodeNameInferenceServiceTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
/// <summary>XMZADD 20260910 验证网格 DataMap 进入字段枚举字典。</summary>
[TestMethod]
public void Extract_GridDataMap_ProducesEnumerationItems()
{
    string[] lines =
    {
        "Dim sql = \"SELECT A.Status FROM DA_Acceptance A\"",
        "fg.Cols(\"Status\").DataMap = New Hashtable From {{0, \"未审核\"}, {1, \"已审核\"}}"
    };
    IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
        "ERP/DA/AcceptanceQuery.vb", "DA", lines);
    SourceEvidence pending = FindEvidence(evidence, "DA_Acceptance", "Status", "GridColumnDataMap");
    Assert.IsNotNull(pending);
    Assert.AreEqual("0", pending.EnumValue);
    Assert.AreEqual("未审核", pending.EnumChineseName);
}
```

另加 `CASE A.Status WHEN 0 THEN '未审核' WHEN 1 THEN '已审核' END AS 状态`、VB `Select Case`、命名 Enum 属性映射测试；动态变量或计算 CASE 不得冒充确定枚举。

- [ ] **Step 2: 实现并去重枚举证据**

枚举项按 `字段稳定键 + 值` 合并；相同值出现不同中文时保留全部证据并标冲突，不能按最后一次覆盖。只有字段归属唯一且值为常量时才进入 `EnumItems`。

```csharp
private static string BuildEnumItemKey(string schemaName, string objectName, string fieldName, string value)
{
    return string.Concat(schemaName, "|", objectName, "|", fieldName, "|", value).ToUpperInvariant();
}
```

- [ ] **Step 3: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EosBusinessUsageEvidenceExtractorTests|FullyQualifiedName~BusinessCodeNameInferenceServiceTests" --no-restore
git add src/SHB.EosDataDictionary/Services/EosBusinessUsageEvidenceExtractor.cs src/SHB.EosDataDictionary/Services/BusinessCodeNameInferenceService.cs tests/SHB.EosDataDictionary.Tests
git commit -m "feat: infer field enumerations from EOS code"
```

Expected: PASS，枚举冲突可见且不会静默覆盖。

## Task 6：实现正式名称准入、参考译名和拒绝指纹

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/BusinessNameLayerService.cs`
- Create: `src/SHB.EosDataDictionary/Services/NameSuggestionFingerprintService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/MetadataEvidencePolicy.cs`
- Modify: `src/SHB.EosDataDictionary/Services/MetadataEnrichmentService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessDictionaryV1EnrichmentService.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/BusinessNameLayerServiceTests.cs`
- Modify: `tests/SHB.EosDataDictionary.Tests/MetadataEvidencePolicyTests.cs`
- Modify: `tests/SHB.EosDataDictionary.Tests/MetadataEnrichmentServiceTests.cs`

- [ ] **Step 1: 写准入矩阵失败测试**

```csharp
/// <summary>XMZADD 20260910 验证单纯词法翻译不能成为正式业务名称。</summary>
[TestMethod]
public void Apply_NamingOnlyCandidate_IsStoredAsSuggestion()
{
    TableMetadata table = CreateTable("Account_Storage_Part_Definition");
    MetadataValue candidate = CreateCandidate("仓储区定义", "IdentifierTranslation", 70);
    new BusinessNameLayerService().ApplyTableCandidate(table, candidate);
    Assert.AreEqual(string.Empty, table.ChineseName.Value);
    Assert.AreEqual("仓储区定义", table.SuggestedChineseName.Value);
}
```

必须覆盖：人工锁定、数据库注释、知识库精确命中、单个直接 UI 标题、两个独立文件一致、两个强证据冲突、已拒绝指纹再次出现、Revision 11 弱正式值迁移到参考译名。

- [ ] **Step 2: 实现明确的准入规则**

```csharp
/// <summary>XMZADD 20260910 判断候选是否达到新人可依赖的正式名称标准。</summary>
public bool CanPromoteToOfficial(MetadataValue candidate, IList<MetadataValue> agreeingEvidence, bool hasConflict)
{
    if (candidate == null || string.IsNullOrWhiteSpace(candidate.Value) || hasConflict)
    {
        return false;
    }

    if (candidate.IsManualOverride || candidate.IsLocked || IsDatabaseComment(candidate) || IsExactKnowledge(candidate))
    {
        return true;
    }

    return IsDirectStrongCode(candidate) || CountIndependentAgreeingSources(agreeingEvidence) >= 2;
}
```

指纹固定为“规范化值 + 排序后的证据类型/规则/相对位置”计算 SHA-256；拒绝只屏蔽同一证据版本，证据改变后允许产生新指纹。弱值、伪中文和只有缩写拼接的值从 `ChineseName` 移到 `SuggestedChineseName`；人工值和强证据值不降级。

- [ ] **Step 3: 接入富化流水线**

顺序固定为：清洗 Revision 11 弱值 → 收集所有候选 → 合并证据 → 检测冲突 → 正式准入/参考分流 → 精确业务规则兜底 → 最终伪中文检测。不得由后运行的弱规则覆盖先运行的强规则。

- [ ] **Step 4: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~BusinessNameLayerServiceTests|FullyQualifiedName~MetadataEvidencePolicyTests|FullyQualifiedName~MetadataEnrichmentServiceTests|FullyQualifiedName~BusinessDictionaryV1EnrichmentServiceTests" --no-restore
git add src/SHB.EosDataDictionary/Services tests/SHB.EosDataDictionary.Tests
git commit -m "feat: enforce evidence-based business name admission"
```

Expected: PASS；任何弱翻译都不再伪装成已确认名称。

## Task 7：加强字段和表语义，修复 Owner、操作时间与 Account 误判

**Files:**
- Modify: `src/SHB.EosDataDictionary/Services/BusinessIdentifierSemanticService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/IdentifierTranslationService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessSemanticRuleService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessCodeNameInferenceService.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/BusinessIdentifierSemanticServiceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/IdentifierTranslationServiceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/BusinessCodeNameInferenceServiceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/BusinessDictionaryV1EnrichmentServiceTests.cs`

- [ ] **Step 1: 用完整富化链路固化用户点名的回归案例**

```csharp
/// <summary>XMZADD 20260910 验证弱伪中文不会阻止完整业务语义进入正式名称。</summary>
[TestMethod]
public void Enrich_DaAcceptanceWeakNames_AreReplacedByCompleteBusinessNames()
{
    SnapshotData snapshot = new SnapshotData
    {
        Tables = new List<TableMetadata>
        {
            new TableMetadata
            {
                ObjectName = "DA_Acceptance",
                ChineseName = new MetadataValue { Value = "验收单", Status = ConfidenceStatus.CodeEvidence },
                ModuleName = new MetadataValue { Value = "财务", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "Owner_Company_ID", ChineseName = new MetadataValue { Value = "Owner公司ID", Status = ConfidenceStatus.CodeEvidence } },
                    new FieldMetadata { FieldName = "op_createtime", ChineseName = new MetadataValue { Value = "op创建时间", Status = ConfidenceStatus.CodeEvidence } }
                }
            }
        }
    };
    new BusinessDictionaryV1EnrichmentService().Enrich(snapshot, new List<SourceEvidence>(), null);
    Assert.AreEqual("货主公司ID", snapshot.Tables[0].Fields[0].ChineseName.Value);
    Assert.AreEqual("操作记录创建时间", snapshot.Tables[0].Fields[1].ChineseName.Value);
}
```

再加入 `kisnumber=金蝶编码`、`Spec=规格`、`Currency=币别`、`Unit=单位`、`Inv=库存`、`Program=项目`、`Money=金额`、`Creator=创建人`、`Using=使用`、`CompanyID=公司ID` 的边界测试；完整业务短语优先于逐词翻译。

- [ ] **Step 2: 固化 Account 的上下文判定**

```csharp
/// <summary>XMZADD 20260910 防止仓储流水账因 Account 被误归财务。</summary>
[TestMethod]
public void Apply_StorageAccountTable_UsesLedgerMeaningWithoutFinanceModule()
{
    SnapshotData snapshot = new SnapshotData
    {
        Tables = new List<TableMetadata>
        {
            new TableMetadata
            {
                ObjectName = "Account_Pallet_FA_Not_IO",
                ChineseName = new MetadataValue { Value = "财务非托盘出入库表", Status = ConfidenceStatus.Guessed },
                ModuleName = new MetadataValue { Value = "财务", Status = ConfidenceStatus.Guessed }
            }
        }
    };
    BusinessSemanticRuleService.Apply(snapshot);
    Assert.AreEqual("非托盘出入库流水账", snapshot.Tables[0].ChineseName.Value);
    Assert.AreNotEqual("财务", snapshot.Tables[0].ModuleName.Value);
}
```

`Account` 只在 `DA_`、会计科目、凭证、借贷、结算等财务强上下文中解释为财务账；在库存、托盘、出入库、事件上下文解释为“流水账/台账”。

- [ ] **Step 3: 用表实体、窗体、SQL 动作和邻接字段共同定语义**

`Owner`、`Source`、`Using`、`Account` 等多义词必须读取表名、实体属性、所在窗体、写入/读取动作和同表字段。仅字段英文不足时输出参考译名并给出“缺少业务上下文”的原因，不能强行升正式名。

- [ ] **Step 4: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~BusinessIdentifierSemanticServiceTests|FullyQualifiedName~IdentifierTranslationServiceTests|FullyQualifiedName~BusinessCodeNameInferenceServiceTests|FullyQualifiedName~BusinessDictionaryV1EnrichmentServiceTests" --no-restore
git add src/SHB.EosDataDictionary/Services tests/SHB.EosDataDictionary.Tests
git commit -m "fix: infer EOS identifiers from business context"
```

Expected: PASS，点名案例和缩写案例均保持精确中文。

## Task 8：重做模块归属为“主模块 + 被使用模块”

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/BusinessModuleAttributionService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/MetadataEnrichmentService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/EosSourceAnalyzer.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/BusinessModuleAttributionServiceTests.cs`

- [ ] **Step 1: 写权重和跨模块失败测试**

```csharp
/// <summary>XMZADD 20260910 验证实体生成目录不能主导业务模块。</summary>
[TestMethod]
public void Attribute_EntityDefinitionAndWarehouseWrite_PicksWarehouseAsPrimary()
{
    TableMetadata table = CreateTable("Account_Storage_IO");
    IList<SourceEvidence> evidence = CreateModuleEvidence("表-类定义", SourceUsageKind.Read, "仓储与库存", SourceUsageKind.Write);
    new BusinessModuleAttributionService().Apply(table, evidence);
    Assert.AreEqual("仓储与库存", table.ModuleName.Value);
    AssertContainsModule(table.UsedByModules, "仓储与库存");
}
```

另测：知识库精确模块优先；采购写入、财务读取时主模块为采购管理且被使用模块包含财务管理；只有生成实体路径时主模块保持待确认。

- [ ] **Step 2: 实现稳定模块映射和权重**

主模块优先级：知识库精确归属 > 核心写入业务窗体/服务 > 菜单与流程入口 > 多个独立读取位置 > 目录命名。`ERP/表-类定义`、Designer、公共数据库层不参与主模块投票。规范模块至少统一为：物料与BOM、仓储与库存、采购管理、销售与客户、生产制造、计划管理、质量管理、财务管理、人力资源、设备与工装、系统配置、日志与审计、文件与图纸、其他。

- [ ] **Step 3: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~BusinessModuleAttributionServiceTests|FullyQualifiedName~MetadataEnrichmentServiceTests|FullyQualifiedName~EosSourceAnalyzerTests" --no-restore
git add src/SHB.EosDataDictionary/Services tests/SHB.EosDataDictionary.Tests
git commit -m "feat: attribute primary and consuming business modules"
```

Expected: PASS；`Account_Pallet_FA_Not_IO` 不再被单词 Account 拉入财务。

## Task 9：以物理 FK、实体关系和 SQL JOIN 交叉验证关联关系

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/CodeRelationEvidenceService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessDictionaryV1EnrichmentService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/LogicalRelationDiscoveryService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/RelationInferenceService.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/CodeRelationEvidenceServiceTests.cs`
- Modify: `tests/SHB.EosDataDictionary.Tests/RelationInferenceTests.cs`

- [ ] **Step 1: 写关系置信规则失败测试**

```csharp
/// <summary>XMZADD 20260910 验证实体关系与 SQL JOIN 一致时形成正式代码关系。</summary>
[TestMethod]
public void Build_EntityAndJoinAgree_PromotesCodeRelation()
{
    SnapshotData snapshot = CreatePurchaseOrderSnapshot();
    IList<SourceEvidence> evidence = CreateEntityAndJoinEvidence("Purchase_Order_Item", "PO_ID", "Purchase_Order", "PO_ID");
    new CodeRelationEvidenceService().Apply(snapshot, evidence);
    Assert.AreEqual(ConfidenceStatus.CodeEvidence, snapshot.Tables[0].Relations[0].RelationType.Status);
}
```

另测：单一 JOIN 只生成参考关系；物理 FK 永远是正式关系；名称相似但无代码使用不得升正式；临时表、表变量、复杂表达式 JOIN 被排除；方向依据目标主键/唯一键而不是 SQL 左右顺序。

- [ ] **Step 2: 实现关系合并键与证据门槛**

```csharp
private static string BuildRelationKey(RelationMetadata relation)
{
    return string.Concat(
        relation.ParentSchemaName, "|", relation.ParentTableName, "|", relation.ParentFieldName, "|",
        relation.ChildSchemaName, "|", relation.ChildTableName, "|", relation.ChildFieldName).ToUpperInvariant();
}
```

正式关系只接受物理 FK，或“实体属性关系 + SQL JOIN”，或两个互相独立的业务文件中相同方向 JOIN；单个 JOIN 和纯命名关系保留 `Guessed` 并在 UI 标注“参考”。同一稳定键合并证据，不重复生成。

- [ ] **Step 3: 运行测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~CodeRelationEvidenceServiceTests|FullyQualifiedName~RelationInferenceTests|FullyQualifiedName~BusinessDictionaryV1EnrichmentServiceTests" --no-restore
git add src/SHB.EosDataDictionary/Services tests/SHB.EosDataDictionary.Tests
git commit -m "feat: cross-check EOS table relations"
```

Expected: PASS；关系方向、来源与置信级别均可解释。

## Task 10：建立金标准、审计报告和发布硬门禁

**Files:**
- Add: `glossary/quality/dictionary-v6-gold.json`
- Create: `src/SHB.EosDataDictionary/Services/DictionaryV6QualityGateService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/BusinessDictionaryV1ReportService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/OfflineDictionaryV1Generator.cs`
- Modify: `tools/SHB.EosDataDictionary.Enricher/Program.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/DictionaryV6QualityGateServiceTests.cs`
- Modify: `tests/SHB.EosDataDictionary.Tests/BusinessDictionaryV1ReportServiceTests.cs`

- [ ] **Step 1: 建立可审计金标准**

金标准必须来自当前知识库、数据库明确注释和业务代码直接证据，至少覆盖 150 张核心表、1,000 个实际使用字段、100 条关系，跨仓储、采购、销售、生产、计划、质量、财务、物料等模块。每条包含稳定键、期望中文、证据类型、相对源码位置或知识库位置；不得把模型本轮自动结果直接当答案。

示例条目结构：

```json
{
  "kind": "Field",
  "scopeKey": "SHB",
  "schemaName": "dbo",
  "objectName": "DA_Acceptance",
  "fieldName": "Owner_Company_ID",
  "expectedChineseName": "货主公司ID",
  "evidenceType": "UserConfirmed",
  "source": "UserConfirmed:2026-09-09"
}
```

- [ ] **Step 2: 先写门禁失败测试**

```csharp
/// <summary>XMZADD 20260910 验证正式名称准确率不足时禁止发布。</summary>
[TestMethod]
public void Evaluate_OfficialAccuracyBelowNinetyNinePercent_BlocksPromotion()
{
    DictionaryV6QualityInput input = CreateQualityInput(0.989D, 0.97D, 0.99D);
    DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);
    Assert.IsFalse(result.CanPromote);
    AssertContains(result.Failures, "正式名称准确率");
}
```

同时测试：参考译名低于 95%、关系低于 98%、任一实际使用字段既无正式名也无参考名、伪中文正式值、冲突正式值、实体表覆盖不足均阻止发布。

- [ ] **Step 3: 输出完整审计报告**

至少生成：`summary.json`、`official-name-coverage.csv`、`suggested-name-coverage.csv`、`used-field-gaps.csv`、`conflicts.csv`、`pseudo-chinese.csv`、`module-attribution.csv`、`relation-audit.csv`、`gold-evaluation.csv`、`evidence-distribution.csv`。报告使用稳定排序与 UTF-8 BOM CSV，便于 Excel 打开。

- [ ] **Step 4: 运行报告和门禁测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~DictionaryV6QualityGateServiceTests|FullyQualifiedName~BusinessDictionaryV1ReportServiceTests|FullyQualifiedName~OfflineDictionaryV1GeneratorTests" --no-restore
git add glossary/quality src/SHB.EosDataDictionary/Services tools/SHB.EosDataDictionary.Enricher tests/SHB.EosDataDictionary.Tests
git commit -m "feat: gate dictionary publication on measured accuracy"
```

Expected: PASS；不达标候选只能留在候选目录，不能调用提升服务。

## Task 11：升级 UI，让正式名、参考译名、冲突和实际使用情况一眼可见

**Files:**
- Modify: `src/SHB.EosDataDictionary/ViewModels/DisplayModels.cs`
- Modify: `src/SHB.EosDataDictionary/ViewModels/MainViewModel.cs`
- Modify: `src/SHB.EosDataDictionary/Services/ColumnFilterService.cs`
- Modify: `src/SHB.EosDataDictionary/MainWindow.xaml`
- Modify: `src/SHB.EosDataDictionary/MainWindow.xaml.cs`
- Modify: `src/SHB.EosDataDictionary/DictionaryEditWindow.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/MainViewModelFilterTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/MainViewModelTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/ColumnFilterServiceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/DictionaryEditPersistenceTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/WpfResourceTests.cs`

- [ ] **Step 1: 写搜索作用域和新列失败测试**

```csharp
/// <summary>XMZADD 20260910 验证参考译名参与当前作用域内搜索。</summary>
[TestMethod]
public void SearchTables_SuggestedChineseName_MatchesInsideEntityScope()
{
    MainViewModel viewModel = CreateViewModelWithEntityAndNonEntityTables();
    viewModel.ShowOnlyEntityTables = true;
    viewModel.SearchText = "仓储区定义";
    Assert.AreEqual(1, viewModel.TableRows.Count);
    Assert.IsTrue(viewModel.TableRows[0].HasEntity);
}
```

另测：“全部对象”在 71,953 张表内搜索；“仅 EOS 实体”只在 1,287 张表内搜索；只看缺正式名、只看有参考译名、只看冲突、只看业务实际使用字段可组合；枚举中文、被使用模块和证据摘要均可搜索。

- [ ] **Step 2: 调整展示模型**

`ChineseName` 列只显示正式名称；空时显示“待确认”，不得把英文或参考译名塞回该列。增加橙色“参考译名”列、冲突标识和“被使用模块”摘要。字段默认可勾选“仅业务实际使用”，但允许用户取消查看全部物理字段。

```csharp
public bool HasOfficialName { get { return !string.IsNullOrWhiteSpace(OfficialChineseName); } }
public bool HasSuggestion { get { return !string.IsNullOrWhiteSpace(SuggestedChineseName); } }
public bool IsConflict { get { return AlternativeChineseNameCount > 0; } }
```

- [ ] **Step 3: 保持搜索独占一行并改善可读性**

搜索框保持单独一行，不恢复右上角重复统计；表格正文使用 14px 左右高对比字体，标题 14–16px 半粗体，行高不低于 34px，状态色同时用文字表达，浅色和深色主题均达到清晰对比。横向列多时冻结状态、中文名和英文名，参考译名允许省略号并用 Tooltip 显示全称。

- [ ] **Step 4: 编辑窗口支持采纳、修改和拒绝参考译名**

采纳或修改后仍提交普通 `Set ChineseName`；拒绝只提交参考译名指纹，不影响已有正式名。证据区显示来源类型、规则、相对文件和行号，禁止显示本机绝对路径或整段源代码。

- [ ] **Step 5: 运行 UI 与 72k 性能测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~MainViewModelFilterTests|FullyQualifiedName~MainViewModelTests|FullyQualifiedName~ColumnFilterServiceTests|FullyQualifiedName~DictionaryEditPersistenceTests|FullyQualifiedName~WpfResourceTests" --no-restore
git add src/SHB.EosDataDictionary tests/SHB.EosDataDictionary.Tests
git commit -m "feat: expose trusted and suggested metadata in UI"
```

Expected: PASS；72,000 表筛选基准不退化超过 20%，搜索框不与统计或复选框重叠。

## Task 12：让参考译名拒绝决定可同步且跨语言一致

**Files:**
- Modify: `src/SHB.EosDataDictionary/Models/DictionarySyncModels.cs`
- Modify: `src/SHB.EosDataDictionary/Services/DictionaryChangeValidator.cs`
- Modify: `src/SHB.EosDataDictionary/Services/DictionaryEventApplyService.cs`
- Modify: `tools/process_open_issues.py`
- Modify: `tools/tests/test_process_open_issues.py`
- Modify: `README.md`
- Test: `tests/SHB.EosDataDictionary.Tests/DictionaryChangeValidatorTests.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/DictionaryEventApplyServiceTests.cs`

- [ ] **Step 1: 写 C# 与 Python 失败测试**

协议继续使用现有 operation 成员：`ChangeKind = RejectSuggestion`、`PropertyName = SuggestedChineseName`、`NewValue = 64 位小写 SHA-256`。测试合法指纹追加到拒绝列表并清除完全匹配的当前参考译名；错误长度、非十六进制、对象不存在、字段不存在都拒绝；重复事件幂等。

```python
def test_reject_suggestion_is_idempotent():
    snapshot = snapshot_with_table_suggestion("仓储区定义", SUGGESTION_HASH)
    operation = reject_table_suggestion_operation(SUGGESTION_HASH)
    apply_operation(snapshot, operation)
    apply_operation(snapshot, operation)
    assert snapshot["Tables"][0]["SuggestedChineseName"] is None
    assert snapshot["Tables"][0]["RejectedSuggestionFingerprints"] == [SUGGESTION_HASH]
```

- [ ] **Step 2: 实现 C# 和 Python 镜像逻辑**

验证器不得增加未知 JSON 成员；只扩展合法 `ChangeKind`/`PropertyName` 组合。事件历史继续记录 GitHub 作者和服务端时间。排序、指纹规范化和清除逻辑在两端必须相同。

- [ ] **Step 3: 运行跨语言协议测试并提交**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~DictionaryChangeValidatorTests|FullyQualifiedName~DictionaryEventApplyServiceTests|FullyQualifiedName~SnapshotCodecTests" --no-restore
python -m unittest discover -s tools/tests -p "test_*.py"
git add src/SHB.EosDataDictionary/Models/DictionarySyncModels.cs src/SHB.EosDataDictionary/Services/DictionaryChangeValidator.cs src/SHB.EosDataDictionary/Services/DictionaryEventApplyService.cs tools/process_open_issues.py tools/tests/test_process_open_issues.py README.md tests/SHB.EosDataDictionary.Tests
git commit -m "feat: synchronize suggestion review decisions"
```

Expected: .NET 与 Python 全部 PASS，旧事件协议继续可用。

## Task 13：全量回归、旧客户端兼容与安全审计

**Files:**
- Modify only if a regression is proven: files introduced or changed in Tasks 2–12
- Add: `docs/quality/dictionary-v6-verification.md`

- [ ] **Step 1: 运行完整测试矩阵**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --no-restore
python -m unittest discover -s tools/tests -p "test_*.py"
dotnet build src/SHB.EosDataDictionary/SHB.EosDataDictionary.csproj -c Release --no-restore
dotnet build tools/SHB.EosDataDictionary.Enricher/SHB.EosDataDictionary.Enricher.csproj -c Release --no-restore
dotnet build tools/SHB.EosDataDictionary.Promoter/SHB.EosDataDictionary.Promoter.csproj -c Release --no-restore
```

Expected: 至少保持 838 个既有 .NET 测试并增加本计划的新测试；Python 至少保持 29 个既有测试并增加拒绝测试；0 失败、0 编译错误。

- [ ] **Step 2: 验证 Revision 11 客户端兼容**

用发布前保存的 Revision 11 可执行文件读取含新增可选成员的候选 payload，验证表/字段列表、实体筛选和搜索能加载；再用新客户端读取 Revision 11 payload。两向读取均不得抛异常，format 保持 1。

- [ ] **Step 3: 运行敏感信息、乱码和占位符扫描**

```powershell
rg -n --hidden '(github_pat_|ghp_|Password\s*=|Pwd\s*=|User ID\s*=|密码\s*[:=]|[A-Za-z]:\\\\)' src tests tools README.md config glossary
rg -n --hidden --glob '!.git/**' '[�]|鏄|鍙|缂' src tests tools docs glossary
rg -n --hidden --glob '!.git/**' '(TODO|TBD|待补充|placeholder)' src tests tools glossary/quality
```

Expected: 无私密信息和乱码；占位符扫描只允许测试数据中明确用于验证的字面量，并在验证文档逐条说明。

- [ ] **Step 4: 记录证据并提交**

`docs/quality/dictionary-v6-verification.md` 写入命令、日期、测试计数、耗时、兼容结果和扫描结果，不写本机密钥或数据库地址。

```powershell
git add docs/quality/dictionary-v6-verification.md
git commit -m "test: verify dictionary v6 compatibility and safety"
```

## Task 14：生成全量候选快照并执行质量审计

**Files:**
- Generated locally, never commit: `artifacts/dictionary-v6-candidate-20260910/dictionary.db`
- Generated locally, never commit: `artifacts/dictionary-v6-candidate-20260910/report/**`
- Update only after all gates pass: `snapshot/**`

- [ ] **Step 1: 复制活动数据库作为只读输入，避免直接覆盖**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$candidateRoot = Join-Path $repo 'artifacts\dictionary-v6-candidate-20260910'
New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
$activeDatabase = Join-Path $env:LOCALAPPDATA 'SHB\EosDataDictionary\dictionary.db'
Copy-Item -LiteralPath $activeDatabase -Destination (Join-Path $candidateRoot 'revision11-input.db') -Force
```

操作前先退出正在占用数据库的应用；如仍存在 WAL/SHM 或 SQLite 完整性检查失败，停止生成并保留原库。

- [ ] **Step 2: 用 EOS 源码和知识库生成候选**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$candidateRoot = Join-Path $repo 'artifacts\dictionary-v6-candidate-20260910'
$eosSource = (Resolve-Path $env:EOS_SOURCE_ROOT).Path
$knowledgeRoot = Join-Path $eosSource 'docs_knowledge'
dotnet run --project (Join-Path $repo 'tools\SHB.EosDataDictionary.Enricher\SHB.EosDataDictionary.Enricher.csproj') -c Release --no-build -- --source-db (Join-Path $candidateRoot 'revision11-input.db') --output-db (Join-Path $candidateRoot 'dictionary.db') --scope SHB --source-root $eosSource --knowledge-root $knowledgeRoot --report-root (Join-Path $candidateRoot 'report')
```

Expected: 输出包含 71,953 张表、869,344 个字段、1,287 张 EOS 实体表和 16,039 个业务使用字段；数量若变化必须由结构差异报告解释。

- [ ] **Step 3: 人机结合审计高风险项**

逐项核对所有正式名冲突、伪中文、`Other` 模块中的实体表、关系方向冲突、同一英文标识跨上下文中文不同、实际使用却无正式名/参考名的字段。重点复核 `Account`、`Owner`、`Source`、`Using`、`Company`、`Inv`、`FA`、`DA`、`KIS` 等多义缩写。

- [ ] **Step 4: 执行硬门禁**

```powershell
$summary = Get-Content -LiteralPath 'artifacts\dictionary-v6-candidate-20260910\report\summary.json' -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $summary.CanPromote) { throw ($summary.Failures -join [Environment]::NewLine) }
if ($summary.OfficialNameAccuracy -lt 0.99) { throw '正式名称准确率未达到 99%。' }
if ($summary.SuggestedNameAccuracy -lt 0.95) { throw '参考译名准确率未达到 95%。' }
if ($summary.RelationDirectionAccuracy -lt 0.98) { throw '关系方向准确率未达到 98%。' }
```

Expected: 全部门禁通过；否则回到产生错误的最早任务补测试和修复，禁止手工改 summary 绕过。

## Task 15：提升规范快照、安装验证并同步 GitHub

**Files:**
- Modify: `snapshot/manifest.json`
- Add: `snapshot/revisions/<next-revision>/**`
- Modify: `snapshot/latest/**`
- Modify: `README.md` if measured counts changed

- [ ] **Step 1: 计算下一修订号并提升候选**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$manifest = Get-Content -LiteralPath (Join-Path $repo 'snapshot\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$baseRevision = [long]$manifest.Revision
$baseSha256 = [string]$manifest.SnapshotSha256
$expectedHead = (git -C $repo rev-parse HEAD).Trim()
$candidateRoot = Join-Path $repo 'artifacts\dictionary-v6-candidate-20260910'
$promotedDatabase = Join-Path $candidateRoot 'promoted.db'
if (Test-Path -LiteralPath $promotedDatabase) { throw '提升输出已存在，请使用新的候选目录，禁止覆盖。' }
dotnet run --project (Join-Path $repo 'tools\SHB.EosDataDictionary.Promoter\SHB.EosDataDictionary.Promoter.csproj') -c Release --no-build -- --source-db (Join-Path $candidateRoot 'dictionary.db') --output-db $promotedDatabase --scope SHB --repository-root $repo --expected-head $expectedHead --expected-revision $baseRevision --expected-sha256 $baseSha256
```

Expected: 新 revision 只在仓库工作区生成，Revision 11 文件仍保留，manifest 的 payload hash 与新 latest 文件一致。

- [ ] **Step 2: 构建手测包并验证关键流程**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$manual = Join-Path $repo 'artifacts\manual-test-v6-20260910'
dotnet build (Join-Path $repo 'src\SHB.EosDataDictionary\SHB.EosDataDictionary.csproj') -c Release --no-restore
New-Item -ItemType Directory -Path $manual -Force | Out-Null
Copy-Item -Path (Join-Path $repo 'src\SHB.EosDataDictionary\bin\Release\net48\*') -Destination $manual -Recurse -Force
Start-Process -FilePath (Join-Path $manual 'SHB.EosDataDictionary.exe') -WorkingDirectory $manual
```

手测：启动后非空；“全部对象”为全量，“仅 EOS 实体”为 1,287；搜索在当前作用域内；正式/参考列分离；`op_createtime`、`Owner_Company_ID`、两类 Account 案例正确；枚举、模块、关系和来源证据可追溯；“待生效”在同步完成后归零。

- [ ] **Step 3: 安装候选数据库并验证 SHA**

退出应用后，把原活动库复制到带时间戳的备份文件，再复制 `promoted.db` 到活动路径；不得删除备份。启动正式路径应用并再次完成上述冒烟测试。对提升库、活动库导出的 payload 和仓库 latest payload 计算 SHA-256，三者必须一致。

- [ ] **Step 4: 把 Git 仓库源码同步回本地源码目录并比较**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$source = Split-Path (Split-Path $repo -Parent) -Parent
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $source -Force
& robocopy (Join-Path $repo 'src') (Join-Path $source 'src') /E /XD bin obj /XF *.user *.suo *.db *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '同步 src 失败。' }
& robocopy (Join-Path $repo 'tests') (Join-Path $source 'tests') /E /XD bin obj TestResults /XF *.user *.suo *.db *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '同步 tests 失败。' }
& robocopy (Join-Path $repo 'tools\SHB.EosDataDictionary.Enricher') (Join-Path $source 'tools\SHB.EosDataDictionary.Enricher') /E /XD bin obj /XF *.user *.suo *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '同步 Enricher 失败。' }
& robocopy (Join-Path $repo 'tools\SHB.EosDataDictionary.Promoter') (Join-Path $source 'tools\SHB.EosDataDictionary.Promoter') /E /XD bin obj /XF *.user *.suo *.exe *.dll *.pdb
if ($LASTEXITCODE -ge 8) { throw '同步 Promoter 失败。' }
```

用 `git ls-files src tests tools/SHB.EosDataDictionary.Enricher tools/SHB.EosDataDictionary.Promoter global.json` 逐文件计算仓库与本地副本 SHA-256；任何差异都必须解决后才能推送。

- [ ] **Step 5: 最终审查、合并并推送**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
git -C $repo diff --check
git -C $repo status --short
git -C $repo add snapshot README.md docs
git -C $repo commit -m "data: publish trusted EOS dictionary revision"
git -C $repo fetch origin
git -C $repo rebase origin/main
git -C $repo switch main
git -C $repo merge --ff-only codex/eos-dictionary-accuracy-v6
git -C $repo push origin main
git -C $repo rev-parse HEAD
git -C $repo ls-remote origin refs/heads/main
```

Expected: 本地 HEAD 与远程 `main` SHA 完全一致；远程 latest revision、payload SHA、本机活动字典和手测包一致；无待提交或待推送文件。

## 实施后的维护方式

维护者直接克隆 GitHub 仓库即可获得应用源码、工作流、术语库和规范快照。普通中文名维护通过应用提交 GitHub Issue，由工作流按顺序应用；不会由每台电脑直接定时强推 Git。其他人点击“更新字典”拉取最新规范快照。结构发布和全量候选提升仍只由授权发布者执行，并必须通过本计划的质量门禁。
