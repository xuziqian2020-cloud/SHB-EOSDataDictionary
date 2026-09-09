# EOS 数据字典 Revision 11 全量提升实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以远程 Revision 10 为基线生成包含 EOS 源码实体映射和最新业务中文推断的 Revision 11，使远程规范快照与本机共享 Payload 完全一致，并清除永久“待生效 1”。

**Architecture:** 应用先用现有离线富化器生成候选快照，再由独立提升服务执行公开内容校验、修订递增、确定性压缩和仓库文件生成。同步器把 GitHub 明确拒绝的 Issue 转成终态；结构扫描保留上一规范中的高可信实体映射。远程写入采用单 Commit、非强制推送和 HEAD/manifest 双重并发保护。

**Tech Stack:** C#、WPF、.NET Framework 4.8、System.Data.SQLite、MSTest、Python 3.12、GitHub Actions、Git。

---

## 文件职责

- `src/SHB.EosDataDictionary/Models/DictionarySyncModels.cs`：outbox 状态。
- `src/SHB.EosDataDictionary/Services/LocalDictionaryStore.cs`：活动队列与终态写入。
- `src/SHB.EosDataDictionary/Services/DictionarySyncCoordinator.cs`：远端 Issue 三态确认。
- `src/SHB.EosDataDictionary/Services/EntityMappingRetentionService.cs`：保留可信实体映射。
- `src/SHB.EosDataDictionary/Services/StructurePublishService.cs`：结构发布时调用保留规则。
- `src/SHB.EosDataDictionary/Services/CanonicalSnapshotPromotionService.cs`：准备 Revision 11 文件。
- `tools/SHB.EosDataDictionary.Promoter/`：不执行 Git 推送的命令行包装器。
- `.github/workflows/process-dictionary-event.yml`：无效 Issue 评论幂等化。

## Task 1：增加 Rejected 与 Superseded 终态

**Files:**
- Modify: `src/SHB.EosDataDictionary/Models/DictionarySyncModels.cs`
- Modify: `src/SHB.EosDataDictionary/Services/LocalDictionaryStore.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/LocalDictionaryStoreTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[TestMethod]
public void LoadActivePendingOperations_ExcludesRejectedAndSupersededHistory()
{
    var store = new LocalDictionaryStore(CreateDatabasePath());
    PendingDictionaryOperation rejected = SavePendingOperation(store, "rejected-op");
    PendingDictionaryOperation superseded = SavePendingOperation(store, "superseded-op");
    store.MarkOperationRejected(rejected.OperationId, "GitHub Issue 校验失败。");
    store.MarkOperationSuperseded(superseded.OperationId, "已被后续规范值取代。");
    Assert.AreEqual(2, store.LoadPendingOperations().Count);
    Assert.AreEqual(0, store.LoadActivePendingOperations().Count);
}
```

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "LoadActivePendingOperations_ExcludesRejectedAndSupersededHistory" --no-restore
```

Expected: FAIL，终态或方法不存在。

- [ ] **Step 3: 最小实现**

```csharp
public enum PendingOperationStatus
{
    Pending = 0,
    Submitted = 1,
    Completed = 2,
    Failed = 3,
    Rejected = 4,
    Superseded = 5
}
```

`LoadActivePendingOperations` 改为只查询 `Pending`、`Submitted`、`Failed`。新增方法：

```csharp
public void MarkOperationRejected(string operationId, string reason)
{
    UpdateOperationStatus(operationId, PendingOperationStatus.Rejected, 0, null, null, false, reason);
}

public void MarkOperationSuperseded(string operationId, string reason)
{
    UpdateOperationStatus(operationId, PendingOperationStatus.Superseded, 0, null, null, false, reason);
}
```

- [ ] **Step 4: 运行存储测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~LocalDictionaryStoreTests" --no-restore
```

Expected: PASS，0失败。

## Task 2：同步器消费 Rejected 三态

**Files:**
- Modify: `src/SHB.EosDataDictionary/Services/DictionarySyncCoordinator.cs`
- Test: `tests/SHB.EosDataDictionary.Tests/DictionarySyncCoordinatorTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[TestMethod]
public async Task SyncNow_RejectedSubmittedIssue_BecomesTerminal()
{
    TestContextState state = CreateSubmittedOperationContext(2);
    state.Client.IssueStatuses[2] = DictionaryIssueApplyStatus.Rejected;
    await state.Coordinator.SyncNowAsync(CancellationToken.None);
    Assert.AreEqual(PendingOperationStatus.Rejected, state.Store.LoadPendingOperations()[0].Status);
    Assert.AreEqual(0, state.Store.LoadActivePendingOperations().Count);
}
```

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "SyncNow_RejectedSubmittedIssue_BecomesTerminal" --no-restore
```

Expected: FAIL，记录仍为 `Submitted`。

- [ ] **Step 3: 使用现有三态 API**

```csharp
DictionaryIssueApplyStatus status = await _client.GetDictionaryOperationsApplyStatusAsync(
    pending.Batch, pending.GitHubIssueNumber.Value, maximumRevision, cancellationToken);
if (status == DictionaryIssueApplyStatus.Applied)
{
    _localStore.MarkOperationCompleted(pending.OperationId);
}
else if (status == DictionaryIssueApplyStatus.Rejected)
{
    _localStore.MarkOperationRejected(pending.OperationId, "GitHub Issue 校验失败，未进入规范快照。");
}
```

不得自动调用 `RecreateRejectedDictionaryIssueAsync`，避免把 Issue #2 的旧值“财务”重新覆盖当前“财务管理”。

- [ ] **Step 4: 运行协调器测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~DictionarySyncCoordinatorTests" --no-restore
```

Expected: PASS，0失败。

## Task 3：结构扫描保留可信 EOS 实体映射

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/EntityMappingRetentionService.cs`
- Modify: `src/SHB.EosDataDictionary/Services/StructurePublishService.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/EntityMappingRetentionServiceTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[TestMethod]
public void Apply_CurrentEntityMissing_RetainsPreviousEosSourceEntity()
{
    SnapshotData previous = SnapshotWithEntity("dbo", "DA_Account", "t_DA_Account");
    SnapshotData current = SnapshotWithoutEntity("dbo", "DA_Account");
    new EntityMappingRetentionService().Apply(previous, current);
    Assert.AreEqual("t_DA_Account", current.Tables[0].EntityName.Value);
    Assert.AreEqual(ConfidenceStatus.CodeEvidence, current.Tables[0].EntityName.Status);
}
```

另加测试：当前已有可信实体时不得替换；上一值没有源码实体证据时不得复制。

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EntityMappingRetentionServiceTests" --no-restore
```

Expected: FAIL，服务尚不存在。

- [ ] **Step 3: 实现稳定键保留规则**

```csharp
public void Apply(SnapshotData previous, SnapshotData current)
{
    IDictionary<string, TableMetadata> previousTables = BuildLookup(previous);
    for (int index = 0; index < current.Tables.Count; index++)
    {
        TableMetadata currentTable = current.Tables[index];
        TableMetadata previousTable;
        if (currentTable != null && IsEmpty(currentTable.EntityName) &&
            previousTables.TryGetValue(MakeKey(currentTable), out previousTable) &&
            HasEosEntityEvidence(previousTable.EntityName))
        {
            currentTable.EntityName = CloneMetadataValue(previousTable.EntityName);
        }
    }
}
```

证据只接受 `EOS 源码实体类`、`KisEntityClass`、`TableNameProperty`。

- [ ] **Step 4: 接入结构发布流程**

在 `StructurePublishService.PreparePreviewAsync` 中、`MetadataEnrichmentService.Enrich` 前调用：

```csharp
new EntityMappingRetentionService().Apply(previous, current);
MetadataEnrichmentService.Enrich(current, safeSourceEvidence);
```

- [ ] **Step 5: 运行实体及结构发布测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~EntityMappingRetentionServiceTests|FullyQualifiedName~StructurePublishServiceTests" --no-restore
```

Expected: PASS，0失败。

## Task 4：实现规范快照提升服务与命令行工具

**Files:**
- Create: `src/SHB.EosDataDictionary/Services/CanonicalSnapshotPromotionService.cs`
- Create: `tools/SHB.EosDataDictionary.Promoter/SHB.EosDataDictionary.Promoter.csproj`
- Create: `tools/SHB.EosDataDictionary.Promoter/Program.cs`
- Create: `tests/SHB.EosDataDictionary.Tests/CanonicalSnapshotPromotionServiceTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[TestMethod]
public void Prepare_Revision10Candidate_WritesIdenticalRevision11Payloads()
{
    string repositoryRoot = CreateRepositoryWithRevision10Manifest();
    SnapshotData candidate = CreatePublicCandidateSnapshot(10L);
    CanonicalSnapshotPromotionResult result = new CanonicalSnapshotPromotionService().Prepare(
        repositoryRoot, candidate, 10L, Revision10Hash,
        new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));
    CollectionAssert.AreEqual(File.ReadAllBytes(result.LatestSnapshotPath),
        File.ReadAllBytes(result.RevisionSnapshotPath));
    Assert.AreEqual(11L, result.Manifest.Revision);
    Assert.AreEqual(result.SnapshotSha256, result.Manifest.SnapshotSha256);
}
```

另加测试：基线 Revision 不符、SHA 不符、公开内容含绝对路径时拒绝且不留下半套文件。

- [ ] **Step 2: 运行红灯测试**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~CanonicalSnapshotPromotionServiceTests" --no-restore
```

Expected: FAIL，提升服务不存在。

- [ ] **Step 3: 实现准备服务**

```csharp
public CanonicalSnapshotPromotionResult Prepare(
    string repositoryRoot,
    SnapshotData candidate,
    long expectedBaseRevision,
    string expectedBaseSha256,
    DateTime generatedAtUtc)
```

固定执行顺序：读取 manifest 并核对基线；将候选修订设为基线加一；执行 `SnapshotValidator.Validate` 和 `ValidatePublicContent`；只调用一次 `SnapshotCodec.Encode`；相同字节写入 `latest.json.gz` 和修订文件；写空事件修订文档与提升审计；最后原子替换 manifest。失败时删除本次临时文件，不删除既有修订。

- [ ] **Step 4: 实现 Promoter CLI**

参数固定为：

```text
--source-db --output-db --scope --repository-root
--expected-head --expected-revision --expected-sha256
```

工具拒绝已存在输出库，核对 `git rev-parse HEAD`，复制源库后用 `SnapshotStore.ReplaceScopeFromRemote` 写入同一规范字节，输出实际 Revision、表数、字段数、实体数和 SHA。工具不得执行 `git commit`、`git push` 或 GitHub API。

- [ ] **Step 5: 运行提升测试和构建**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --filter "FullyQualifiedName~CanonicalSnapshotPromotionServiceTests" --no-restore
dotnet build tools/SHB.EosDataDictionary.Promoter/SHB.EosDataDictionary.Promoter.csproj -c Release --no-restore
```

Expected: 测试通过；构建0错误、0警告。

## Task 5：无效 Issue 评论幂等化

**Files:**
- Modify: `.github/workflows/process-dictionary-event.yml`
- Modify: `tools/tests/test_process_open_issues.py`

- [ ] **Step 1: 写失败的静态契约测试**

```python
def test_invalid_issue_comment_is_only_added_when_label_is_absent(self):
    workflow = self.workflow_path.read_text(encoding="utf-8")
    self.assertIn('gh issue view "$number" --repo "$REPOSITORY" --json labels', workflow)
    self.assertIn('grep -Fqx "dictionary-event-invalid"', workflow)
```

- [ ] **Step 2: 运行红灯测试**

```powershell
python -m unittest tools.tests.test_process_open_issues -v
```

Expected: FAIL，工作流尚未检查旧标签。

- [ ] **Step 3: 评论前检查标签**

```bash
existing_labels_file="$RUNNER_TEMP/failed-issue-$number-labels.txt"
gh issue view "$number" --repo "$REPOSITORY" --json labels \
  --jq '.labels[].name' > "$existing_labels_file"
if grep -Fqx "dictionary-event-invalid" "$existing_labels_file"; then
  continue
fi
```

标签不存在时才添加标签和评论；已修正文档的 Issue 仍由主处理步骤重新校验。

- [ ] **Step 4: 运行全部 Python 测试**

```powershell
python -m unittest discover -s tools/tests -p "test_*.py" -v
```

Expected: PASS，0失败。

## Task 6：生成 Revision 10 基线上的 V5 与 Revision 11

**Files:**
- Generate: `artifacts/dictionary.enriched-v5.db`
- Generate: `artifacts/dictionary-v5-report/`
- Generate: `artifacts/dictionary.canonical-r11.db`
- Generate: shared repository Revision 11 files

- [ ] **Step 1: 精确停止当前数据字典进程并备份 Revision 10**

只停止可执行路径完全匹配当前发布版的进程。保留原数据库和 SHA-256 备份，不删除历史备份。

- [ ] **Step 2: 运行现有离线富化器**

输入当前 Revision 10、本机已配置源码根目录、知识库根目录和 `github-shared-dictionary` scope，输出新目录。预期表数71,953、实体数1,287、多义冲突0。

- [ ] **Step 3: 验证候选业务样例**

断言 Revision 仍为10、表数71,953、实体数1,287、字段数不低于基线，并核验：

- `Account_Pallet_FA_Not_IO`：非托盘出入库流水账。
- `DA_Account`：财务流水账，模块为财务管理。
- `Owner_Company_ID`：货主公司ID。
- `op_createtime`：操作记录创建时间。

- [ ] **Step 4: 运行 Promoter 生成 Revision 11**

基线必须精确匹配 Revision 10、SHA `bbee1132eb8acc772913bddadc795f779ac2dbb7bc75bd4f0c67ba6c2fd44897` 和实施时重新获取的远程 HEAD。

- [ ] **Step 5: 比较精确字节**

以下三处 SHA-256 必须相同：仓库 `snapshot/latest.json.gz`、Revision 11 快照文件、`dictionary.canonical-r11.db` 共享 Payload。

## Task 7：全量回归、关闭 Issue #2 并发布

**Files:** all modified files and generated Revision 11 files.

- [ ] **Step 1: 运行全部测试与构建**

```powershell
dotnet test tests/SHB.EosDataDictionary.Tests/SHB.EosDataDictionary.Tests.csproj -c Release --no-restore
python -m unittest discover -s tools/tests -p "test_*.py" -v
dotnet build src/SHB.EosDataDictionary/SHB.EosDataDictionary.csproj -c Release --no-restore
```

Expected: 全部测试0失败；构建0错误、0警告。

- [ ] **Step 2: 做公开内容和 Git 检查**

```powershell
git diff --check
git status --short
```

扫描公开新增文件，拒绝密码、Token、服务器地址、绝对本机路径、SQLite 数据库和日志。

- [ ] **Step 3: 关闭 Issue #2**

重新核对 Issue #2 的编号、作者、标题和 `dictionary-event-invalid` 标签后，评论：

```text
该无效请求已被后续合法事件取代；当前规范值为“财务管理”，不再重新应用旧值“财务”。
```

然后关闭 Issue；失败则停止 Revision 11 发布。

- [ ] **Step 4: 非强制推进 main**

`git fetch origin main` 后重新比较 HEAD、Revision 和 SHA，匹配才提交并运行：

```powershell
git push origin HEAD:main
```

发生非快进冲突时停止，不使用 `--force`。

- [ ] **Step 5: 安装远程一致的本地快照**

下载远程 Revision 11 并验证 SHA，备份当前 SQLite 后安装 `dictionary.canonical-r11.db`。将 OperationId `e2c0aaea371b48028e5eb545db90b682` 标记为 `Superseded`，原因是已被 Revision 8 的“财务管理”取代。

## Task 8：发布应用并验证多人维护

**Files:** generate a new Release publish directory under `artifacts/`.

- [ ] **Step 1: 发布并启动 Release 应用**

```powershell
dotnet publish src/SHB.EosDataDictionary/SHB.EosDataDictionary.csproj -c Release --no-restore
```

Expected: 0错误、0警告，SQLite x86/x64 依赖完整。

- [ ] **Step 2: 验证主界面**

“显示全部对象”为71,953；“仅显示 EOS 有对应实体”为1,287；待上传0、待生效0；关键中文名正确。

- [ ] **Step 3: 自动化验证双客户端语义**

两个隔离临时 SQLite 客户端模拟：A 保存后进入 outbox，上传后为 Submitted，远程新修订后为 Completed；B 点击更新后取得相同值。测试不得访问生产数据库或真实 GitHub。

- [ ] **Step 4: 输出交付摘要**

报告 Revision 11 manifest、远程提交 SHA、快照 SHA、本机 Payload SHA、表/字段/实体/枚举/关系数量、测试数、Issue #2 状态和新应用路径。
