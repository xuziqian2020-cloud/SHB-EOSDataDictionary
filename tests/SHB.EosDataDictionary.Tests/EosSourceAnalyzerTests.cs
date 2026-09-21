using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class EosSourceAnalyzerTests
    {
        /// <summary>XMZADD 20260911 验证带 BOM 的 UTF-8 老源码不会把标记字符混入首行声明。</summary>
        [TestMethod]
        public void Decode_Utf8BomSource_PreservesChineseCaption()
        {
            byte[] textBytes = new UTF8Encoding(false, true).GetBytes(".Caption = \"货主公司ID\"");
            byte[] preamble = Encoding.UTF8.GetPreamble();
            var bytes = new byte[preamble.Length + textBytes.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(textBytes, 0, bytes, preamble.Length, textBytes.Length);

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.AreEqual(".Caption = \"货主公司ID\"", result.Text);
            Assert.AreEqual("UTF-8 BOM", result.EncodingName);
            Assert.IsTrue(result.IsReliable);
            Assert.IsFalse(result.HasMojibake);
        }

        /// <summary>XMZADD 20260911 验证无 BOM 的严格 UTF-8 在多编码均可解释时保持优先。</summary>
        [TestMethod]
        public void Decode_StrictUtf8Source_IsPreferred()
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(".Caption = \"采购订单\"");

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.AreEqual(".Caption = \"采购订单\"", result.Text);
            Assert.AreEqual("UTF-8", result.EncodingName);
            Assert.IsTrue(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证合法 UTF-8 西文和版权符号不会因 GB18030 也可解码而被错误换码。</summary>
        [TestMethod]
        public void Decode_ValidUtf8NonChineseText_IsPreferred()
        {
            const string source = "' Résumé © SHB";
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(source);

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.AreEqual(source, result.Text);
            Assert.AreEqual("UTF-8", result.EncodingName);
            Assert.IsTrue(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证字节同时可被两种编码解释时优先选择可读中文而非西里尔乱码。</summary>
        [TestMethod]
        public void Decode_AmbiguousGb18030Chinese_UsesQualityScore()
        {
            byte[] bytes = Encoding.GetEncoding("GB18030").GetBytes("一");

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.AreEqual("一", result.Text);
            Assert.AreEqual("GB18030", result.EncodingName);
            Assert.IsTrue(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证 GB18030 老源码按确定性编码读取并保留业务中文。</summary>
        [TestMethod]
        public void Decode_Gb18030Source_PreservesChineseCaption()
        {
            byte[] bytes = Encoding.GetEncoding("GB18030").GetBytes(
                ".Cols(\"Owner_Company_ID\").Caption = \"货主公司ID\"");

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            StringAssert.Contains(result.Text, "货主公司ID");
            Assert.AreEqual("GB18030", result.EncodingName);
            Assert.IsTrue(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证替换字符和可逆的典型二次转码片段分别触发乱码门禁。</summary>
        [DataTestMethod]
        [DataRow("\uFFFD")]
        [DataRow("涓枃")]
        [DataRow("涓氬姟")]
        [DataRow("鏄惁")]
        [DataRow("\u95B2\u56EA\u5598\u7481\u3220\u5D1F")]
        [DataRow("\u9352\u6D98\u7F13\u93C3\u5815\u68FF")]
        [DataRow("\u6D60\u64B3\u504D\u9351\u54C4\u53C6\u6434?")]
        [DataRow("\u7490\u3220\u59DF\u5A34\u4F79\u6309\u7490?")]
        [DataRow("\u9422\u71B6\u9A87\u9352\u5815??")]
        public void Decode_TypicalMojibake_IsMarkedUnreliable(string mojibake)
        {
            string text = ".Caption = \"" + mojibake + "\"";
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.IsFalse(result.IsReliable);
            Assert.IsTrue(result.HasMojibake);
        }

        /// <summary>XMZADD 20260911 验证合法罕见汉字和常见业务词不会被可逆乱码门禁误伤。</summary>
        [DataTestMethod]
        [DataRow("缂丝工艺")]
        [DataRow("采购订单")]
        [DataRow("创建时间")]
        [DataRow("财务流水账")]
        [DataRow("生产制造")]
        public void Decode_LegitimateBusinessChinese_RemainsReliable(string chineseText)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(".Caption = \"" + chineseText + "\"");

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(bytes);

            Assert.IsTrue(result.IsReliable);
            Assert.IsFalse(result.HasMojibake);
        }

        /// <summary>XMZADD 20260911 验证带字节序标记的 UTF-16 老源码也能确定性读取。</summary>
        [DataTestMethod]
        [DataRow(false, "UTF-16 LE BOM")]
        [DataRow(true, "UTF-16 BE BOM")]
        public void Decode_Utf16BomSource_PreservesChineseCaption(bool bigEndian, string expectedEncoding)
        {
            var encoding = new UnicodeEncoding(bigEndian, true, true);
            string source = ".Caption = \"操作记录创建时间\"";
            byte[] bytes = encoding.GetBytes(source);
            byte[] preamble = encoding.GetPreamble();
            var sourceBytes = new byte[preamble.Length + bytes.Length];
            Buffer.BlockCopy(preamble, 0, sourceBytes, 0, preamble.Length);
            Buffer.BlockCopy(bytes, 0, sourceBytes, preamble.Length, bytes.Length);

            SourceTextDecodeResult result = new SourceTextDecoder().Decode(sourceBytes);

            Assert.AreEqual(source, result.Text);
            Assert.AreEqual(expectedEncoding, result.EncodingName);
            Assert.IsTrue(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证无法完整解码的截断字节不会被系统默认代码页静默接收。</summary>
        [TestMethod]
        public void Decode_InvalidBytes_ReturnsUnreliableResult()
        {
            SourceTextDecodeResult result = new SourceTextDecoder().Decode(new byte[] { 0x81 });

            Assert.AreEqual(string.Empty, result.Text);
            Assert.AreEqual("Unknown", result.EncodingName);
            Assert.IsFalse(result.IsReliable);
        }

        /// <summary>XMZADD 20260911 验证分析器通过 GB18030 解码器提取老源码中的准确业务中文。</summary>
        [TestMethod]
        public void Analyze_Gb18030Source_PreservesChineseNameCandidate()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-gb-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Warehouse", "OwnerEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string source = "''' <summary>货主公司</summary>\r\n" +
                "Public Class Kis_T_OWNER_COMPANY\r\nEnd Class\r\n";
            File.WriteAllBytes(file, Encoding.GetEncoding("GB18030").GetBytes(source));

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence item = FindEvidence(evidence, "T_OWNER_COMPANY", null, "KisEntityClass");

                Assert.IsNotNull(item);
                Assert.AreEqual("货主公司", item.ChineseNameCandidate);
                Assert.AreEqual("EOS业务源码", item.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceStrength.Authoritative, item.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证典型错码只保留物理映射和冲突记录，不形成中文名称候选。</summary>
        [TestMethod]
        public void Analyze_MojibakeSource_DoesNotPublishChineseNameCandidate()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-bad-text-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Purchase", "BadEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string source = "''' <summary>\u93C4\u9359\u7F02</summary>\r\n" +
                "Public Class Kis_T_BAD_TEXT\r\nEnd Class\r\n";
            File.WriteAllBytes(file, new UTF8Encoding(false, true).GetBytes(source));

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence mapping = FindEvidence(evidence, "T_BAD_TEXT", null, "KisEntityClass");
                SourceEvidence warning = FindEvidence(evidence, null, null, "SourceEncodingUnreliable");

                Assert.IsNotNull(mapping);
                Assert.IsNull(mapping.ChineseNameCandidate);
                Assert.AreEqual(SourceEvidenceStrength.NamingOnly, mapping.Strength);
                Assert.IsNotNull(warning);
                Assert.AreEqual(SourceEvidenceStrength.NamingOnly, warning.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证乱码文件中的界面标题和枚举注释都只能留下审计记录。</summary>
        [TestMethod]
        public void Analyze_MojibakeCaptionAndEnum_AreRemovedFromCandidates()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-bad-caption-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Purchase", "BadCaption.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string source =
                "Dim order As New t_BAD_CAPTION\r\n" +
                "value = order.f_Status\r\n" +
                "fg.Cols(\"Status\").Caption = \"涓氬姟\"\r\n" +
                "Public Enum BadStatus\r\n" +
                "    ' 涓氬姟\r\n" +
                "    Active = 1\r\n" +
                "End Enum\r\n";
            File.WriteAllBytes(file, new UTF8Encoding(false, true).GetBytes(source));

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence caption = FindEvidence(evidence, "BAD_CAPTION", "Status", "GridColumnCaption");
                SourceEvidence enumItem = FindEvidence(evidence, null, null, "EnumMember");

                Assert.IsNotNull(caption);
                Assert.IsNull(caption.ChineseNameCandidate);
                Assert.AreEqual(SourceEvidenceStrength.NamingOnly, caption.Strength);
                Assert.AreEqual("EOS源码（编码不可靠）", caption.Evidence.SourceType);
                Assert.IsNotNull(enumItem);
                Assert.IsNull(enumItem.EnumChineseName);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证日志和备份目录中的历史源码不会参与业务字典推断。</summary>
        [TestMethod]
        public void Analyze_LogAndBackupDirectories_AreExcluded()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-ignore-" + Guid.NewGuid().ToString("N"));
            string[] directories = { "logs", "log", "backup", "temp", "tmp", "history" };
            for (int index = 0; index < directories.Length; index++)
            {
                string file = Path.Combine(root, directories[index], "Ignored" + index + ".vb");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "Public Class Kis_T_IGNORED_" + index + "\r\nEnd Class\r\n");
            }

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                for (int index = 0; index < evidence.Count; index++)
                {
                    Assert.IsFalse((evidence[index].ObjectName ?? string.Empty).StartsWith(
                        "T_IGNORED_", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260916 验证生成实体带明确来源角色且只提供物理映射，不发布机械中文名。</summary>
        [TestMethod]
        public void Analyze_GeneratedEntity_SeparatesPhysicalMappingFromChineseName()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-generated-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "表-类定义", "t_Order.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' 表-类生成代码\r\n" +
                "''' <summary>机械订单名称</summary>\r\n" +
                "Public Class t_Order\r\n" +
                "    Private mTable As String = \"Order\"\r\n" +
                "    ''' <summary>机械创建时间</summary>\r\n" +
                "    Public Property f_op_createtime As DateTime\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence table = FindEvidence(evidence, "Order", null, "EntityClassConvention");
                SourceEvidence field = FindEvidence(evidence, "Order", "op_createtime", "EntityProperty");

                Assert.IsNotNull(table);
                Assert.IsNotNull(field);
                Assert.IsNull(table.ChineseNameCandidate);
                Assert.IsNull(field.ChineseNameCandidate);
                Assert.AreEqual("EOS生成实体", table.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceOrigin.GeneratedEntity, table.Origin);
                Assert.AreEqual(SourceEvidenceOrigin.GeneratedEntity, field.Origin);
                Assert.AreEqual(SourceEvidenceStrength.Authoritative, table.Strength);
                Assert.AreEqual(SourceUsageKind.Unknown, table.UsageKind);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证真实 EOS 生成表类的 Imports 和扩展标记仍能隔离机械中文注释。</summary>
        [TestMethod]
        public void Analyze_RealGeneratedEntityHeader_IsRecognizedAfterImports()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-real-generated-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "表-类定义", "code_Account_Sheet.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Imports DataControl\r\n" +
                "Imports System.Data.SqlClient\r\n" +
                "''' <简介>\r\n" +
                "''' 表-类生成代码,本文件涉及数据表 Account_Sheet\r\n" +
                "''' </简介>\r\n" +
                "''' <summary>V1.4 表Account_Sheet，由生成程序自动生成</summary>\r\n" +
                "Partial Public Class t_Account_Sheet\r\n" +
                "    ''' <summary>默认值:(getdate())</summary>\r\n" +
                "    Public Property f_Account_Sheet_opDate As DateTime\r\n" +
                "End Class\r\n", Encoding.UTF8);

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence table = FindEvidence(evidence, "Account_Sheet", null, "EntityClassConvention");
                SourceEvidence field = FindEvidence(evidence, "Account_Sheet", "Account_Sheet_opDate", "EntityProperty");

                Assert.IsNotNull(table);
                Assert.IsNotNull(field);
                Assert.AreEqual("EOS生成实体", table.Evidence.SourceType);
                Assert.IsNull(table.ChineseNameCandidate);
                Assert.IsNull(field.ChineseNameCandidate);
                Assert.AreEqual(SourceEvidenceStrength.Authoritative, field.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证业务代码正文提到生成标记时不会被误判为表类生成文件。</summary>
        [TestMethod]
        public void Analyze_GeneratorMarkerInsideBusinessCode_DoesNotChangeFileKind()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-marker-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Purchase", "BusinessOrder.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "''' <summary>业务订单</summary>\r\n" +
                "Public Class t_BusinessOrder\r\n" +
                "    Private Const GeneratorNote As String = \"表-类生成代码\"\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence item = FindEvidence(evidence, "BusinessOrder", null, "EntityClassConvention");

                Assert.IsNotNull(item);
                Assert.AreEqual("业务订单", item.ChineseNameCandidate);
                Assert.AreEqual("EOS业务源码", item.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceStrength.Authoritative, item.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证通用自动生成源码只作为参考上下文而不冒充直接业务证据。</summary>
        [TestMethod]
        public void Analyze_AutoGeneratedReport_IsContextualEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-auto-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Reports", "GeneratedReport.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' <auto-generated>\r\n" +
                "''' <summary>订单自动报表</summary>\r\n" +
                "Public Class t_GeneratedReport\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence item = FindEvidence(evidence, "GeneratedReport", null, "EntityClassConvention");

                Assert.IsNotNull(item);
                Assert.AreEqual("订单自动报表", item.ChineseNameCandidate);
                Assert.AreEqual("EOS自动生成源码", item.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceStrength.Contextual, item.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260916 验证 Designer 与普通业务代码具有不同来源角色、用途和证据强度。</summary>
        [TestMethod]
        public void Analyze_DesignerAndBusinessCaptions_HaveDifferentStrength()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-strength-" + Guid.NewGuid().ToString("N"));
            string designerFile = Path.Combine(root, "ERP", "Sales", "Order.Designer.vb");
            string businessFile = Path.Combine(root, "ERP", "Sales", "OrderService.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(designerFile));
            File.WriteAllText(designerFile,
                "Dim designerOrder As New t_DESIGNER_ORDER\r\n" +
                "value = designerOrder.f_Owner_Company_ID\r\n" +
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\"\r\n");
            File.WriteAllText(businessFile,
                "Dim businessOrder As New t_BUSINESS_ORDER\r\n" +
                "value = businessOrder.f_Owner_Company_ID\r\n" +
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\"\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence designer = FindEvidence(evidence, "DESIGNER_ORDER", "Owner_Company_ID", "GridColumnCaption");
                SourceEvidence business = FindEvidence(evidence, "BUSINESS_ORDER", "Owner_Company_ID", "GridColumnCaption");

                Assert.IsNotNull(designer);
                Assert.IsNotNull(business);
                Assert.AreEqual("EOS设计器", designer.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceOrigin.Designer, designer.Origin);
                Assert.AreEqual(SourceEvidenceStrength.Contextual, designer.Strength);
                Assert.AreEqual(SourceUsageKind.Display, designer.UsageKind);
                Assert.AreEqual("EOS业务源码", business.Evidence.SourceType);
                Assert.AreEqual(SourceEvidenceOrigin.BusinessCode, business.Origin);
                Assert.AreEqual(SourceEvidenceStrength.DirectBusinessCode, business.Strength);
                Assert.AreEqual(SourceUsageKind.Display, business.UsageKind);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证实体赋值方向被标记为写入或读取而不依赖目录名称猜测。</summary>
        [TestMethod]
        public void Analyze_EntityAssignments_ClassifyReadAndWriteUsage()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-usage-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Sales", "OrderService.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Dim order As New t_Order\r\n" +
                "order.f_Write_Field = sourceValue\r\n" +
                "targetValue = order.f_Read_Field\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence write = FindEvidence(evidence, "Order", "Write_Field", "EntityFieldAssignment");
                SourceEvidence read = FindEvidence(evidence, "Order", "Read_Field", "EntityFieldAssignment");

                Assert.IsNotNull(write);
                Assert.IsNotNull(read);
                Assert.AreEqual(SourceUsageKind.Write, write.UsageKind);
                Assert.AreEqual(SourceUsageKind.Read, read.UsageKind);
                Assert.AreEqual(SourceEvidenceStrength.DirectBusinessCode, write.Strength);
                Assert.AreEqual(SourceEvidenceStrength.DirectBusinessCode, read.Strength);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证 INSERT SELECT 同行中目标表字段为写入而来源表字段保持读取。</summary>
        [TestMethod]
        public void Analyze_InsertSelect_ClassifiesTargetAndSourceDirections()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-sql-direction-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Warehouse", "TransferService.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Dim sql = \"INSERT INTO Target_Table (Target_Field) SELECT S.Source_Field FROM Source_Table S\"\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence targetTable = FindEvidence(evidence, "Target_Table", null, "SqlTableUsage");
                SourceEvidence sourceTable = FindEvidence(evidence, "Source_Table", null, "SqlTableUsage");
                SourceEvidence targetField = FindEvidence(evidence, "Target_Table", "Target_Field", "SqlFieldUsage");
                SourceEvidence sourceField = FindEvidence(evidence, "Source_Table", "Source_Field", "SqlFieldUsage");

                Assert.IsNotNull(targetTable);
                Assert.IsNotNull(sourceTable);
                Assert.IsNotNull(targetField);
                Assert.IsNotNull(sourceField);
                Assert.AreEqual(SourceUsageKind.Write, targetTable.UsageKind);
                Assert.AreEqual(SourceUsageKind.Read, sourceTable.UsageKind);
                Assert.AreEqual(SourceUsageKind.Write, targetField.UsageKind);
                Assert.AreEqual(SourceUsageKind.Read, sourceField.UsageKind);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证同一字段在同一文件先读后写或先写后读时两种用途都不会被去重吞掉。</summary>
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Analyze_SameSqlFieldReadAndWrite_PreservesBothDirections(bool writeFirst)
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-both-directions-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Sales", "OrderService.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string read = "Dim readSql = \"SELECT Name FROM T_ORDER\"\r\n";
            string write = "Dim writeSql = \"UPDATE T_ORDER SET Name='New'\"\r\n";
            File.WriteAllText(file, writeFirst ? write + read : read + write);

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsTrue(ContainsEvidenceUsage(evidence, "T_ORDER", "Name", "SqlFieldUsage", SourceUsageKind.Read));
                Assert.IsTrue(ContainsEvidenceUsage(evidence, "T_ORDER", "Name", "SqlFieldUsage", SourceUsageKind.Write));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证 DELETE 和 CREATE 的显式及动态目标均被标记为写入。</summary>
        [TestMethod]
        public void Analyze_DeleteAndCreateTargets_AreWriteUsage()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-delete-create-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "Warehouse", "ArchiveService.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Public Class t_ArchiveConfig\r\n" +
                "    Dim deleteSql = \"DELETE FROM Obsolete_Order\"\r\n" +
                "    Dim createSql = \"CREATE TABLE New_Order (ID int)\"\r\n" +
                "    Dim dynamicDelete = \"DELETE FROM \" & config.f_Table_Storage_Account\r\n" +
                "    Dim dynamicCreate = \"CREATE TABLE \" & config.f_Table_Storage_Event\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsTrue(ContainsEvidenceUsage(evidence, "Obsolete_Order", null, "SqlTableUsage", SourceUsageKind.Write));
                Assert.IsTrue(ContainsEvidenceUsage(evidence, "New_Order", null, "SqlTableUsage", SourceUsageKind.Write));
                Assert.IsTrue(ContainsEvidenceUsage(evidence, "ArchiveConfig", "Table_Storage_Account", "DynamicTableFieldUsage", SourceUsageKind.Write));
                Assert.IsTrue(ContainsEvidenceUsage(evidence, "ArchiveConfig", "Table_Storage_Event", "DynamicTableFieldUsage", SourceUsageKind.Write));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260911 验证单次 SQL JOIN 关系只提供上下文参考，不能直接升级为权威关系。</summary>
        [TestMethod]
        public void Analyze_SingleSqlJoinRelation_RemainsContextual()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-relation-strength-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "DA", "AcceptanceQuery.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Dim sql = \"SELECT A.Owner_Company_ID FROM DA_Acceptance A " +
                "JOIN Company C ON A.Owner_Company_ID=C.Company_ID\"\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence relation = FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation");

                Assert.IsNotNull(relation);
                Assert.AreEqual(SourceEvidenceStrength.Contextual, relation.Strength);
                Assert.AreEqual(SourceUsageKind.Relation, relation.UsageKind);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260903 验证 EOS 表类生成代码可映射真实物理表、字段和对象关系，并排除字段序号枚举。</summary>
        [TestMethod]
        public void Analyze_EosGeneratedEntity_MapsPhysicalMembersAndSkipsOrdinalEnum()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-eos-entity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "表-类定义", "t_Item.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' 表-类生成代码\r\n" +
                "<Serializable> Partial Public Class t_Item\r\n" +
                "    Private mTable As String = \"Item\"\r\n" +
                "    Public Property f_Item_ID() As Int64\r\n" +
                "    Public Property objCompany_ID As t_Company\r\n" +
                "    Public Enum en_Item\r\n" +
                "        Item_ID = 0\r\n" +
                "    End Enum\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence tableEvidence = null;
                SourceEvidence fieldEvidence = null;
                SourceEvidence relationEvidence = null;
                bool hasGeneratedEnumMember = false;
                for (int index = 0; index < evidence.Count; index++)
                {
                    SourceEvidence item = evidence[index];
                    if (item.ObjectName == "Item" && item.FieldName == null && item.EntityName == "t_Item")
                    {
                        tableEvidence = item;
                    }
                    if (item.ObjectName == "Item" && item.FieldName == "Item_ID")
                    {
                        fieldEvidence = item;
                    }
                    if (item.Evidence != null && item.Evidence.RuleName == "EntityObjectRelation")
                    {
                        relationEvidence = item;
                    }
                    if (item.EnumName == "en_Item" && item.EnumValue == "Item_ID")
                    {
                        hasGeneratedEnumMember = true;
                    }
                }

                Assert.IsNotNull(tableEvidence);
                Assert.IsNotNull(fieldEvidence);
                Assert.AreEqual("f_Item_ID", fieldEvidence.PropertyName);
                Assert.IsNotNull(relationEvidence);
                Assert.AreEqual("Company_ID", relationEvidence.RelationFieldName);
                Assert.AreEqual("t_Company", relationEvidence.RelationTargetEntity);
                Assert.IsFalse(hasGeneratedEnumMember);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260918 验证生成实体的 mIDCol 声明可作为缺失数据库主键约束时的代码级主键证据。</summary>
        [TestMethod]
        public void Analyze_GeneratedEntityIdColumn_PublishesPrimaryKeyEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-entity-key-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "ERP", "表-类定义", "code_Order.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' 表-类生成代码\r\n" +
                "Partial Public Class t_Order\r\n" +
                "    Shared ReadOnly mIDCol As String = \"Order_ID\"\r\n" +
                "    Public Property f_Order_ID() As Int64\r\n" +
                "End Class\r\n", Encoding.UTF8);

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence key = FindEvidence(evidence, "Order", "Order_ID", "EntityPrimaryKey");

                Assert.IsNotNull(key);
                Assert.AreEqual(SourceEvidenceOrigin.GeneratedEntity, key.Origin);
                Assert.AreEqual(SourceEvidenceStrength.Authoritative, key.Strength);
                Assert.AreEqual(SourceUsageKind.Unknown, key.UsageKind);
                Assert.AreEqual(3, key.Evidence.SourceLine);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestMethod]
        public void Analyze_FindsTableEntityFieldAndEnumEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "DemoEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' 示例订单\r\n" +
                "Public Class Kis_T_DEMO_ORDER\r\n" +
                "    Public Const TableName As String = \"T_DEMO_ORDER\"\r\n" +
                "    ' 订单状态\r\n" +
                "    Public Property FSTATUS As Integer\r\n" +
                "    Public Enum DemoStatus\r\n" +
                "        NewStatus = 1\r\n" +
                "    End Enum\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);

                bool hasEntity = false;
                bool hasField = false;
                bool hasEnum = false;
                bool hasSourceLine = false;
                bool hasRelativeSourcePath = false;
                for (int i = 0; i < evidence.Count; i++)
                {
                    if (evidence[i].ObjectName == "T_DEMO_ORDER" && evidence[i].EntityName == "Kis_T_DEMO_ORDER")
                    {
                        hasEntity = true;
                    }
                    if (evidence[i].ObjectName == "T_DEMO_ORDER" && evidence[i].FieldName == "FSTATUS")
                    {
                        hasField = true;
                    }
                    if (evidence[i].EnumName == "DemoStatus" && evidence[i].EnumValue == "NewStatus")
                    {
                        hasEnum = true;
                    }
                    if (evidence[i].Evidence != null && evidence[i].Evidence.SourceLine > 0)
                    {
                        hasSourceLine = true;
                    }
                    if (evidence[i].Evidence != null &&
                        evidence[i].Evidence.SourcePath == "Purchase/DemoEntity.vb")
                    {
                        hasRelativeSourcePath = true;
                    }
                }

                Assert.IsTrue(hasEntity);
                Assert.IsTrue(hasField);
                Assert.IsTrue(hasEnum);
                Assert.IsTrue(hasSourceLine);
                Assert.IsTrue(hasRelativeSourcePath);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证标准 XML 文档注释会成为表和字段的干净中文证据。</summary>
        [TestMethod]
        public void Analyze_UsesXmlSummaryForTableAndField()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "OrderEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "''' <summary>\r\n" +
                "''' 采购订单主表\r\n" +
                "''' </summary>\r\n" +
                "Public Class Kis_T_PURCHASE_ORDER\r\n" +
                "    ''' <summary>\r\n" +
                "    ''' 采购单号\r\n" +
                "    ''' </summary>\r\n" +
                "    Public Property FBILLNO As String\r\n" +
                "    Public Property FSTATUS As Integer\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                string tableName = null;
                string fieldName = null;
                string fieldWithoutSummary = "未找到";
                for (int i = 0; i < evidence.Count; i++)
                {
                    if (evidence[i].ObjectName == "T_PURCHASE_ORDER" && string.IsNullOrWhiteSpace(evidence[i].FieldName))
                    {
                        tableName = evidence[i].ChineseNameCandidate;
                    }
                    if (evidence[i].ObjectName == "T_PURCHASE_ORDER" && evidence[i].FieldName == "FBILLNO")
                    {
                        fieldName = evidence[i].ChineseNameCandidate;
                    }
                    if (evidence[i].ObjectName == "T_PURCHASE_ORDER" && evidence[i].FieldName == "FSTATUS")
                    {
                        fieldWithoutSummary = evidence[i].ChineseNameCandidate;
                    }
                }

                Assert.AreEqual("采购订单主表", tableName);
                Assert.AreEqual("采购单号", fieldName);
                Assert.IsNull(fieldWithoutSummary);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260828 验证已取消的源码扫描不会开始遍历 EOS 项目目录。</summary>
        [TestMethod]
        public void Analyze_PreCancelled_ThrowsOperationCanceledException()
        {
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsException<OperationCanceledException>(
                () => new EosSourceAnalyzer().Analyze(Path.GetTempPath(), cancellation.Token));
        }

        /// <summary>XMZADD 20260901 验证超大源码文件会在读取前终止整次扫描，避免发布不完整的结构证据。</summary>
        [TestMethod]
        public void Analyze_OversizedSourceFile_ThrowsInvalidDataException()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Oversized.vb");
            try
            {
                File.WriteAllBytes(file, new byte[(4 * 1024 * 1024) + 1]);

                Assert.ThrowsException<InvalidDataException>(() => new EosSourceAnalyzer().Analyze(root));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260902 验证完整 EOS 根目录中的依赖包、测试数据和资源文件不占用业务源码扫描边界。</summary>
        [TestMethod]
        public void Analyze_ExcludesDependenciesTestDataAndResourceFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-filter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string businessFile = Path.Combine(root, "ERP", "OrderEntity.vb");
            string packageFile = Path.Combine(root, "packages", "PackageEntity.vb");
            string testDataFile = Path.Combine(root, "_codex_testdata", "TestEntity.vb");
            string resourceFile = Path.Combine(root, "ERP", "Images.resx");
            Directory.CreateDirectory(Path.GetDirectoryName(businessFile));
            Directory.CreateDirectory(Path.GetDirectoryName(packageFile));
            Directory.CreateDirectory(Path.GetDirectoryName(testDataFile));
            try
            {
                File.WriteAllText(businessFile, "Public Class Kis_T_ORDER\r\nEnd Class\r\n");
                File.WriteAllText(packageFile, "Public Class Kis_T_PACKAGE\r\nEnd Class\r\n");
                File.WriteAllText(testDataFile, "Public Class Kis_T_TESTDATA\r\nEnd Class\r\n");
                File.WriteAllBytes(resourceFile, new byte[(2 * 1024 * 1024) + 1]);

                var evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsNotNull(FindEvidence(evidence, "T_ORDER", null, "KisEntityClass"));
                Assert.IsNull(FindEvidence(evidence, "T_PACKAGE", null, "KisEntityClass"));
                Assert.IsNull(FindEvidence(evidence, "T_TESTDATA", null, "KisEntityClass"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证任一受支持源码文件不可读时阻断整次扫描，避免发布不完整证据。</summary>
        [TestMethod]
        public void Analyze_SupportedFileCannotBeRead_BlocksWholeScan()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-locked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Locked.vb");
            File.WriteAllText(file, "Public Class Locked\r\nEnd Class");
            try
            {
                using (var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.ThrowsException<AggregateException>(() => new EosSourceAnalyzer().Analyze(root));
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证远离声明的过程注释不会错误成为表中文名称。</summary>
        [TestMethod]
        public void Analyze_DoesNotReuseDistantProceduralCommentForTableName()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "ucPicView.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "' Delete_ID(),Delete_N表示删除的图片的ID和总数\r\n" +
                "Public Class ucPicView\r\n" +
                "    Public Sub LoadItem()\r\n" +
                "        Dim value As Integer\r\n" +
                "        value = 1\r\n" +
                "        TableName = \"Item_Image\"\r\n" +
                "    End Sub\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence tableEvidence = FindEvidence(evidence, "Item_Image", null, "TableNameProperty");

                Assert.IsNotNull(tableEvidence);
                Assert.IsNull(tableEvidence.ChineseNameCandidate);
                Assert.AreEqual("其他", tableEvidence.ModulePath);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证 SQL 使用位置能为表提供中文模块上下文。</summary>
        [TestMethod]
        public void Analyze_FindsSqlTableUsageAndChineseModule()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "Item 物料", "frmMaterial.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Public Class frmMaterial\r\n" +
                "    Public Sub LoadImage()\r\n" +
                "        Dim sql = \"Select Img_ID From Item_Image Where Item_ID=1\"\r\n" +
                "    End Sub\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence usage = FindEvidence(evidence, "Item_Image", null, "SqlTableUsage");

                Assert.IsNotNull(usage);
                Assert.AreEqual("Item 物料", usage.ModulePath);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证实体属性会保留源码声明类型供枚举值匹配。</summary>
        [TestMethod]
        public void Analyze_CapturesPropertyTypeForEnumMapping()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "DemoEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Public Class Kis_T_DEMO_ORDER\r\n" +
                "    Public Property FSTATUS As DemoStatus\r\n" +
                "    Public Enum DemoStatus\r\n" +
                "        ' 已审核\r\n" +
                "        Approved = 1\r\n" +
                "    End Enum\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence property = FindEvidence(evidence, "T_DEMO_ORDER", "FSTATUS", "EntityProperty");

                Assert.IsNotNull(property);
                Assert.AreEqual("DemoStatus", property.PropertyTypeName);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证被其他代码隔开的 XML 摘要不会污染后续实体表名。</summary>
        [TestMethod]
        public void Analyze_DistantXmlSummary_DoesNotBecomeTableName()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "OrderEntity.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "''' <summary>删除临时图片</summary>\r\n" +
                "Public Const Version As Integer = 1\r\n" +
                "Public Class Kis_T_PURCHASE_ORDER\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                SourceEvidence table = FindEvidence(evidence, "T_PURCHASE_ORDER", null, "KisEntityClass");

                Assert.IsNotNull(table);
                Assert.IsNull(table.ChineseNameCandidate);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证进入普通类后会结束上一实体的表作用域，避免属性串到错误表。</summary>
        [TestMethod]
        public void Analyze_NewOrdinaryClass_ClearsPreviousTableScope()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "MixedClasses.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Public Class Kis_T_PURCHASE_ORDER\r\n" +
                "End Class\r\n" +
                "Public Class PurchaseWindow\r\n" +
                "    Public Property WindowTitle As String\r\n" +
                "End Class\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsNull(FindEvidence(evidence, "T_PURCHASE_ORDER", "WindowTitle", "EntityProperty"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证 C# 枚举右大括号会结束枚举作用域，不误收后续标识符。</summary>
        [TestMethod]
        public void Analyze_CSharpEnumBrace_StopsCollectingMembers()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Purchase", "OrderStatus.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "public enum OrderStatus\r\n" +
                "{\r\n" +
                "    NewOrder = 1\r\n" +
                "}\r\n" +
                "AccidentalIdentifier\r\n");

            try
            {
                var evidence = new EosSourceAnalyzer().Analyze(root);
                bool hasAccidentalMember = false;
                for (int i = 0; i < evidence.Count; i++)
                {
                    if (evidence[i].EnumName == "OrderStatus" && evidence[i].EnumValue == "AccidentalIdentifier")
                    {
                        hasAccidentalMember = true;
                    }
                }

                Assert.IsFalse(hasAccidentalMember);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260904 验证 Partial 实体中的动态建表和写入语句可还原配置字段的真实业务用途。</summary>
        [TestMethod]
        public void Analyze_PartialEntityDynamicTableFields_CollectsBusinessUsageEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "表-类定义", "code_Storage_Partial.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Partial Class t_Account_Storage_Part_Definition\r\n" +
                "    ''' 生成车间的区域存储相关表\r\n" +
                "    Public Shared Sub Build_Bu_Storage_Part_Area_Data()\r\n" +
                "        S = \"CREATE TABLE [dbo].[\" & BST.f_Table_Storage_Event & \"](\"\r\n" +
                "        Insert_IO = \"Insert Into \" & BST.f_Table_Storage_Account & \" (Item_ID)\"\r\n" +
                "        Insert_Inv = \"Insert Into \" & BST.f_Table_Storage_Inv & \" (Item_ID)\"\r\n" +
                "    End Sub\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                AssertPublishedEvidence(evidence, "Account_Storage_Part_Definition", null, "EntityClassConvention");
                AssertPublishedEvidence(evidence, "Account_Storage_Part_Definition", "Table_Storage_Event", "DynamicTableFieldUsage");
                AssertPublishedEvidence(evidence, "Account_Storage_Part_Definition", "Table_Storage_Account", "DynamicTableFieldUsage");
                AssertPublishedEvidence(evidence, "Account_Storage_Part_Definition", "Table_Storage_Inv", "DynamicTableFieldUsage");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260904 验证分析器使用已读取源码追加真实业务字段用途证据。</summary>
        [TestMethod]
        public void Analyze_AppendsBusinessUsageEvidenceFromLoadedLines()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-business-usage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "DA", "frmAcceptance.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "Dim sql As String = \"From DA_Acceptance DAA \" & _\r\n" +
                " \"Inner Join Company C On DAA.Owner_Company_ID=C.Company_ID\"\r\n" +
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\"\r\n" +
                "Dim acceptance As New t_DA_Acceptance\r\n" +
                "acceptance.f_op_createtime = Now\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                SourceEvidence relation = FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation");
                Assert.IsNotNull(relation);
                Assert.AreEqual("Company", relation.RelationTargetObjectName);
                Assert.AreEqual("Company_ID", relation.RelationTargetFieldName);
                Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "GridColumnCaption"));
                Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "op_createtime", "EntityFieldAssignment"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260904 验证原有实体、枚举、动态表和 SQL 表证据均保留实际源码原行。</summary>
        [TestMethod]
        public void Analyze_LegacyEvidence_PreservesOriginalSourceLine()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-legacy-original-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "Item", "t_Item.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "<Serializable> Partial Public Class t_Item\r\n" +
                "    Private mTable As String = \"Item\"\r\n" +
                "    Public Property f_Item_ID() As Int64\r\n" +
                "    Public Property objCompany_ID As t_Company\r\n" +
                "    Public Enum ItemStatus\r\n" +
                "        Active = 1\r\n" +
                "    End Enum\r\n" +
                "    Dim sql As String = \"SELECT Item_ID FROM Item_Image\"\r\n" +
                "    Dim dynamicSql As String = \"SELECT * FROM \" & config.f_Table_Storage_Event\r\n" +
                "End Class\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                AssertOriginalText(evidence, "Item", null, "EntityClassConvention", "Class t_Item");
                AssertOriginalText(evidence, "Item", null, "TableNameProperty", "mTable As String");
                AssertOriginalText(evidence, "Item", "Item_ID", "EntityProperty", "Property f_Item_ID");
                AssertOriginalText(evidence, "Item", null, "EntityObjectRelation", "objCompany_ID As t_Company");
                AssertOriginalText(evidence, "Item", null, "EnumMember", "Active = 1");
                AssertOriginalText(evidence, "Item", "Table_Storage_Event", "DynamicTableFieldUsage", "f_Table_Storage_Event");
                AssertOriginalText(evidence, "Item_Image", null, "SqlTableUsage", "FROM Item_Image");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260905 验证分析器集成层忽略独立 REM 注释且保留 Rem 前缀普通代码。</summary>
        [TestMethod]
        public void Analyze_RemCommentOnlyLines_DoNotPublishLegacySqlTableUsage()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-rem-comment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "Common", "RemComments.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "REM Private mTable As String = \"Secret_Object\"\r\n" +
                "   rEm TableName = \"Hidden_Object\"\r\n" +
                "REM Dim sql = \"SELECT Secret_ID FROM Secret_Table\"\r\n" +
                "   rEm    Dim sql = \"SELECT Hidden_ID FROM Hidden_Table\"\r\n" +
                "Remote = \"SELECT Remote_ID FROM Remote_Table\"\r\n" +
                "Remark = \"SELECT Remark_ID FROM Remark_Table\"\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsNull(FindEvidence(evidence, "Secret_Object", null, "TableNameProperty"));
                Assert.IsNull(FindEvidence(evidence, "Hidden_Object", null, "TableNameProperty"));
                Assert.IsNull(FindEvidence(evidence, "Secret_Table", null, "SqlTableUsage"));
                Assert.IsNull(FindEvidence(evidence, "Hidden_Table", null, "SqlTableUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "Remote_Table", null, "SqlTableUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "Remark_Table", null, "SqlTableUsage"));
                for (int evidenceIndex = 0; evidenceIndex < evidence.Count; evidenceIndex++)
                {
                    string originalText = evidence[evidenceIndex].Evidence == null
                        ? string.Empty
                        : evidence[evidenceIndex].Evidence.OriginalText ?? string.Empty;
                    Assert.IsTrue(originalText.IndexOf("Secret", StringComparison.OrdinalIgnoreCase) < 0);
                    Assert.IsTrue(originalText.IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) < 0);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260905 验证 XML 配置仅保留原分析且大小写扩展名源码仍可提取业务字段用途。</summary>
        [TestMethod]
        public void Analyze_BusinessUsageExtractor_OnlyRunsForCodeExtensionsIgnoringCase()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-code-extension-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string xmlFile = Path.Combine(root, "Config", "zxing.XML");
            string codeFile = Path.Combine(root, "ERP", "DA", "Business.Vb");
            Directory.CreateDirectory(Path.GetDirectoryName(xmlFile));
            Directory.CreateDirectory(Path.GetDirectoryName(codeFile));
            File.WriteAllText(xmlFile,
                "<zxing:result text=\"SELECT A.Owner_Company_ID FROM DA_Acceptance A " +
                "JOIN Company C ON A.Owner_Company_ID=C.Company_ID\" />\r\n");
            File.WriteAllText(codeFile,
                "Dim sql = \"SELECT Owner_Company_ID FROM DA_Acceptance\"\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", null, "SqlTableUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage"));
                for (int evidenceIndex = 0; evidenceIndex < evidence.Count; evidenceIndex++)
                {
                    SourceEvidence item = evidence[evidenceIndex];
                    if (item.Evidence == null ||
                        !string.Equals(item.Evidence.SourcePath, "Config/zxing.XML", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Assert.IsFalse(string.Equals(item.Evidence.RuleName, "SqlFieldUsage", StringComparison.Ordinal));
                    Assert.IsFalse(string.Equals(item.Evidence.RuleName, "SqlFieldRelation", StringComparison.Ordinal));
                    Assert.IsFalse(string.Equals(item.Evidence.RuleName, "GridColumnCaption", StringComparison.Ordinal));
                    Assert.IsFalse(string.Equals(item.Evidence.RuleName, "DataColumnCaption", StringComparison.Ordinal));
                    Assert.IsFalse(string.Equals(item.Evidence.RuleName, "EntityFieldAssignment", StringComparison.Ordinal));
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260905 验证 C# 块注释不会污染旧结构扫描或业务用途提取且字符串分隔符不改变注释状态。</summary>
        [TestMethod]
        public void Analyze_CSharpBlockComments_DoNotPublishLegacyOrBusinessEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-csharp-block-comment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "ERP", "DA", "BlockComments.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "/* Private mTable As String = \"Secret_Object\"; var sql = \"SELECT Secret_ID FROM Secret_Table\"; */\r\n" +
                "/*\r\n" +
                "public class t_Hidden\r\n" +
                "{\r\n" +
                "    public long f_Hidden_ID { get; set; }\r\n" +
                "    var sql = \"SELECT Hidden_ID FROM Hidden_Table\";\r\n" +
                "}\r\n" +
                "*/\r\n" +
                "public class t_Visible\r\n" +
                "{\r\n" +
                "    public long f_Visible_ID { get; set; }\r\n" +
                "}\r\n" +
                "var beforeSql = \"SELECT Before_ID FROM Before_Table\"; /* var ghostSql = \"SELECT Ghost_ID FROM Ghost_Table\"; */\r\n" +
                "/* var maskedSql = \"SELECT Masked_ID FROM Masked_Table\"; */ var afterSql = \"SELECT After_ID FROM After_Table\";\r\n" +
                "var blockStartLiteral = \"/*\";\r\n" +
                "var quotedSql = \"SELECT Quoted_ID FROM Quoted_Table\";\r\n" +
                "var blockEndLiteral = \"*/\";\r\n" +
                "// /* var hiddenLineSql = \"SELECT HiddenLine_ID FROM HiddenLine_Table\"; */\r\n" +
                "var finalSql = \"SELECT Final_ID FROM Final_Table\";\r\n");

            try
            {
                IList<SourceEvidence> evidence = new EosSourceAnalyzer().Analyze(root);

                Assert.IsNotNull(FindEvidence(evidence, "Visible", null, "EntityClassConvention"));
                Assert.IsNotNull(FindEvidence(evidence, "Visible", "Visible_ID", "EntityProperty"));
                Assert.IsNotNull(FindEvidence(evidence, "Before_Table", "Before_ID", "SqlFieldUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "After_Table", "After_ID", "SqlFieldUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "Quoted_Table", "Quoted_ID", "SqlFieldUsage"));
                Assert.IsNotNull(FindEvidence(evidence, "Final_Table", "Final_ID", "SqlFieldUsage"));
                Assert.IsNull(FindEvidence(evidence, "Ghost_Table", null, "SqlTableUsage"));
                Assert.IsNull(FindEvidence(evidence, "Masked_Table", null, "SqlTableUsage"));
                AssertBusinessIdentityDoesNotContain(evidence, "Secret");
                AssertBusinessIdentityDoesNotContain(evidence, "Hidden");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260904 断言指定原有规则的证据包含预期源码原行。</summary>
        private static void AssertOriginalText(IList<SourceEvidence> evidence, string objectName,
            string fieldName, string ruleName, string expectedText)
        {
            SourceEvidence item = FindEvidence(evidence, objectName, fieldName, ruleName);
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Evidence);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Evidence.OriginalText));
            StringAssert.Contains(item.Evidence.OriginalText, expectedText);
        }

        /// <summary>XMZADD 20260904 验证业务用途证据包含可定位源码位置和明确解释。</summary>
        private static void AssertPublishedEvidence(IList<SourceEvidence> evidence,
            string objectName, string fieldName, string ruleName)
        {
            SourceEvidence item = FindEvidence(evidence, objectName, fieldName, ruleName);
            Assert.IsNotNull(item);
            Assert.IsNotNull(item.Evidence);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Evidence.SourcePath));
            Assert.IsTrue(item.Evidence.SourceLine > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Evidence.Explanation));
        }

        /// <summary>XMZADD 20260905 遍历新旧证据和值，确保 C# 块注释内标识没有进入扫描结果。</summary>
        private static void AssertBusinessIdentityDoesNotContain(IList<SourceEvidence> evidence, string forbiddenText)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                AssertTextDoesNotContain(item.ObjectName, forbiddenText);
                AssertTextDoesNotContain(item.FieldName, forbiddenText);
                AssertTextDoesNotContain(item.EntityName, forbiddenText);
                AssertTextDoesNotContain(item.EnumName, forbiddenText);
                AssertTextDoesNotContain(item.EnumValue, forbiddenText);
                AssertTextDoesNotContain(item.PropertyName, forbiddenText);
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
                "证据不应包含 C# 块注释标识：" + forbiddenText + "，实际值：" + value);
        }

        /// <summary>XMZADD 20260831 按对象、字段和提取规则定位测试证据。</summary>
        private static SourceEvidence FindEvidence(System.Collections.Generic.IList<SourceEvidence> evidence,
            string objectName, string fieldName, string ruleName)
        {
            for (int i = 0; i < evidence.Count; i++)
            {
                SourceEvidence item = evidence[i];
                if (string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                    item.Evidence != null && string.Equals(item.Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260911 按对象、字段、规则和用途判断方向证据是否存在。</summary>
        private static bool ContainsEvidenceUsage(IList<SourceEvidence> evidence, string objectName,
            string fieldName, string ruleName, SourceUsageKind usageKind)
        {
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                    item.Evidence != null && string.Equals(item.Evidence.RuleName, ruleName, StringComparison.Ordinal) &&
                    item.UsageKind == usageKind)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
