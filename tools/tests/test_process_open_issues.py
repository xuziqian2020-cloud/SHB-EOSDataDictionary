"""XMZADD 20260901 验证公开 Issue 字典事件处理器的授权、幂等和事务边界。"""

import base64
import gzip
import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "process_open_issues.py"
SPEC = importlib.util.spec_from_file_location("process_open_issues", MODULE_PATH)
PROCESSOR = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PROCESSOR
SPEC.loader.exec_module(PROCESSOR)


def make_operation(operation_id, value, property_name="ChineseName", field_key=""):
    """XMZADD 20260901 构造最小协议操作以突出每个安全规则的业务结果。"""
    return {
        "OperationId": operation_id,
        "AuthorGitHubUserId": "999999",
        "ObjectKey": "dbo.T_ORDER",
        "FieldKey": field_key,
        "PropertyName": property_name,
        "OldValue": "客户端伪造旧值",
        "NewValue": value,
        "ChangeKind": "Set",
        "CreatedAtUtc": "1999-01-01T00:00:00Z",
        "TablePayload": None,
        "FieldPayload": None,
        "RelationPayload": None,
        "Evidence": None,
    }


def make_batch(operation, author_id="999999", extra=None):
    """XMZADD 20260901 构造客户端批次并允许测试未知成员不能越过白名单。"""
    batch = {
        "BatchId": "batch_" + operation["OperationId"].replace(".", "x"),
        "AuthorGitHubUserId": author_id,
        "CreatedAtUtc": "1999-01-01T00:00:00Z",
        "Overrides": [],
        "Operations": [operation],
    }
    if extra:
        batch.update(extra)
    return batch


def make_issue(number, user_id, operation, title="[EOS-DICTIONARY-EVENT] dictionary update", batch=None):
    """XMZADD 20260901 用 GitHub API 形状构造 Issue，确保测试不访问真实网络。"""
    payload = batch if batch is not None else make_batch(operation)
    return {
        "number": number,
        "title": title,
        "body": "```json-v1\n" + json.dumps(payload, ensure_ascii=False) + "\n```",
        "created_at": "2026-09-01T01:02:03Z",
        "updated_at": "2026-09-01T01:02:04Z",
        "user": {"id": user_id, "login": "display-name-is-not-authority"},
    }


def make_structure_operation(operation_id, change_kind, object_key, field_key="",
                             table_payload=None, field_payload=None, relation_payload=None):
    """XMZADD 20260901 构造与 C# 结构发布器一致的单个类型化协议操作。"""
    return {
        "OperationId": operation_id,
        "AuthorGitHubUserId": "501",
        "ObjectKey": object_key,
        "FieldKey": field_key,
        "PropertyName": "",
        "OldValue": None,
        "NewValue": None,
        "ChangeKind": change_kind,
        "CreatedAtUtc": "1999-01-01T00:00:00Z",
        "TablePayload": table_payload,
        "FieldPayload": field_payload,
        "RelationPayload": relation_payload,
        "Evidence": None,
    }


def make_child_add_operations():
    """XMZADD 20260901 按 AddTable、AddField、AddRelation、分类维护的客户端发布顺序构造新子表。"""
    table_payload = {
        "SchemaName": "dbo", "ObjectName": "T_CHILD", "ObjectType": "TABLE",
        "ApproximateRowCount": 3, "Fields": [], "Relations": [],
    }
    field_payload = {
        "FieldName": "FORDERID", "OwnerTableName": "T_CHILD", "DataType": "int",
        "LengthText": "—", "IsRequired": True, "IsPrimaryKey": False, "IsForeignKey": True,
    }
    relation_payload = {
        "ForeignKeyName": "FK_CHILD_ORDER", "ParentSchemaName": "dbo",
        "ParentTableName": "T_ORDER", "ParentFieldName": "FNAME", "ChildSchemaName": "dbo",
        "ChildTableName": "T_CHILD", "ChildFieldName": "FORDERID",
    }
    add_table = make_structure_operation("cross_add_table", "AddTable", "dbo.T_CHILD",
                                         table_payload=table_payload)
    add_field = make_structure_operation("cross_add_field", "AddField", "dbo.T_CHILD", "FORDERID",
                                         field_payload=field_payload)
    add_relation = make_structure_operation("cross_add_relation", "AddRelation", "dbo.T_CHILD",
                                            relation_payload=relation_payload)
    category = make_operation("cross_category", "Business", "Category")
    category["AuthorGitHubUserId"] = "501"
    category["ObjectKey"] = "dbo.T_CHILD"
    keep = make_operation("cross_keep", "True", "KeepWhenEmpty")
    keep["AuthorGitHubUserId"] = "501"
    keep["ObjectKey"] = "dbo.T_CHILD"
    return [add_table, add_field, add_relation, category, keep], relation_payload


def empty_metadata(value):
    """XMZADD 20260901 创建与客户端快照契约兼容的人工元数据起点。"""
    return {
        "Value": value,
        "Description": None,
        "Status": 0,
        "ConfidenceScore": 100,
        "SourceType": "测试种子",
        "SourceSummary": "测试种子",
        "OriginalAutomaticValue": "",
        "IsManualOverride": False,
        "IsLocked": False,
        "Evidence": [],
    }


def initial_snapshot():
    """XMZADD 20260901 创建可被普通属性事件更新的最小合法共享快照。"""
    return {
        "FormatVersion": 1,
        "Revision": 0,
        "RefreshedAt": "2026-08-31T00:00:00Z",
        "Tables": [
            {
                "ScopeKey": None,
                "SchemaName": "dbo",
                "ObjectName": "T_ORDER",
                "ObjectType": "TABLE",
                "ChineseName": empty_metadata("原名称"),
                "ModuleName": None,
                "EntityName": None,
                "BusinessMeaning": None,
                "Remark": None,
                "Category": 0,
                "ApproximateRowCount": 0,
                "KeepWhenEmpty": False,
                "Fields": [],
                "Relations": [],
            }
        ],
        "ExcludedObjects": [],
        "Abbreviations": [],
    }


def physical_field(data_type="nvarchar", length_text="40"):
    """XMZADD 20260901 创建可验证发布者物理类型与长度组合的最小字段。"""
    return {
        "FieldName": "FNAME",
        "ChineseName": None,
        "OwnerTableName": "T_ORDER",
        "EntityPropertyName": None,
        "BusinessMeaning": None,
        "Usage": None,
        "DataType": data_type,
        "LengthText": length_text,
        "IsRequired": False,
        "IsPrimaryKey": False,
        "IsForeignKey": False,
        "EnumName": None,
        "EnumItems": [],
        "RelationSummary": None,
        "Remark": None,
    }


def canonical_json_bytes(value):
    """XMZADD 20260901 生成稳定 JSON 字节以便测试快照哈希而不依赖平台格式。"""
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def write_snapshot(repo_dir, snapshot):
    """XMZADD 20260901 写入确定性测试快照和清单以模拟已发布仓库状态。"""
    snapshot_dir = repo_dir / "snapshot"
    snapshot_dir.mkdir(parents=True, exist_ok=True)
    content = gzip.compress(canonical_json_bytes(snapshot), mtime=0)
    snapshot_path = snapshot_dir / "latest.json.gz"
    snapshot_path.write_bytes(content)
    manifest = {
        "FormatVersion": 1,
        "Revision": snapshot["Revision"],
        "SnapshotSha256": hashlib.sha256(content).hexdigest(),
        "SnapshotPath": "snapshot/latest.json.gz",
        "GeneratedAtUtc": snapshot["RefreshedAt"],
        "LastEventPath": "",
    }
    (snapshot_dir / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False), encoding="utf-8"
    )


def read_snapshot(repo_dir):
    """XMZADD 20260901 解压处理结果以直接核对服务端最终业务值。"""
    manifest = json.loads((repo_dir / "snapshot" / "manifest.json").read_text(encoding="utf-8"))
    with gzip.open(repo_dir / manifest["SnapshotPath"], "rt", encoding="utf-8") as stream:
        return json.load(stream)


class ProcessOpenIssuesTests(unittest.TestCase):
    """XMZADD 20260901 覆盖不可信 Issue 到不可变事件和规范快照的完整处理边界。"""

    def setUp(self):
        """XMZADD 20260901 为每个测试隔离仓库，防止事件和幂等状态相互污染。"""
        self.temporary = tempfile.TemporaryDirectory()
        self.repo_dir = Path(self.temporary.name)
        write_snapshot(self.repo_dir, initial_snapshot())

    def tearDown(self):
        """XMZADD 20260901 清理隔离仓库，确保测试不在工作区留下运行产物。"""
        self.temporary.cleanup()

    def test_ordinary_property_succeeds_and_records_actual_values(self):
        """XMZADD 20260901 验证公开用户可维护业务元数据且审计采用服务端真实前后值。"""
        issue = make_issue(5, 101, make_operation("ordinary_1", "订单"))

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertEqual([5], result.closed_issue_numbers)
        self.assertEqual("订单", result.snapshot["Tables"][0]["ChineseName"]["Value"])
        event = json.loads((self.repo_dir / result.event_paths[0]).read_text(encoding="utf-8"))
        self.assertEqual("原名称", event["OldValue"])
        self.assertEqual("订单", event["NewValue"])

    def test_success_result_preserves_trusted_version_for_safe_close(self):
        """XMZADD 20260901 验证发布结果保留抓取时更新时间和正文摘要，供推送后关闭前防止覆盖用户新编辑。"""
        issue = make_issue(50, 101, make_operation("version_1", "版本名称"))

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertEqual(
            {
                "updatedAt": issue["updated_at"],
                "bodySha256": hashlib.sha256(issue["body"].encode("utf-8")).hexdigest(),
            },
            result.processed_issue_versions[50],
        )

    def test_cli_output_includes_versions_needed_for_post_push_recheck(self):
        """XMZADD 20260901 验证工作流结果文件携带成功 Issue 的可信版本标记，而不是只有可关闭编号。"""
        issue = make_issue(51, 101, make_operation("version_cli_1", "命令行版本名称"))
        issues_path = self.repo_dir / "issues.json"
        result_path = self.repo_dir / "result.json"
        config_dir = self.repo_dir / "config"
        config_dir.mkdir(parents=True, exist_ok=True)
        issues_path.write_text(json.dumps([issue], ensure_ascii=False), encoding="utf-8")
        (config_dir / "publishers.json").write_text(
            json.dumps({"githubUserIds": []}), encoding="utf-8"
        )

        exit_code = PROCESSOR.main(
            [
                "--repo-dir", str(self.repo_dir),
                "--issues-file", str(issues_path),
                "--result-file", str(result_path),
            ]
        )

        output = json.loads(result_path.read_text(encoding="utf-8"))
        self.assertEqual(0, exit_code)
        self.assertEqual(
            {
                "updatedAt": issue["updated_at"],
                "bodySha256": hashlib.sha256(issue["body"].encode("utf-8")).hexdigest(),
            },
            output["processedIssueVersions"]["51"],
        )

    def test_workflow_rechecks_issue_version_before_comment_and_close(self):
        """XMZADD 20260901 验证推送成功后重新读取 Issue，并在版本一致时才评论和关闭。"""
        workflow_path = MODULE_PATH.parents[1] / ".github" / "workflows" / "process-dictionary-event.yml"
        workflow = workflow_path.read_text(encoding="utf-8")
        close_step = workflow.split("- name: Comment and close successfully published issues", 1)[1]
        close_step = close_step.split("- name: Label validation failures without closing them", 1)[0]

        self.assertIn('result["processedIssueVersions"]', close_step)
        self.assertIn('gh api --method GET "repos/$REPOSITORY/issues/$number"', close_step)
        self.assertIn('current_issue.get("state") != "open"', close_step)
        self.assertIn('current_issue.get("updated_at") != expected["updatedAt"]', close_step)
        self.assertIn('hashlib.sha256(body.encode("utf-8")).hexdigest()', close_step)
        self.assertLess(close_step.index("gh api --method GET"), close_step.index("gh issue comment"))
        self.assertLess(close_step.index("gh api --method GET"), close_step.index("gh issue close"))

    def test_workflow_reopens_when_body_changes_during_close(self):
        """XMZADD 20260901 验证关闭后再次核对正文，竞态变化时重开并跳过成功评论。"""
        workflow_path = MODULE_PATH.parents[1] / ".github" / "workflows" / "process-dictionary-event.yml"
        workflow = workflow_path.read_text(encoding="utf-8")
        close_step = workflow.split("- name: Comment and close successfully published issues", 1)[1]
        close_step = close_step.split("- name: Label validation failures without closing them", 1)[0]

        pre_get = close_step.index('gh api --method GET "repos/$REPOSITORY/issues/$number"')
        close_issue = close_step.index('gh issue close "$number"')
        post_get = close_step.index(
            'gh api --method GET "repos/$REPOSITORY/issues/$number" > "$post_close_issue_file"'
        )
        success_comment = close_step.index('gh issue comment "$number"')
        post_state_check = close_step.index('current_issue.get("state") != "closed"', post_get)
        post_body_check = close_step.index(
            'hashlib.sha256(body.encode("utf-8")).hexdigest() != expected["bodySha256"]',
            post_get,
        )
        reopen_after_post_get = close_step.index('reopen_issue_with_retry "$number"', post_get)
        continue_after_reopen = close_step.index("continue", reopen_after_post_get)

        self.assertLess(pre_get, close_issue)
        self.assertLess(close_issue, post_get)
        self.assertLess(post_get, success_comment)
        self.assertLess(post_get, post_state_check)
        self.assertLess(post_get, post_body_check)
        self.assertLess(reopen_after_post_get, continue_after_reopen)
        self.assertLess(continue_after_reopen, success_comment)
        self.assertIn('gh issue reopen "$issue_number"', close_step)
        self.assertGreaterEqual(close_step.count('reopen_issue_with_retry "$number"'), 2)

    def test_workflow_recovers_labeled_closed_issues_with_bounded_retries(self):
        """XMZADD 20260901 验证每轮先恢复带关闭中标签的已关闭 Issue，重试耗尽必须显式失败。"""
        workflow_path = MODULE_PATH.parents[1] / ".github" / "workflows" / "process-dictionary-event.yml"
        workflow = workflow_path.read_text(encoding="utf-8")
        recovery_step = workflow.split("- name: Recover interrupted dictionary issue closures", 1)[1]
        recovery_step = recovery_step.split("- name: Process and publish the complete open queue", 1)[0]

        self.assertIn("CLOSING_LABEL: eos-dictionary-closing", recovery_step)
        self.assertIn("gh label create \"$CLOSING_LABEL\"", recovery_step)
        self.assertIn("state=closed&labels=$CLOSING_LABEL", recovery_step)
        self.assertIn("number.isascii()", recovery_step)
        self.assertIn("number.isdigit()", recovery_step)
        self.assertIn("for attempt in 1 2 3", recovery_step)
        self.assertIn('gh issue reopen "$number"', recovery_step)
        self.assertIn("exit 1", recovery_step)
        self.assertLess(
            recovery_step.index('gh issue reopen "$number"'),
            recovery_step.index('--remove-label "$CLOSING_LABEL"'),
        )

    def test_workflow_labels_before_close_and_clears_only_after_stable_postcheck(self):
        """XMZADD 20260901 验证关闭补偿标签覆盖整个关闭窗口，重开重试失败时保留标签并非零退出。"""
        workflow_path = MODULE_PATH.parents[1] / ".github" / "workflows" / "process-dictionary-event.yml"
        workflow = workflow_path.read_text(encoding="utf-8")
        close_step = workflow.split("- name: Comment and close successfully published issues", 1)[1]
        close_step = close_step.split("- name: Label validation failures without closing them", 1)[0]

        add_label = close_step.index('--add-label "$CLOSING_LABEL"')
        close_issue = close_step.index('gh issue close "$number"')
        post_get = close_step.index(
            'gh api --method GET "repos/$REPOSITORY/issues/$number" > "$post_close_issue_file"'
        )
        remove_label = close_step.index('--remove-label "$CLOSING_LABEL"', post_get)
        success_comment = close_step.index('gh issue comment "$number"')

        self.assertIn("CLOSING_LABEL: eos-dictionary-closing", close_step)
        self.assertIn("reopen_issue_with_retry()", close_step)
        self.assertIn("for attempt in 1 2 3", close_step)
        self.assertIn('if ! reopen_issue_with_retry "$number"; then', close_step)
        self.assertIn("exit 1", close_step)
        self.assertLess(add_label, close_issue)
        self.assertLess(close_issue, post_get)
        self.assertLess(post_get, remove_label)
        self.assertLess(remove_label, success_comment)

    def test_non_publisher_physical_property_is_rejected_without_snapshot_change(self):
        """XMZADD 20260901 验证普通公开用户不能修改数据库物理结构且失败不产生副作用。"""
        before = (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes()
        operation = make_operation("physical_1", "nvarchar", "DataType", "FNAME")
        issue = make_issue(6, 101, operation)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertEqual([], result.closed_issue_numbers)
        self.assertIn(6, result.failed_issues)
        self.assertEqual(before, (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes())
        self.assertEqual([], list((self.repo_dir / "events").rglob("*.json")))

    def test_body_author_is_ignored_in_favor_of_api_numeric_id(self):
        """XMZADD 20260901 验证正文伪造发布者身份不能获得授权且可信作者写入 API 用户 ID。"""
        issue = make_issue(7, 101, make_operation("author_1", "可信作者"), batch=None)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[999999])

        event = json.loads((self.repo_dir / result.event_paths[0]).read_text(encoding="utf-8"))
        self.assertEqual("101", event["AuthorGitHubUserId"])
        self.assertNotEqual("999999", event["AuthorGitHubUserId"])

    def test_duplicate_operation_id_is_globally_idempotent(self):
        """XMZADD 20260901 验证工作流重试不会重复生成事件、修订或覆盖历史。"""
        first = make_issue(8, 101, make_operation("same_1", "第一次"))
        PROCESSOR.process_issues(self.repo_dir, [first], publishers=[])
        manifest_before = (self.repo_dir / "snapshot" / "manifest.json").read_bytes()

        duplicate = make_issue(9, 102, make_operation("same_1", "伪造重放"))
        result = PROCESSOR.process_issues(self.repo_dir, [duplicate], publishers=[])

        self.assertEqual([9], result.closed_issue_numbers)
        self.assertEqual(1, result.revision)
        self.assertEqual("第一次", result.snapshot["Tables"][0]["ChineseName"]["Value"])
        self.assertEqual(manifest_before, (self.repo_dir / "snapshot" / "manifest.json").read_bytes())
        self.assertEqual(1, len(list((self.repo_dir / "events" / "2026").rglob("same_1.json"))))

    def test_issues_are_processed_by_number_and_later_success_wins(self):
        """XMZADD 20260901 验证 API Issue 编号是唯一排序依据且后成功值覆盖前值。"""
        later = make_issue(11, 102, make_operation("ordered_b", "名称乙"))
        earlier = make_issue(10, 101, make_operation("ordered_a", "名称甲"))

        result = PROCESSOR.process_issues(self.repo_dir, [later, earlier], publishers=[])

        self.assertEqual([10, 11], result.closed_issue_numbers)
        self.assertEqual("名称乙", result.snapshot["Tables"][0]["ChineseName"]["Value"])
        first_event = json.loads((self.repo_dir / result.event_paths[0]).read_text(encoding="utf-8"))
        second_event = json.loads((self.repo_dir / result.event_paths[1]).read_text(encoding="utf-8"))
        self.assertEqual("原名称", first_event["OldValue"])
        self.assertEqual("名称甲", second_event["OldValue"])
        self.assertEqual(1, first_event["Revision"])
        self.assertEqual(1, second_event["Revision"])

    def test_event_path_rejects_parent_segments(self):
        """XMZADD 20260901 验证操作号不能把不可变事件写出受控 events 目录。"""
        issue = make_issue(12, 101, make_operation("..", "越界"))

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertIn(12, result.failed_issues)
        self.assertEqual([], result.event_paths)
        self.assertFalse((self.repo_dir.parent / "...json").exists())

    def test_manifest_hash_matches_updated_snapshot_bytes(self):
        """XMZADD 20260901 验证客户端可用清单哈希确认原子发布后的压缩快照完整性。"""
        issue = make_issue(13, 101, make_operation("hash_1", "哈希名称"))

        PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        manifest = json.loads((self.repo_dir / "snapshot" / "manifest.json").read_text(encoding="utf-8"))
        content = (self.repo_dir / manifest["SnapshotPath"]).read_bytes()
        self.assertEqual(hashlib.sha256(content).hexdigest(), manifest["SnapshotSha256"])
        self.assertEqual(1, manifest["Revision"])
        self.assertRegex(manifest["SnapshotPath"], r"^snapshot/revisions/000000001-[0-9a-f]{64}[.]json[.]gz$")
        self.assertRegex(manifest["GeneratedAtUtc"], r"^/Date\([0-9]+\)/$")
        self.assertRegex(read_snapshot(self.repo_dir)["RefreshedAt"], r"^/Date\([0-9]+\)/$")

    def test_manifest_write_failure_keeps_previous_snapshot_reference_valid(self):
        """XMZADD 20260901 验证发布在清单切换前失败时旧清单仍引用未被覆盖且哈希一致的旧快照。"""
        manifest_path = self.repo_dir / "snapshot" / "manifest.json"
        manifest_before = manifest_path.read_bytes()
        previous_manifest = json.loads(manifest_before.decode("utf-8"))
        previous_snapshot_path = self.repo_dir / previous_manifest["SnapshotPath"]
        previous_snapshot = previous_snapshot_path.read_bytes()
        issue = make_issue(131, 101, make_operation("manifest_failure", "新名称"))
        original_atomic_write = PROCESSOR._atomic_write

        def fail_manifest_write(path, content):
            """XMZADD 20260901 在测试边界模拟最终清单切换失败。"""
            if Path(path) == manifest_path:
                raise OSError("fake manifest failure")
            original_atomic_write(path, content)

        with mock.patch.object(PROCESSOR, "_atomic_write", side_effect=fail_manifest_write):
            with self.assertRaises(OSError):
                PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertEqual(manifest_before, manifest_path.read_bytes())
        self.assertEqual(previous_snapshot, previous_snapshot_path.read_bytes())
        self.assertEqual(
            previous_manifest["SnapshotSha256"],
            hashlib.sha256(previous_snapshot_path.read_bytes()).hexdigest(),
        )

    def test_invalid_issue_does_not_modify_existing_snapshot_manifest_or_events(self):
        """XMZADD 20260901 验证单个校验失败 Issue 对仓库发布状态完全无副作用。"""
        snapshot_before = (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes()
        manifest_before = (self.repo_dir / "snapshot" / "manifest.json").read_bytes()
        issue = make_issue(14, 101, make_operation("invalid_1", "值", "UnknownProperty"))

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[])

        self.assertIn(14, result.failed_issues)
        self.assertEqual(snapshot_before, (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes())
        self.assertEqual(manifest_before, (self.repo_dir / "snapshot" / "manifest.json").read_bytes())
        self.assertEqual([], list((self.repo_dir / "events").rglob("*.json")))

    def test_unknown_members_overrides_and_extra_code_blocks_are_rejected(self):
        """XMZADD 20260901 验证远程原始 JSON 和 Markdown 外壳都不能携带隐藏输入。"""
        unknown_operation = make_operation("unknown_1", "值")
        unknown_issue = make_issue(
            15, 101, unknown_operation, batch=make_batch(unknown_operation, extra={"Hidden": True})
        )
        override_operation = make_operation("override_1", "值")
        override_batch = make_batch(override_operation)
        override_batch["Overrides"] = [{"PropertyName": "ChineseName"}]
        override_issue = make_issue(16, 101, override_operation, batch=override_batch)
        extra_block = make_issue(17, 101, make_operation("block_1", "值"))
        extra_block["body"] += "\n```json-v1\n{}\n```"

        result = PROCESSOR.process_issues(
            self.repo_dir, [unknown_issue, override_issue, extra_block], publishers=[]
        )

        self.assertEqual({15, 16, 17}, set(result.failed_issues))
        self.assertEqual(0, result.revision)

    def test_publisher_can_add_table_and_field_with_normalized_sql_type(self):
        """XMZADD 20260901 验证结构发布者可按 C# 协议新增表字段并规范化 SQL 类型长度。"""
        add_table = {
            "OperationId": "add_table_1",
            "AuthorGitHubUserId": "1",
            "ObjectKey": "dbo.T_NEW",
            "FieldKey": "",
            "PropertyName": "",
            "OldValue": None,
            "NewValue": None,
            "ChangeKind": "AddTable",
            "CreatedAtUtc": "1999-01-01T00:00:00Z",
            "TablePayload": {
                "SchemaName": "dbo",
                "ObjectName": "T_NEW",
                "ObjectType": "TABLE",
                "ApproximateRowCount": 0,
                "Fields": [],
                "Relations": [],
            },
            "FieldPayload": None,
            "RelationPayload": None,
        }
        add_field = {
            "OperationId": "add_field_1",
            "AuthorGitHubUserId": "1",
            "ObjectKey": "dbo.T_NEW",
            "FieldKey": "FNAME",
            "PropertyName": "",
            "OldValue": None,
            "NewValue": None,
            "ChangeKind": "AddField",
            "CreatedAtUtc": "1999-01-01T00:00:00Z",
            "TablePayload": None,
            "FieldPayload": {
                "FieldName": "FNAME",
                "OwnerTableName": "T_NEW",
                "DataType": " NVARCHAR ",
                "LengthText": "0040",
                "IsRequired": False,
                "IsPrimaryKey": False,
                "IsForeignKey": False,
            },
            "RelationPayload": None,
        }
        batch = make_batch(add_table, author_id="1")
        batch["Operations"].append(add_field)
        issue = make_issue(18, 501, add_table, batch=batch)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        table = next(item for item in result.snapshot["Tables"] if item["ObjectName"] == "T_NEW")
        self.assertEqual("nvarchar", table["Fields"][0]["DataType"])
        self.assertEqual("40", table["Fields"][0]["LengthText"])

    def test_csharp_structure_order_persists_new_table_classification_and_relation(self):
        """XMZADD 20260901 验证 C# 发布顺序可由 Python 原子应用并保留分类、空表决定和关系。"""
        snapshot = initial_snapshot()
        snapshot["Tables"][0]["Fields"].append(physical_field("int", "—"))
        write_snapshot(self.repo_dir, snapshot)
        operations, relation_payload = make_child_add_operations()
        batch = make_batch(operations[0], author_id="501")
        batch["Operations"] = operations
        issue = make_issue(1801, 501, operations[0], batch=batch)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        self.assertEqual([1801], result.closed_issue_numbers)
        child = next(table for table in result.snapshot["Tables"] if table["ObjectName"] == "T_CHILD")
        self.assertEqual(1, child["Category"])
        self.assertTrue(child["KeepWhenEmpty"])
        self.assertEqual("FORDERID", child["Fields"][0]["FieldName"])
        self.assertEqual(relation_payload["ForeignKeyName"], child["Relations"][0]["ForeignKeyName"])

    def test_csharp_delete_order_removes_relation_then_whole_table_without_child_field_event(self):
        """XMZADD 20260901 验证关系先删、整表后删的 C# 批次无需冗余字段删除即可由 Python 应用。"""
        snapshot = initial_snapshot()
        snapshot["Tables"][0]["Fields"].append(physical_field("int", "—"))
        write_snapshot(self.repo_dir, snapshot)
        add_operations, relation_payload = make_child_add_operations()
        add_batch = make_batch(add_operations[0], author_id="501")
        add_batch["Operations"] = add_operations
        PROCESSOR.process_issues(
            self.repo_dir, [make_issue(1802, 501, add_operations[0], batch=add_batch)], publishers=[501]
        )
        remove_relation = make_structure_operation(
            "cross_remove_relation", "RemoveRelation", "dbo.T_CHILD", relation_payload=relation_payload
        )
        remove_table = make_structure_operation("cross_remove_table", "RemoveTable", "dbo.T_CHILD")
        remove_batch = make_batch(remove_relation, author_id="501")
        remove_batch["Operations"] = [remove_relation, remove_table]

        result = PROCESSOR.process_issues(
            self.repo_dir, [make_issue(1803, 501, remove_relation, batch=remove_batch)], publishers=[501]
        )

        self.assertEqual([1803], result.closed_issue_numbers)
        self.assertFalse(any(table["ObjectName"] == "T_CHILD" for table in result.snapshot["Tables"]))
        applied_kinds = []
        for path in result.event_paths:
            event = json.loads((self.repo_dir / path).read_text(encoding="utf-8"))
            applied_kinds.append(event["ChangeKind"])
        self.assertEqual(["RemoveRelation", "RemoveTable"], applied_kinds)

    def test_publisher_set_can_publish_only_relative_short_evidence(self):
        """XMZADD 20260901 验证结构发布者的可靠名称证据进入事件与快照且只保留公开字段。"""
        operation = make_operation("evidence_1", "订单名称", "ChineseName", "")
        operation["Evidence"] = [{
            "SourceType": "EOS源码",
            "SourcePath": "Order/OrderEntity.vb",
            "SourceLine": 12,
            "RuleName": "EntityProperty",
            "Explanation": "实体属性中文摘要",
        }]
        issue = make_issue(181, 501, operation)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        self.assertEqual([181], result.closed_issue_numbers)
        metadata = result.snapshot["Tables"][0]["ChineseName"]
        self.assertEqual("Order/OrderEntity.vb", metadata["Evidence"][0]["SourcePath"])
        event = json.loads((self.repo_dir / result.event_paths[0]).read_text(encoding="utf-8"))
        self.assertEqual("EntityProperty", event["Evidence"][0]["RuleName"])
        self.assertNotIn("RawValue", event["Evidence"][0])

    def test_database_description_evidence_remains_automatic_after_python_apply(self):
        """XMZADD 20260901 验证 C# 发布的数据库说明证据经 Python 应用后不会变成人工锁定值。"""
        operation = make_operation("database_evidence_1", "订单")
        operation["Evidence"] = [{
            "SourceType": "数据库说明", "SourcePath": "", "SourceLine": 0,
            "RuleName": "SqlExtendedDescription", "Explanation": "SQL Server 扩展说明",
        }]
        issue = make_issue(1811, 501, operation)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        self.assertEqual([1811], result.closed_issue_numbers)
        metadata = result.snapshot["Tables"][0]["ChineseName"]
        self.assertEqual(1, metadata["Status"])
        self.assertFalse(metadata["IsManualOverride"])
        self.assertFalse(metadata["IsLocked"])
        self.assertEqual("数据库说明", metadata["Evidence"][0]["SourceType"])

    def test_published_evidence_rejects_absolute_path_secret_and_source_body_atomically(self):
        """XMZADD 20260901 验证绝对路径、凭据形态和源码正文不能随结构事件进入仓库。"""
        invalid_evidence = [
            {"SourceType": "EOS源码", "SourcePath": "C:/private/Order.vb", "SourceLine": 1,
             "RuleName": "EntityProperty", "Explanation": "短摘要"},
            {"SourceType": "EOS源码", "SourcePath": "Order/Order.vb", "SourceLine": 1,
             "RuleName": "EntityProperty", "Explanation": "password=fake"},
            {"SourceType": "EOS源码", "SourcePath": "Order/Order.vb", "SourceLine": 1,
             "RuleName": "EntityProperty", "Explanation": "短摘要", "OriginalText": "完整源码正文"},
        ]
        issues = []
        for index, evidence in enumerate(invalid_evidence):
            operation = make_operation("bad_evidence_" + str(index), "订单名称")
            operation["Evidence"] = [evidence]
            issues.append(make_issue(182 + index, 501, operation))

        result = PROCESSOR.process_issues(self.repo_dir, issues, publishers=[501])

        self.assertEqual([], result.closed_issue_numbers)
        self.assertEqual([], result.event_paths)
        self.assertEqual({182, 183, 184}, set(result.failed_issues))

    def test_shared_evidence_rejection_vectors_match_csharp_validator(self):
        """XMZADD 20260901 验证 Python 与 C# 在证据和业务赋值中使用相同的凭据与不可见字符拒绝向量。"""
        vector_path = Path(__file__).with_name("evidence_rejection_vectors.txt")
        vector_lines = vector_path.read_text(encoding="utf-8").splitlines()
        checked_count = 0
        for index, line in enumerate(vector_lines):
            if not line or line.startswith("#"):
                continue
            label, encoded = line.split("|", 1)
            value = base64.b64decode(encoded).decode("utf-8")
            operation = make_operation("shared_vector_" + label, "订单名称")
            operation["Evidence"] = [{
                "SourceType": "EOS源码", "SourcePath": "Order/Order.vb", "SourceLine": 1,
                "RuleName": "EntityProperty", "Explanation": value,
            }]
            issue_number = 1900 + index

            result = PROCESSOR.process_issues(
                self.repo_dir, [make_issue(issue_number, 501, operation)], publishers=[501]
            )

            self.assertIn(issue_number, result.failed_issues, "未拒绝共享证据向量：" + label)
            self.assertEqual([], result.event_paths)

            value_operation = make_operation("shared_value_vector_" + label, value)
            value_issue_number = 2000 + index
            value_result = PROCESSOR.process_issues(
                self.repo_dir, [make_issue(value_issue_number, 501, value_operation)], publishers=[501]
            )

            self.assertIn(value_issue_number, value_result.failed_issues, "未拒绝共享赋值向量：" + label)
            self.assertEqual([], value_result.event_paths)
            checked_count += 1
        self.assertGreater(checked_count, 0)

    def test_issue_body_uses_sixty_kibibyte_safe_boundary(self):
        """XMZADD 20260901 验证 Python 解析端为 Markdown 包装预留空间并在边界加一字节时拒绝正文。"""
        self.assertEqual((60 * 1024) - 15, PROCESSOR.MAXIMUM_PAYLOAD_BYTES)
        self.assertEqual(60 * 1024, PROCESSOR.MAXIMUM_ISSUE_BODY_BYTES)
        base_json = json.dumps(make_batch(make_operation("size_boundary", "名称")), ensure_ascii=False)
        padding_length = PROCESSOR.MAXIMUM_PAYLOAD_BYTES - len(base_json.encode("utf-8"))
        self.assertGreater(padding_length, 0)
        exact_body = "```json-v1\n" + base_json + (" " * padding_length) + "\n```"
        over_body = "```json-v1\n" + base_json + (" " * (padding_length + 1)) + "\n```"

        parsed = PROCESSOR._parse_issue_body(exact_body)

        self.assertEqual("batch_size_boundary", parsed["BatchId"])
        with self.assertRaises(PROCESSOR.IssueValidationError):
            PROCESSOR._parse_issue_body(over_body)

    def test_set_data_type_rejects_incompatible_final_length_without_side_effects(self):
        """XMZADD 20260901 验证发布者单改类型不能留下与既有长度不兼容的字段结构。"""
        snapshot = initial_snapshot()
        snapshot["Tables"][0]["Fields"].append(physical_field())
        write_snapshot(self.repo_dir, snapshot)
        snapshot_before = (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes()
        manifest_before = (self.repo_dir / "snapshot" / "manifest.json").read_bytes()
        operation = make_operation("type_only_1", "int", "DataType", "FNAME")
        issue = make_issue(19, 501, operation)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        self.assertIn(19, result.failed_issues)
        self.assertEqual([], result.event_paths)
        self.assertEqual(snapshot_before, (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes())
        self.assertEqual(manifest_before, (self.repo_dir / "snapshot" / "manifest.json").read_bytes())
        self.assertEqual([], list((self.repo_dir / "events").rglob("*.json")))

    def test_final_field_structure_allows_temporarily_incompatible_set_order(self):
        """XMZADD 20260901 验证同一批可先改长度再改类型，只以 Issue 最终物理结构判定成功。"""
        snapshot = initial_snapshot()
        snapshot["Tables"][0]["Fields"].append(physical_field())
        write_snapshot(self.repo_dir, snapshot)
        length_operation = make_operation("length_first_1", "—", "LengthText", "FNAME")
        type_operation = make_operation("type_second_1", "int", "DataType", "FNAME")
        batch = make_batch(length_operation, author_id="501")
        batch["Operations"].append(type_operation)
        issue = make_issue(20, 501, length_operation, batch=batch)

        result = PROCESSOR.process_issues(self.repo_dir, [issue], publishers=[501])

        self.assertEqual([20], result.closed_issue_numbers)
        field = result.snapshot["Tables"][0]["Fields"][0]
        self.assertEqual("int", field["DataType"])
        self.assertEqual("—", field["LengthText"])
        self.assertEqual(2, len(result.event_paths))

    def test_final_field_structure_enforces_csharp_sql_length_ranges(self):
        """XMZADD 20260901 验证常用可变、定长与精度类型仍遵循 C# 的最终长度范围。"""
        invalid_pairs = (
            ("decimal", "40"),
            ("varchar", "8001"),
            ("nvarchar", "4001"),
            ("char", "MAX"),
        )
        for index, pair in enumerate(invalid_pairs):
            with self.subTest(data_type=pair[0], length_text=pair[1]):
                with tempfile.TemporaryDirectory() as temporary:
                    repo_dir = Path(temporary)
                    snapshot = initial_snapshot()
                    snapshot["Tables"][0]["Fields"].append(physical_field())
                    write_snapshot(repo_dir, snapshot)
                    type_operation = make_operation("range_type_" + str(index), pair[0], "DataType", "FNAME")
                    length_operation = make_operation("range_length_" + str(index), pair[1], "LengthText", "FNAME")
                    batch = make_batch(type_operation, author_id="501")
                    batch["Operations"].append(length_operation)
                    issue = make_issue(30 + index, 501, type_operation, batch=batch)

                    result = PROCESSOR.process_issues(repo_dir, [issue], publishers=[501])

                    self.assertIn(30 + index, result.failed_issues)
                    self.assertEqual([], result.event_paths)

    def test_created_at_strings_over_limit_are_rejected_at_batch_and_operation_levels(self):
        """XMZADD 20260901 验证未采用的客户端时间也不能利用超长文本污染公开输入。"""
        batch_operation = make_operation("long_batch_time_1", "批次时间")
        long_batch = make_batch(batch_operation)
        long_batch["CreatedAtUtc"] = "x" * 2001
        batch_issue = make_issue(40, 101, batch_operation, batch=long_batch)
        operation = make_operation("long_operation_time_1", "操作时间")
        operation["CreatedAtUtc"] = "x" * 2001
        operation_issue = make_issue(41, 101, operation)
        snapshot_before = (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes()
        manifest_before = (self.repo_dir / "snapshot" / "manifest.json").read_bytes()

        result = PROCESSOR.process_issues(self.repo_dir, [batch_issue, operation_issue], publishers=[])

        self.assertEqual({40, 41}, set(result.failed_issues))
        self.assertEqual([], result.event_paths)
        self.assertEqual(snapshot_before, (self.repo_dir / "snapshot" / "latest.json.gz").read_bytes())
        self.assertEqual(manifest_before, (self.repo_dir / "snapshot" / "manifest.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
