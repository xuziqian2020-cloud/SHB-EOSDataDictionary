using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260904 验证 EOS 业务源码中的 SQL、界面标题和实体赋值可形成可审计字段用途证据。</summary>
    [TestClass]
    public sealed class EosBusinessUsageEvidenceExtractorTests
    {
        /// <summary>XMZADD 20260904 验证指定 VB 夹具可提取字段关系、唯一标题归属和实体字段赋值。</summary>
        [TestMethod]
        public void Extract_RequiredFixture_PublishesRelationsCaptionAndEntityAssignment()
        {
            string[] lines =
            {
                "Dim sql As String = \"From DA_Acceptance DAA \" & _",
                " \"Inner Join Company C On DAA.Owner_Company_ID=C.Company_ID\"",
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\"",
                "Dim acceptance As New t_DA_Acceptance",
                "acceptance.f_op_createtime = Now",
                "' Dim ignored As String = \"From Secret S Join Hidden H On S.Secret_ID=H.Hidden_ID\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/frmAcceptance.vb", "DA", lines);

            SourceEvidence relation = FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation");
            Assert.IsNotNull(relation);
            Assert.AreEqual("Company", relation.RelationTargetObjectName);
            Assert.AreEqual("Company_ID", relation.RelationTargetFieldName);

            SourceEvidence reverseRelation = FindEvidence(evidence, "Company", "Company_ID", "SqlFieldRelation");
            Assert.IsNotNull(reverseRelation);
            Assert.AreEqual("DA_Acceptance", reverseRelation.RelationTargetObjectName);
            Assert.AreEqual("Owner_Company_ID", reverseRelation.RelationTargetFieldName);

            SourceEvidence caption = FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "GridColumnCaption");
            Assert.IsNotNull(caption);
            Assert.AreEqual("货主公司", caption.ChineseNameCandidate);

            SourceEvidence assignment = FindEvidence(evidence, "DA_Acceptance", "op_createtime", "EntityFieldAssignment");
            Assert.IsNotNull(assignment);
            Assert.AreEqual("t_DA_Acceptance", assignment.EntityName);
            Assert.AreEqual("Now", assignment.BusinessIdentifierCandidate);
            AssertAuditFields(evidence);
            Assert.IsNull(FindEvidence(evidence, "Secret", "Secret_ID", "SqlFieldRelation"));
            Assert.IsNull(FindEvidence(evidence, "Hidden", "Hidden_ID", "SqlFieldRelation"));
        }

        /// <summary>XMZADD 20260904 验证 SQL 解析兼容大小写、方括号和 AS 别名并按业务端点去重。</summary>
        [TestMethod]
        public void Extract_BracketedMixedCaseSql_DeduplicatesFieldUsagesAndRelations()
        {
            string sql = "Dim sql = \"sElEcT [a].[Order_ID], [c].[Company_Name] " +
                         "FrOm [dbo].[DA_Acceptance] AS [a] " +
                         "INNER JOIN [dbo].[Company] AS [c] ON [a].[Owner_Company_ID]=[c].[Company_ID] " +
                         "WhErE [a].[Order_ID]>0 GROUP BY [a].[Order_ID], [c].[Company_Name] " +
                         "ORDER BY [a].[Order_ID]\"";
            string[] lines =
            {
                sql,
                sql
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/Query.vb", "DA", lines);

            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Order_ID", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "Company", "Company_Name", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation"));
            Assert.AreEqual(1, CountEvidence(evidence, "Company", "Company_ID", "SqlFieldRelation"));
        }

        /// <summary>XMZADD 20260904 验证连续 VB 字符串只在最多三十二行内共享 SQL 别名上下文。</summary>
        [TestMethod]
        public void Extract_SqlContinuationBeyondThirtyTwoLines_DoesNotCrossWindow()
        {
            var lines = new string[34];
            lines[0] = "Dim sql = \"FROM DA_Acceptance DAA \" & _";
            for (int index = 1; index < 32; index++)
            {
                lines[index] = " \" \" & _";
            }
            lines[32] = " \"WHERE DAA.Owner_Company_ID=1\"";
            lines[33] = "' \"FROM Hidden H WHERE H.Hidden_ID=1\"";

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/LongSql.vb", "DA", lines);

            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "Hidden", "Hidden_ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260904 验证同名字段存在多表候选时记录冲突且不发布中文命名证据。</summary>
        [TestMethod]
        public void Extract_AmbiguousCaption_RecordsConflictAndKeepsDataColumnUniqueBinding()
        {
            string[] lines =
            {
                "Dim sql = \"SELECT A.Code, B.Code, A.Status FROM Alpha AS A JOIN Beta AS B ON A.Parent_ID=B.Parent_ID\"",
                "fg.Cols(\"Code\").Caption = \"编码\"",
                "data.Columns(\"Status\").Caption = \"状态\"",
                "Dim alpha As New t_Alpha",
                "value = alpha.f_Status"
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/Ambiguous.vb", "Common", lines);

            Assert.IsNull(FindEvidence(evidence, "Alpha", "Code", "GridColumnCaption"));
            Assert.IsNull(FindEvidence(evidence, "Beta", "Code", "GridColumnCaption"));
            SourceEvidence conflict = FindEvidence(evidence, null, "Code", "GridColumnCaptionConflict");
            Assert.IsNotNull(conflict);
            Assert.IsNull(conflict.ChineseNameCandidate);
            Assert.AreEqual("Code", conflict.BusinessIdentifierCandidate);

            SourceEvidence dataColumn = FindEvidence(evidence, "Alpha", "Status", "DataColumnCaption");
            Assert.IsNotNull(dataColumn);
            Assert.AreEqual("状态", dataColumn.ChineseNameCandidate);
            Assert.IsNotNull(FindEvidence(evidence, "Alpha", "Status", "EntityFieldAssignment"));
        }

        /// <summary>XMZADD 20260904 验证多行 VB SQL 的查询、筛选、分组和排序子句分别发布不同字段用途。</summary>
        [TestMethod]
        public void Extract_MultilineSelectClauses_PublishesEachDistinctFieldUsage()
        {
            string[] lines =
            {
                "Dim sql As String = \"SELECT Select_Field \" & _",
                " \"FROM DA_Acceptance DAA \" & _",
                " \"WHERE Where_Field=1 \" & _",
                " \"GROUP BY Group_Field \" & _",
                " \"ORDER BY Order_Field\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/MultilineSelect.vb", "DA", lines);

            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Select_Field", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Where_Field", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Group_Field", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Order_Field", "SqlFieldUsage"));
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Select_Field", "SqlFieldUsage", 1,
                "SELECT Select_Field");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Where_Field", "SqlFieldUsage", 3,
                "WHERE Where_Field");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Group_Field", "SqlFieldUsage", 4,
                "GROUP BY Group_Field");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Order_Field", "SqlFieldUsage", 5,
                "ORDER BY Order_Field");
        }

        /// <summary>XMZADD 20260904 验证多行 UPDATE 同时提取 SET 写入字段和 WHERE 定位字段。</summary>
        [TestMethod]
        public void Extract_MultilineUpdate_PublishesSetAndWhereFieldUsage()
        {
            string[] lines =
            {
                "Dim sql As String = \"UPDATE DA_Acceptance \" & _",
                " \"SET Owner_Company_ID=1 \" & _",
                " \", Acceptance_Status=2 \" & _",
                " \"WHERE Acceptance_ID=2\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/MultilineUpdate.vb", "DA", lines);

            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Acceptance_Status", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", 2,
                "SET Owner_Company_ID");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Acceptance_Status", "SqlFieldUsage", 3,
                "Acceptance_Status=2");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage", 4,
                "WHERE Acceptance_ID");
        }

        /// <summary>XMZADD 20260904 验证多行 INSERT 列表中的各目标字段均发布写入用途证据。</summary>
        [TestMethod]
        public void Extract_MultilineInsert_PublishesEachTargetColumnUsage()
        {
            string[] lines =
            {
                "Dim sql As String = \"INSERT INTO DA_Acceptance (\" & _",
                " \"Owner_Company_ID,\" & _",
                " \"Acceptance_ID) VALUES (1, 2)\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/MultilineInsert.vb", "DA", lines);

            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.AreEqual(1, CountEvidence(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", 2,
                "Owner_Company_ID");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage", 3,
                "Acceptance_ID) VALUES");
        }

        /// <summary>XMZADD 20260905 验证 SQL 单引号常量不会伪造字段归属、关联关系或界面标题证据。</summary>
        [TestMethod]
        public void Extract_SqlSingleQuotedConstants_DoNotPublishFieldsOwnersOrRelations()
        {
            string[] lines =
            {
                "Dim sql As String = \"SELECT Acceptance_ID FROM DA_Acceptance \" & _",
                " \"WHERE Status='OPEN' \" & _",
                " \"AND Remark LIKE '%Owner_Company_ID%' \" & _",
                " \"AND Customer_Name='O''Brien' \" & _",
                " \"AND Filter='DA_Acceptance.Owner_Company_ID=Company.Company_ID'\"",
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/SqlConstants.vb", "DA", lines);

            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Status", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Remark", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Customer_Name", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Filter", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "OPEN", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "O", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Brien", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证 SQL Server Unicode 字符串前缀及其常量内容不会伪造成业务字段。</summary>
        [TestMethod]
        public void Extract_SqlUnicodeStringConstants_DoNotPublishPrefixOrConstantFields()
        {
            string[] lines =
            {
                "Dim sql As String = \"SELECT Customer_Name FROM DA_Acceptance \" & _",
                " \"WHERE Customer_Name=N'O''Brien' \" & _",
                " \"AND Remark LIKE n'%Owner_Company_ID%'\"",
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/UnicodeSqlConstants.vb", "DA", lines);

            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Customer_Name", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Remark", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "N", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "O", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Brien", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证独立字段 N 不会因 Unicode 字面量前缀规则被误屏蔽。</summary>
        [TestMethod]
        public void Extract_LegitimateFieldNamedN_RemainsSqlFieldUsage()
        {
            string[] lines =
            {
                "Dim sql As String = \"SELECT N FROM DA_Acceptance WHERE N='normal'\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/FieldN.vb", "DA", lines);

            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "N", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证完整 REM 注释行被忽略且 Remote、Remark 等普通代码仍参与扫描。</summary>
        [TestMethod]
        public void Extract_RemCommentOnlyLines_AreSkippedWithoutHidingRemPrefixCode()
        {
            string[] lines =
            {
                "REM Dim sql = \"SELECT Secret_ID FROM Secret_Table\"",
                "   rEm    Dim sql = \"SELECT Hidden_ID FROM Hidden_Table\"",
                "Remote = \"SELECT Remote_ID FROM Remote_Table\"",
                "Remark = \"SELECT Remark_ID FROM Remark_Table\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/RemComments.vb", "Common", lines);

            Assert.IsNull(FindEvidence(evidence, "Secret_Table", "Secret_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "Hidden_Table", "Hidden_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "Remote_Table", "Remote_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "Remark_Table", "Remark_ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证动态表表达式不会把后续 SQL 别名伪装成物理表。</summary>
        [TestMethod]
        public void Extract_DynamicTableExpression_DoesNotPublishAliasAsPhysicalObject()
        {
            string[] lines =
            {
                "Dim sql = \"SELECT a.Owner_Company_ID FROM \" & config.f_Table_Account & \" a\"",
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Storage/DynamicAccount.vb", "Storage", lines);

            Assert.IsNull(FindEvidence(evidence, "a", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "a", "Owner_Company_ID", "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证连续同变量 SQL 追加共享别名上下文且各字段保留实际源码行。</summary>
        [TestMethod]
        public void Extract_ConsecutiveSameVariableSqlAppend_CombinesWithinWindow()
        {
            string[] lines =
            {
                "sql = \"UPDATE L SET Deleted=1\"",
                "sql = sql & \" FROM Real_Table L WHERE L.ID=1\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/AppendSql.vb", "Common", lines);

            AssertEvidenceLocation(evidence, "Real_Table", "Deleted", "SqlFieldUsage", 1,
                "UPDATE L SET Deleted=1");
            AssertEvidenceLocation(evidence, "Real_Table", "ID", "SqlFieldUsage", 2,
                "FROM Real_Table L WHERE L.ID=1");
            Assert.IsNull(FindEvidence(evidence, "L", "Deleted", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "L", "ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证不同变量、非连续赋值及超过三十二行的追加 SQL 不共享上下文。</summary>
        [TestMethod]
        public void Extract_SqlAppendOutsideSameContinuousWindow_DoesNotCombine()
        {
            string[] differentVariables =
            {
                "sql = \"UPDATE L SET Deleted=1\"",
                "other = other & \" FROM Real_Table L WHERE L.ID=1\""
            };
            IList<SourceEvidence> differentEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/DifferentSql.vb", "Common", differentVariables);
            Assert.IsNull(FindEvidence(differentEvidence, "Real_Table", "Deleted", "SqlFieldUsage"));

            string[] nonContinuous =
            {
                "sql = \"UPDATE L SET Deleted=1\"",
                string.Empty,
                "sql = sql & \" FROM Real_Table L WHERE L.ID=1\""
            };
            IList<SourceEvidence> nonContinuousEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/SeparatedSql.vb", "Common", nonContinuous);
            Assert.IsNull(FindEvidence(nonContinuousEvidence, "Real_Table", "Deleted", "SqlFieldUsage"));

            var beyondWindow = new string[33];
            beyondWindow[0] = "sql = \"UPDATE L SET Deleted=1\"";
            for (int lineIndex = 1; lineIndex < 32; lineIndex++)
            {
                beyondWindow[lineIndex] = "sql = sql & \" \"";
            }
            beyondWindow[32] = "sql = sql & \" FROM Real_Table L WHERE L.ID=1\"";
            IList<SourceEvidence> beyondEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/LongAppendSql.vb", "Common", beyondWindow);
            Assert.IsNull(FindEvidence(beyondEvidence, "Real_Table", "Deleted", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证 UPDATE 目标别名通过 FROM 表映射且不发布别名伪对象。</summary>
        [TestMethod]
        public void Extract_UpdateFromAlias_MapsSetFieldToPhysicalTable()
        {
            string[] lines =
            {
                "Dim sql = \"UPDATE A SET A.Owner_Company_ID=1 FROM DA_Acceptance A WHERE A.Acceptance_ID=2\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/UpdateAlias.vb", "DA", lines);

            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "A", "Owner_Company_ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证 SELECT 显式和隐式输出别名不会进入字段证据且多字段边界不受影响。</summary>
        [TestMethod]
        public void Extract_SelectOutputAliases_DoNotPublishAsPhysicalFields()
        {
            string[] lines =
            {
                "Dim sql = \"SELECT Owner_Company_ID OwnerCompany, Acceptance_ID, Status AS StatusName FROM DA_Acceptance\"",
                "fg.Cols(\"OwnerCompany\").Caption = \"货主公司\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/SelectAliases.vb", "DA", lines);

            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Status", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "OwnerCompany", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "StatusName", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DA_Acceptance", "OwnerCompany", "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证 DISTINCT 和 ALL 修饰符不会使首个真实投影字段被当成隐式别名。</summary>
        [TestMethod]
        public void Extract_SelectSetModifiers_PreserveFirstPhysicalField()
        {
            string[] distinctLines =
            {
                "Dim sql = \"SELECT DISTINCT Owner_Company_ID, Acceptance_ID FROM DA_Acceptance\""
            };
            IList<SourceEvidence> distinctEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/DistinctFields.vb", "DA", distinctLines);
            Assert.IsNotNull(FindEvidence(distinctEvidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(distinctEvidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));

            string[] allLines =
            {
                "Dim sql = \"SELECT ALL Owner_Company_ID, Acceptance_ID OwnerId FROM DA_Acceptance\""
            };
            IList<SourceEvidence> allEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/AllFields.vb", "DA", allLines);
            Assert.IsNotNull(FindEvidence(allEvidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNotNull(FindEvidence(allEvidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(allEvidence, "DA_Acceptance", "OwnerId", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证 VB 行末连接符可还原 CTE SQL 且不会把 CTE 名称发布为物理表。</summary>
        [TestMethod]
        public void Extract_ImplicitAmpersandContinuation_CombinesCteWithoutPublishingCteObject()
        {
            string[] lines =
            {
                "Dim sql = \"WITH RecentAcceptance AS (\" &",
                " \" SELECT A.Owner_Company_ID, A.Acceptance_ID \" &",
                " \" FROM DA_Acceptance A\" &",
                " \" ) SELECT RecentAcceptance.Owner_Company_ID FROM RecentAcceptance\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/CteQuery.vb", "DA", lines);

            AssertEvidenceLocation(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", 2,
                "A.Owner_Company_ID");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage", 2,
                "A.Acceptance_ID");
            Assert.IsNull(FindEvidence(evidence, "RecentAcceptance", "Owner_Company_ID", "SqlFieldUsage"));

            string[] commentBoundary =
            {
                "Dim broken = \"SELECT X.ID FROM \" ' trailing comment &",
                " \"Real_Table X\""
            };
            IList<SourceEvidence> commentEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/CommentAmpersand.vb", "DA", commentBoundary);
            Assert.IsNull(FindEvidence(commentEvidence, "Real_Table", "ID", "SqlFieldUsage"));

            string[] literalBoundary =
            {
                "Dim broken = \"SELECT X.ID FROM &\"",
                " \"Real_Table X\""
            };
            IList<SourceEvidence> literalEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/LiteralAmpersand.vb", "DA", literalBoundary);
            Assert.IsNull(FindEvidence(literalEvidence, "Real_Table", "ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证带列清单的多个 CTE 不会被发布为物理表且内层真实字段仍保留。</summary>
        [TestMethod]
        public void Extract_CteColumnLists_DoNotPublishLogicalObjects()
        {
            string[] lines =
            {
                "Dim sql = \"WITH [RecentAcceptance] ([Owner_Company_ID]) AS (\" &",
                " \" SELECT A.Owner_Company_ID FROM DA_Acceptance A\" &",
                " \" ), secondCte (Acceptance_ID) AS (\" &",
                " \" SELECT A.Acceptance_ID FROM DA_Acceptance A\" &",
                " \" ) SELECT R.Owner_Company_ID FROM [RecentAcceptance] R\" &",
                " \" JOIN secondCte S ON R.Owner_Company_ID=S.Acceptance_ID\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/CteColumnListQuery.vb", "DA", lines);

            AssertEvidenceLocation(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", 2,
                "A.Owner_Company_ID");
            AssertEvidenceLocation(evidence, "DA_Acceptance", "Acceptance_ID", "SqlFieldUsage", 4,
                "A.Acceptance_ID");
            Assert.IsNull(FindEvidence(evidence, "RecentAcceptance", "Owner_Company_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "secondCte", "Acceptance_ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证 EOS 常用 &= 连续拼接可在受限窗口内还原 CTE 且只发布真实物理表字段。</summary>
        [TestMethod]
        public void Extract_CompoundAmpersandSqlAppend_CombinesCteWithinWindow()
        {
            string[] lines =
            {
                "S &= \"WITH CTE AS (\"",
                "s &= \" SELECT D.DPI_ID, D.DPORI_ID\"",
                "S &= \" FROM DP_Order_Receipt_Item D\"",
                "s &= \" ) SELECT DPI_ID, COUNT(*) AS RowCount\"",
                "S &= \" FROM CTE\"",
                "s &= \" GROUP BY DPI_ID\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Sale/frmSaleToBeInvoicedException.vb", "Sale", lines);

            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "DPI_ID", "SqlFieldUsage", 2,
                "D.DPI_ID");
            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "DPORI_ID", "SqlFieldUsage", 2,
                "D.DPORI_ID");
            Assert.IsNull(FindEvidence(evidence, "CTE", "DPI_ID", "SqlFieldUsage"));
            AssertBusinessIdentityDoesNotContain(evidence, "CTE");
        }

        /// <summary>XMZADD 20260905 验证 &= 仅合并连续同变量且不跨越三十二行窗口或扩展到其他复合赋值。</summary>
        [TestMethod]
        public void Extract_CompoundAmpersandSqlAppend_RespectsVariableContinuityAndWindow()
        {
            string[] differentVariables =
            {
                "S &= \"SELECT X.ID FROM \"",
                "T &= \"Real_Table X\""
            };
            IList<SourceEvidence> differentEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/DifferentCompoundAppend.vb", "Common", differentVariables);
            Assert.IsNull(FindEvidence(differentEvidence, "Real_Table", "ID", "SqlFieldUsage"));

            string[] nonContinuous =
            {
                "S &= \"SELECT X.ID FROM \"",
                "DoWork()",
                "s &= \"Real_Table X\""
            };
            IList<SourceEvidence> nonContinuousEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/SeparatedCompoundAppend.vb", "Common", nonContinuous);
            Assert.IsNull(FindEvidence(nonContinuousEvidence, "Real_Table", "ID", "SqlFieldUsage"));

            var beyondWindow = new string[33];
            beyondWindow[0] = "S &= \"SELECT X.ID FROM \"";
            for (int lineIndex = 1; lineIndex < 32; lineIndex++)
            {
                beyondWindow[lineIndex] = "s &= \" \"";
            }
            beyondWindow[32] = "S &= \"Real_Table X\"";
            IList<SourceEvidence> beyondEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/LongCompoundAppend.vb", "Common", beyondWindow);
            Assert.IsNull(FindEvidence(beyondEvidence, "Real_Table", "ID", "SqlFieldUsage"));

            string[] otherCompoundAssignment =
            {
                "S += \"SELECT X.ID FROM \"",
                "s += \"Real_Table X\""
            };
            IList<SourceEvidence> otherCompoundEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Common/OtherCompoundAppend.vb", "Common", otherCompoundAssignment);
            Assert.IsNull(FindEvidence(otherCompoundEvidence, "Real_Table", "ID", "SqlFieldUsage"));
        }

        /// <summary>XMZADD 20260905 验证窗口函数只贡献真实列且不会把后续 CTE、临时表和输出别名归属到基础表。</summary>
        [TestMethod]
        public void Extract_WindowOrderBy_DoesNotConsumeFollowingCteQueryAsBaseTableFields()
        {
            string[] lines =
            {
                "S &= \"WITH CTE AS (\"",
                "s &= \" SELECT D.DPI_ID,\"",
                "S &= \" ROW_NUMBER() OVER (PARTITION BY Warehouse_ID ORDER BY Batch_No) AS ProductIndex,\"",
                "s &= \" SUM(D.Qty) OVER (PARTITION BY D.DPI_ID) AS DPIBatch\"",
                "S &= \" FROM DP_Order_Receipt_Item D\"",
                "s &= \" ) SELECT DPI_ID, ProductIndex, DPIBatch\"",
                "S &= \" INTO #DPIBatch\"",
                "s &= \" FROM CTE\"",
                "S &= \" GROUP BY DPI_ID, ProductIndex, DPIBatch\""
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Sale/frmSaleToBeInvoicedException.vb", "Sale", lines);

            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "DPI_ID", "SqlFieldUsage", 2,
                "D.DPI_ID");
            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "Warehouse_ID", "SqlFieldUsage", 3,
                "PARTITION BY Warehouse_ID");
            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "Batch_No", "SqlFieldUsage", 3,
                "ORDER BY Batch_No");
            AssertEvidenceLocation(evidence, "DP_Order_Receipt_Item", "Qty", "SqlFieldUsage", 4,
                "D.Qty");
            Assert.IsNull(FindEvidence(evidence, "DP_Order_Receipt_Item", "PARTITION", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DP_Order_Receipt_Item", "CTE", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DP_Order_Receipt_Item", "ProductIndex", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "DP_Order_Receipt_Item", "DPIBatch", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "CTE", null, "SqlFieldUsage"));

            string[] topLevelOrderLines =
            {
                "OrderSql &= \"SELECT Receipt_ID FROM DP_Order_Receipt_Item\"",
                "ordersql &= \" ORDER BY Batch_No\""
            };
            IList<SourceEvidence> topLevelOrderEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/Sale/TopLevelOrder.vb", "Sale", topLevelOrderLines);
            AssertEvidenceLocation(topLevelOrderEvidence, "DP_Order_Receipt_Item", "Batch_No",
                "SqlFieldUsage", 2, "ORDER BY Batch_No");
        }

        /// <summary>XMZADD 20260905 验证 C# 块注释不产生字段证据且同行注释外代码与字符串边界仍可扫描。</summary>
        [TestMethod]
        public void Extract_CSharpBlockComments_AreMaskedWithoutHidingRealCodeOrStringTokens()
        {
            string[] lines =
            {
                "/* var secretSql = \"SELECT Secret_ID FROM Secret_Table\"; */",
                "/*",
                "var hiddenSql = \"SELECT Hidden_ID FROM Hidden_Table\";",
                "*/",
                "var beforeSql = \"SELECT Before_ID FROM Before_Table\"; /* var ghostSql = \"SELECT Ghost_ID FROM Ghost_Table\"; */",
                "/* var maskedSql = \"SELECT Masked_ID FROM Masked_Table\"; */ var afterSql = \"SELECT After_ID FROM After_Table\";",
                "var blockStartLiteral = \"/*\";",
                "var quotedSql = \"SELECT Quoted_ID FROM Quoted_Table\";",
                "var blockEndLiteral = \"*/\";",
                "// /* var hiddenLineSql = \"SELECT HiddenLine_ID FROM HiddenLine_Table\"; */",
                "var finalSql = \"SELECT Final_ID FROM Final_Table\";"
            };

            IList<SourceEvidence> evidence = new EosBusinessUsageEvidenceExtractor().Extract(
                "ERP/DA/BlockComments.cs", "DA", lines);

            AssertEvidenceLocation(evidence, "Before_Table", "Before_ID", "SqlFieldUsage", 5,
                "Before_ID FROM Before_Table");
            AssertEvidenceLocation(evidence, "After_Table", "After_ID", "SqlFieldUsage", 6,
                "After_ID FROM After_Table");
            AssertEvidenceLocation(evidence, "Quoted_Table", "Quoted_ID", "SqlFieldUsage", 8,
                "Quoted_ID FROM Quoted_Table");
            AssertEvidenceLocation(evidence, "Final_Table", "Final_ID", "SqlFieldUsage", 11,
                "Final_ID FROM Final_Table");
            Assert.IsNull(FindEvidence(evidence, "Ghost_Table", "Ghost_ID", "SqlFieldUsage"));
            Assert.IsNull(FindEvidence(evidence, "Masked_Table", "Masked_ID", "SqlFieldUsage"));
            AssertBusinessIdentityDoesNotContain(evidence, "Secret");
            AssertBusinessIdentityDoesNotContain(evidence, "Hidden");
        }

        /// <summary>XMZADD 20260905 验证字段证据准确指向包含该字段的实际物理源码行。</summary>
        private static void AssertEvidenceLocation(IList<SourceEvidence> evidence, string objectName,
            string fieldName, string ruleName, int expectedLine, string expectedOriginalText)
        {
            SourceEvidence item = FindEvidence(evidence, objectName, fieldName, ruleName);
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Evidence);
            Assert.AreEqual(expectedLine, item.Evidence.SourceLine);
            StringAssert.Contains(item.Evidence.OriginalText, expectedOriginalText);
        }

        /// <summary>XMZADD 20260904 按表、字段和规则定位一条测试证据。</summary>
        private static SourceEvidence FindEvidence(IList<SourceEvidence> evidence,
            string objectName, string fieldName, string ruleName)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                    item.Evidence != null &&
                    string.Equals(item.Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260904 统计同一表字段规则的证据数量以验证提取阶段去重。</summary>
        private static int CountEvidence(IList<SourceEvidence> evidence,
            string objectName, string fieldName, string ruleName)
        {
            int count = 0;
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                    item.Evidence != null &&
                    string.Equals(item.Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260905 遍历证据身份和值，确保块注释中的敏感占位标识没有进入任何发布结果。</summary>
        private static void AssertBusinessIdentityDoesNotContain(IList<SourceEvidence> evidence, string forbiddenText)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                AssertTextDoesNotContain(item.ObjectName, forbiddenText);
                AssertTextDoesNotContain(item.FieldName, forbiddenText);
                AssertTextDoesNotContain(item.RelationTargetObjectName, forbiddenText);
                AssertTextDoesNotContain(item.RelationTargetFieldName, forbiddenText);
                AssertTextDoesNotContain(item.BusinessIdentifierCandidate, forbiddenText);
                AssertTextDoesNotContain(item.ChineseNameCandidate, forbiddenText);
                if (item.Evidence != null)
                {
                    AssertTextDoesNotContain(item.Evidence.RawValue, forbiddenText);
                    AssertTextDoesNotContain(item.Evidence.OriginalText, forbiddenText);
                }
            }
        }

        /// <summary>XMZADD 20260905 以忽略大小写方式断言单个可空证据文本不含注释占位标识。</summary>
        private static void AssertTextDoesNotContain(string value, string forbiddenText)
        {
            Assert.IsTrue((value ?? string.Empty).IndexOf(forbiddenText, StringComparison.OrdinalIgnoreCase) < 0,
                "证据不应包含块注释标识：" + forbiddenText + "，实际值：" + value);
        }

        /// <summary>XMZADD 20260904 验证每条业务用途证据均保留定位、原值、原文和业务解释。</summary>
        private static void AssertAuditFields(IList<SourceEvidence> evidence)
        {
            Assert.IsTrue(evidence.Count > 0);
            for (int index = 0; index < evidence.Count; index++)
            {
                Assert.IsNotNull(evidence[index].Evidence);
                Assert.AreEqual("ERP/DA/frmAcceptance.vb", evidence[index].Evidence.SourcePath);
                Assert.IsTrue(evidence[index].Evidence.SourceLine > 0);
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence[index].Evidence.RuleName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence[index].Evidence.RawValue));
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence[index].Evidence.OriginalText));
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence[index].Evidence.Explanation));
            }
        }
    }
}
