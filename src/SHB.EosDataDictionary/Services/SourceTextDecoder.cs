using System;
using System.Text;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260911 保存一次源码解码的文本、编码和可作为业务证据的可靠性。</summary>
    public sealed class SourceTextDecodeResult
    {
        /// <summary>XMZADD 20260911 创建不可变的源码解码结论，供扫描器统一执行乱码门禁。</summary>
        internal SourceTextDecodeResult(string text, string encodingName, bool isReliable,
            bool hasMojibake, int qualityScore)
        {
            Text = text ?? string.Empty;
            EncodingName = encodingName ?? "Unknown";
            IsReliable = isReliable;
            HasMojibake = hasMojibake;
            QualityScore = qualityScore;
        }

        public string Text { get; private set; }
        public string EncodingName { get; private set; }
        public bool IsReliable { get; private set; }
        public bool HasMojibake { get; private set; }
        public int QualityScore { get; private set; }
    }

    /// <summary>XMZADD 20260911 按确定性顺序识别 UTF 与 GB18030 源码，阻止本机默认代码页改变证据。</summary>
    public sealed class SourceTextDecoder
    {
        private const int Utf8PreferenceScore = 180;
        private static readonly string[] TypicalMojibakeFragments =
        {
            "涓氬姟",
            "婧愮爜",
            "璇佹嵁",
            "鍐欏叆",
            "璇诲彇",
            "缂栫爜",
            "鏄鍙缂"
        };
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(false, false, true);
        private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(true, false, true);
        private static readonly Encoding StrictGb18030 = Encoding.GetEncoding(
            54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        /// <summary>XMZADD 20260911 解码一个源码字节数组并返回是否可安全提取中文业务证据。</summary>
        public SourceTextDecodeResult Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException("bytes");
            }
            if (bytes.Length == 0)
            {
                return new SourceTextDecodeResult(string.Empty, "UTF-8", true, false, Utf8PreferenceScore);
            }

            SourceTextDecodeResult bomResult = DecodeBom(bytes);
            if (bomResult != null)
            {
                return bomResult;
            }

            DecodeCandidate utf8 = TryDecode(bytes, 0, bytes.Length, StrictUtf8, "UTF-8", Utf8PreferenceScore);
            DecodeCandidate gb18030 = TryDecode(bytes, 0, bytes.Length, StrictGb18030, "GB18030", 0);
            DecodeCandidate selected = SelectBestCandidate(utf8, gb18030);
            if (selected != null)
            {
                return selected.ToResult();
            }

            return new SourceTextDecodeResult(string.Empty, "Unknown", false, true, int.MinValue);
        }

        /// <summary>XMZADD 20260911 比较可同时解码的 UTF-8 与 GB18030 候选并以中文可读性决定编码。</summary>
        private static DecodeCandidate SelectBestCandidate(DecodeCandidate utf8, DecodeCandidate gb18030)
        {
            if (utf8 == null)
            {
                return gb18030;
            }
            if (gb18030 == null)
            {
                return utf8;
            }
            if (utf8.IsReliable != gb18030.IsReliable)
            {
                return utf8.IsReliable ? utf8 : gb18030;
            }
            if (!utf8.IsReliable)
            {
                // 两种解释都出现乱码信号时保留严格 UTF-8 原文，禁止换码掩盖已经损坏的文本。
                return utf8;
            }
            return gb18030.QualityScore > utf8.QualityScore ? gb18030 : utf8;
        }

        /// <summary>XMZADD 20260911 按字节序标记优先解码带 BOM 源码，避免 BOM 字符干扰类声明匹配。</summary>
        private static SourceTextDecodeResult DecodeBom(byte[] bytes)
        {
            if (StartsWith(bytes, new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                return DecodeBomPayload(bytes, 3, StrictUtf8, "UTF-8 BOM", Utf8PreferenceScore);
            }
            if (StartsWith(bytes, new byte[] { 0xFF, 0xFE }))
            {
                return DecodeBomPayload(bytes, 2, StrictUtf16LittleEndian, "UTF-16 LE BOM", 0);
            }
            if (StartsWith(bytes, new byte[] { 0xFE, 0xFF }))
            {
                return DecodeBomPayload(bytes, 2, StrictUtf16BigEndian, "UTF-16 BE BOM", 0);
            }
            return null;
        }

        /// <summary>XMZADD 20260911 解码 BOM 后正文并在正文损坏时保留不可用结论而不尝试错误代码页。</summary>
        private static SourceTextDecodeResult DecodeBomPayload(byte[] bytes, int offset, Encoding encoding,
            string encodingName, int preferenceScore)
        {
            DecodeCandidate candidate = TryDecode(bytes, offset, bytes.Length - offset,
                encoding, encodingName, preferenceScore);
            return candidate == null
                ? new SourceTextDecodeResult(string.Empty, encodingName, false, true, int.MinValue)
                : candidate.ToResult();
        }

        /// <summary>XMZADD 20260911 使用异常回退执行一次严格解码，使坏字节不会被替换字符静默吞掉。</summary>
        private static DecodeCandidate TryDecode(byte[] bytes, int offset, int count, Encoding encoding,
            string encodingName, int preferenceScore)
        {
            try
            {
                string text = encoding.GetString(bytes, offset, count);
                return Evaluate(text, encodingName, preferenceScore);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>XMZADD 20260911 评价中文有效字符、控制字符和典型乱码信号，形成可审计的可靠性结论。</summary>
        private static DecodeCandidate Evaluate(string text, string encodingName, int preferenceScore)
        {
            long nonAsciiScore = 0L;
            int nonAsciiCount = 0;
            bool hasInvalidControl = false;
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (character >= '\u4E00' && character <= '\u9FFF')
                {
                    nonAsciiScore += 200L;
                    nonAsciiCount++;
                }
                else if (character >= ' ' && character <= '~')
                {
                    continue;
                }
                else if (char.IsControl(character) && character != '\r' && character != '\n' && character != '\t')
                {
                    hasInvalidControl = true;
                    nonAsciiScore -= 2000L;
                    nonAsciiCount++;
                }
                else if (IsPrivateUseCharacter(character))
                {
                    nonAsciiScore -= 1000L;
                    nonAsciiCount++;
                }
                else if (IsCyrillicCharacter(character))
                {
                    // GB18030 中文字节偶尔会严格解码成西里尔字符，应降低该候选而不误伤常见西文注释。
                    nonAsciiScore -= 200L;
                    nonAsciiCount++;
                }
                else if (char.IsLetterOrDigit(character))
                {
                    nonAsciiScore += 100L;
                    nonAsciiCount++;
                }
                else if (!char.IsWhiteSpace(character))
                {
                    nonAsciiScore += 20L;
                    nonAsciiCount++;
                }
            }

            bool hasMojibake = ContainsTypicalMojibake(text);
            int score = preferenceScore;
            if (nonAsciiCount > 0)
            {
                score += (int)(nonAsciiScore / nonAsciiCount);
            }
            if (hasMojibake)
            {
                score -= 4000;
            }
            return new DecodeCandidate(text, encodingName, !hasMojibake && !hasInvalidControl,
                hasMojibake, score);
        }

        /// <summary>XMZADD 20260911 识别替换字符、私用区字符和可逆二次转码片段且不误伤单个合法汉字。</summary>
        private static bool ContainsTypicalMojibake(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf('\uFFFD') >= 0 || text.IndexOf("锟斤拷", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            for (int index = 0; index < text.Length; index++)
            {
                if (IsPrivateUseCharacter(text[index]))
                {
                    return true;
                }
            }
            for (int fragmentIndex = 0; fragmentIndex < TypicalMojibakeFragments.Length; fragmentIndex++)
            {
                if (text.IndexOf(TypicalMojibakeFragments[fragmentIndex], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return ContainsRepairableMojibakeRun(text);
        }

        /// <summary>XMZADD 20260911 检测能按 GB18030 字节还原为更短中文的连续异常 Unicode 片段。</summary>
        private static bool ContainsRepairableMojibakeRun(string text)
        {
            int index = 0;
            while (index < text.Length)
            {
                if (!IsMojibakeSequenceCharacter(text[index]))
                {
                    index++;
                    continue;
                }
                int start = index;
                while (index < text.Length && IsMojibakeSequenceCharacter(text[index]))
                {
                    index++;
                }
                int length = index - start;
                if (ContainsRepairableWindow(text.Substring(start, length)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260911 在异常连续段中容忍最多两个边缘丢失字符，识别带问号的历史错码。</summary>
        private static bool ContainsRepairableWindow(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 3)
            {
                return false;
            }
            int maximumTrim = Math.Min(2, value.Length - 3);
            for (int trimStart = 0; trimStart <= maximumTrim; trimStart++)
            {
                for (int trimEnd = 0; trimEnd <= maximumTrim; trimEnd++)
                {
                    int length = value.Length - trimStart - trimEnd;
                    if (length >= 3 && CanRepairAsUtf8(value.Substring(trimStart, length)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260911 验证异常汉字片段是否可由 GB18030 字节严格还原为更短中文。</summary>
        private static bool CanRepairAsUtf8(string value)
        {
            try
            {
                byte[] bytes = StrictGb18030.GetBytes(value);
                string repaired = StrictUtf8.GetString(bytes);
                if (repaired.Length >= value.Length)
                {
                    return false;
                }
                for (int index = 0; index < repaired.Length; index++)
                {
                    if (repaired[index] >= '\u4E00' && repaired[index] <= '\u9FFF')
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>XMZADD 20260911 判断字符是否属于错码转换常见的连续 Unicode 区段。</summary>
        private static bool IsMojibakeSequenceCharacter(char character)
        {
            return (character >= '\u4E00' && character <= '\u9FFF') ||
                   (character >= '\u3000' && character <= '\u33FF') ||
                   (character >= '\uFF00' && character <= '\uFFEF') ||
                   IsPrivateUseCharacter(character) || IsCyrillicCharacter(character);
        }

        /// <summary>XMZADD 20260911 识别历史错误代码页转换常产生的 Unicode 私用区字符。</summary>
        private static bool IsPrivateUseCharacter(char character)
        {
            return character >= '\uE000' && character <= '\uF8FF';
        }

        /// <summary>XMZADD 20260911 识别 GB18030 双字节可能误解成的西里尔字母范围。</summary>
        private static bool IsCyrillicCharacter(char character)
        {
            return character >= '\u0400' && character <= '\u052F';
        }

        /// <summary>XMZADD 20260911 比较固定 BOM 前缀，避免依赖运行环境的编码自动检测。</summary>
        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            if (bytes.Length < prefix.Length)
            {
                return false;
            }
            for (int index = 0; index < prefix.Length; index++)
            {
                if (bytes[index] != prefix[index])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260911 暂存单一编码候选，避免在解码过程中泄漏可变状态。</summary>
        private sealed class DecodeCandidate
        {
            /// <summary>XMZADD 20260911 创建单一编码候选供统一可靠性评价。</summary>
            public DecodeCandidate(string text, string encodingName, bool isReliable,
                bool hasMojibake, int qualityScore)
            {
                Text = text;
                EncodingName = encodingName;
                IsReliable = isReliable;
                HasMojibake = hasMojibake;
                QualityScore = qualityScore;
            }

            public string Text { get; private set; }
            public string EncodingName { get; private set; }
            public bool IsReliable { get; private set; }
            public bool HasMojibake { get; private set; }
            public int QualityScore { get; private set; }

            /// <summary>XMZADD 20260911 将内部候选转换为对分析器只读的解码结论。</summary>
            public SourceTextDecodeResult ToResult()
            {
                return new SourceTextDecodeResult(Text, EncodingName, IsReliable, HasMojibake, QualityScore);
            }
        }
    }
}
