using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 将本地待上传字典批次转换为可持久化且可安全恢复的 JSON。</summary>
    public static class DictionaryJsonSerializer
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>XMZADD 20260901 序列化完整编辑批次以保证本地队列重试时使用同一份幂等载荷。</summary>
        public static string SerializeBatch(DictionaryChangeBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }

            try
            {
                DataContractJsonSerializer serializer = CreateSerializer(false);
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, batch);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is InvalidDataException || exception is SerializationException)
                {
                    throw new SerializationException("本地字典批次无法序列化。", exception);
                }

                throw;
            }
        }

        /// <summary>XMZADD 20260903 生成公开 Issue 专用事件载荷，仅发布可复现的操作记录并排除本机人工覆盖存储。</summary>
        public static string SerializePublicBatch(DictionaryChangeBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }

            var publicBatch = new DictionaryChangeBatch
            {
                BatchId = batch.BatchId,
                AuthorGitHubUserId = batch.AuthorGitHubUserId,
                CreatedAtUtc = batch.CreatedAtUtc,
                Operations = CopyList(batch.Operations)
            };
            return SerializeBatch(publicBatch);
        }

        /// <summary>XMZADD 20260901 反序列化本地队列载荷并隐藏损坏载荷内容，避免诊断信息泄露编辑数据。</summary>
        public static DictionaryChangeBatch DeserializeBatch(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new SerializationException("本地字典批次 JSON 为空或格式无效。");
            }

            try
            {
                DataContractJsonSerializer serializer = CreateSerializer(false);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DictionaryChangeBatch batch = serializer.ReadObject(stream) as DictionaryChangeBatch;
                    if (batch == null)
                    {
                        throw new SerializationException();
                    }

                    NormalizeMutableCollections(batch);
                    return batch;
                }
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is InvalidDataException || exception is SerializationException)
                {
                    throw new SerializationException("本地字典批次 JSON 格式无效。");
                }

                throw;
            }
        }

        /// <summary>XMZADD 20260901 从远程原始 UTF-8 字节解析事件批次并保留未知成员供严格白名单校验。</summary>
        public static DictionaryChangeBatch DeserializeRemoteBatch(byte[] utf8Json)
        {
            if (utf8Json == null || utf8Json.Length == 0)
            {
                throw new SerializationException("远程字典批次 JSON 为空或格式无效。");
            }
            try
            {
                string json = StrictUtf8.GetString(utf8Json);
                DataContractJsonSerializer serializer = CreateSerializer(false);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DictionaryChangeBatch batch = serializer.ReadObject(stream) as DictionaryChangeBatch;
                    if (batch == null)
                    {
                        throw new SerializationException();
                    }

                    // 远程入口故意不遍历或转换嵌套集合，操作数量边界必须由验证器优先判断。
                    return batch;
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                throw new SerializationException("远程字典批次 JSON 格式无效。", exception);
            }
        }

        /// <summary>XMZADD 20260901 通过扩展数据保留差异识别任意层未知远程成员，避免把正常创建的空容器误判为未知字段。</summary>
        internal static bool ContainsUnknownRemoteMembers(DictionaryChangeBatch batch)
        {
            if (batch == null)
            {
                return false;
            }

            byte[] preserving = SerializeForExtensionComparison(batch, false);
            byte[] ignoring = SerializeForExtensionComparison(batch, true);
            if (preserving.Length != ignoring.Length)
            {
                return true;
            }
            for (int index = 0; index < preserving.Length; index++)
            {
                if (preserving[index] != ignoring[index])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 使用一致契约生成扩展数据对比字节，差异只来源于是否保留未知远程成员。</summary>
        private static byte[] SerializeForExtensionComparison(DictionaryChangeBatch batch, bool ignoreExtensionData)
        {
            DataContractJsonSerializer serializer = CreateSerializer(ignoreExtensionData);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, batch);
                return stream.ToArray();
            }
        }

        /// <summary>XMZADD 20260901 集中创建日期格式一致的批次序列化器，确保持久化和严格远程比较使用同一契约。</summary>
        private static DataContractJsonSerializer CreateSerializer(bool ignoreExtensionData)
        {
            return new DataContractJsonSerializer(
                typeof(DictionaryChangeBatch),
                new DataContractJsonSerializerSettings
                {
                    DateTimeFormat = new DateTimeFormat("o", CultureInfo.InvariantCulture),
                    IgnoreExtensionDataObject = ignoreExtensionData
                });
        }

        /// <summary>XMZADD 20260901 将数据契约恢复出的定长接口集合转换为可继续追加的本地队列列表。</summary>
        private static void NormalizeMutableCollections(DictionaryChangeBatch batch)
        {
            batch.Overrides = CopyList(batch.Overrides);
            batch.Operations = CopyList(batch.Operations);
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = batch.Operations[index];
                if (operation == null)
                {
                    continue;
                }
                operation.Evidence = CopyList(operation.Evidence);
                if (operation.TablePayload != null)
                {
                    operation.TablePayload.Fields = CopyList(operation.TablePayload.Fields);
                    operation.TablePayload.Relations = CopyList(operation.TablePayload.Relations);
                }
            }
        }

        /// <summary>XMZADD 20260901 复制接口集合并为空载荷创建传统可增删列表。</summary>
        private static System.Collections.Generic.IList<T> CopyList<T>(System.Collections.Generic.IList<T> source)
        {
            var result = new System.Collections.Generic.List<T>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            return result;
        }
    }
}
