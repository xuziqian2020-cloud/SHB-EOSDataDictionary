using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证 GitHub 客户端的匿名读取、最小鉴权、协议校验和安全错误边界。</summary>
    [TestClass]
    public sealed class GitHubDictionaryClientTests
    {
        /// <summary>XMZADD 20260901 验证公开 manifest 下载不携带个人 Token 并带齐 GitHub 官方请求头。</summary>
        [TestMethod]
        public async Task GetManifestAsync_PublicRead_IsAnonymous()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK,
                    "{\"FormatVersion\":1,\"Revision\":7,\"SnapshotSha256\":\"" + new string('a', 64) +
                    "\",\"SnapshotPath\":\"snapshot/latest.json.gz\",\"GeneratedAtUtc\":\"\\/Date(0)\\/\"}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            SnapshotManifest manifest = await client.GetManifestAsync(CancellationToken.None);

            Assert.AreEqual(7L, manifest.Revision);
            Assert.IsNull(handler.Requests[0].Authorization);
            Assert.AreEqual("SHB-EOSDataDictionary", handler.Requests[0].UserAgentProduct);
            Assert.AreEqual("application/vnd.github+json", handler.Requests[0].Accept);
            Assert.AreEqual("2026-03-10", handler.Requests[0].ApiVersion);
        }

        /// <summary>XMZADD 20260901 验证用户身份读取只在 Authorization 头使用 Token 并采用不可变数值 ID。</summary>
        [TestMethod]
        public async Task GetCurrentUserAsync_UsesBearerHeaderAndNumericIdentity()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{\"id\":123456,\"login\":\"xmz\",\"name\":\"测试用户\"}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubUserIdentity identity = await client.GetCurrentUserAsync(CancellationToken.None);

            Assert.AreEqual("123456", identity.GitHubUserId);
            Assert.AreEqual("xmz", identity.Login);
            Assert.AreEqual("Bearer secret-token", handler.Requests[0].Authorization);
            Assert.IsFalse(handler.Requests[0].Uri.Contains("secret-token"));
            Assert.IsFalse((handler.Requests[0].Body ?? string.Empty).Contains("secret-token"));
        }

        /// <summary>XMZADD 20260901 验证候选 Token 身份检查只在官方用户请求头使用候选值，不读取或泄露已保存 Token。</summary>
        [TestMethod]
        public async Task GetCurrentUserAsync_CandidateToken_UsesCandidateWithoutStoredToken()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{\"id\":654321,\"login\":\"candidate-user\",\"name\":\"候选用户\"}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "stored-token");
            System.Reflection.MethodInfo candidateMethod = typeof(GitHubDictionaryClient).GetMethod(
                "GetCurrentUserAsync",
                new[] { typeof(string), typeof(CancellationToken) });
            Assert.IsNotNull(candidateMethod);

            var task = candidateMethod.Invoke(client, new object[] { "candidate-token", CancellationToken.None }) as Task<GitHubUserIdentity>;
            Assert.IsNotNull(task);
            GitHubUserIdentity identity = await task;

            Assert.AreEqual("candidate-user", identity.Login);
            Assert.AreEqual("Bearer candidate-token", handler.Requests[0].Authorization);
            Assert.IsFalse(handler.Requests[0].Uri.Contains("candidate-token"));
            Assert.IsFalse(handler.Requests[0].Uri.Contains("stored-token"));
            Assert.IsFalse((handler.Requests[0].Body ?? string.Empty).Contains("candidate-token"));
            Assert.IsFalse((handler.Requests[0].Body ?? string.Empty).Contains("stored-token"));
        }

        /// <summary>XMZADD 20260901 验证结构发布者名单仅从公开仓库固定路径匿名读取并只接受不可变数字用户 ID。</summary>
        [TestMethod]
        public async Task GetPublisherIdsAsync_PublicRead_IsAnonymousAndStrict()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{\"githubUserIds\":[123456,789012]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            IList<string> publisherIds = await client.GetPublisherIdsAsync(CancellationToken.None);

            Assert.AreEqual(2, publisherIds.Count);
            Assert.AreEqual("123456", publisherIds[0]);
            Assert.AreEqual("789012", publisherIds[1]);
            Assert.IsNull(handler.Requests[0].Authorization);
            StringAssert.EndsWith(handler.Requests[0].Uri, "/config/publishers.json");
        }

        /// <summary>XMZADD 20260901 验证发布者名单拒绝未知字段、重复 ID、零值和非数字形态，防止授权配置被宽松解释。</summary>
        [DataTestMethod]
        [DataRow("{\"githubUserIds\":[123],\"token\":\"x\"}")]
        [DataRow("{\"githubUserIds\":[123,123]}")]
        [DataRow("{\"githubUserIds\":[0]}")]
        [DataRow("{\"githubUserIds\":[\"123\"]}")]
        public async Task GetPublisherIdsAsync_InvalidConfiguration_IsRejected(string json)
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, json);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetPublisherIdsAsync(CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
        }

        /// <summary>XMZADD 20260902 验证首次完整快照只创建一个 commit，并在同一 tree 中原子切换快照与清单。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_CreatesSingleCommitWithSnapshotAndManifest()
        {
            string headSha = new string('a', 40);
            string baseTreeSha = new string('b', 40);
            string snapshotBlobSha = new string('c', 40);
            string manifestBlobSha = new string('d', 40);
            string newTreeSha = new string('e', 40);
            string newCommitSha = new string('f', 40);
            int blobCount = 0;
            int tokenReadCount = 0;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/user", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"id\":123,\"login\":\"publisher\"}");
                }
                if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + headSha + "\"}}");
                }
                if (path.EndsWith("/contents/snapshot/manifest.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, CreateRevisionZeroContentsResponse());
                }
                if (path.EndsWith("/git/commits/" + headSha, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"sha\":\"" + headSha + "\",\"tree\":{\"sha\":\"" + baseTreeSha + "\"}}");
                }
                if (path.EndsWith("/git/blobs", StringComparison.Ordinal))
                {
                    blobCount++;
                    return JsonResponse(HttpStatusCode.Created,
                        blobCount == 1 ? "{\"sha\":\"" + snapshotBlobSha + "\"}" : "{\"sha\":\"" + manifestBlobSha + "\"}");
                }
                if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + newTreeSha + "\"}");
                }
                if (path.EndsWith("/git/commits", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + newCommitSha + "\"}");
                }
                return JsonResponse(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + newCommitSha + "\"}}");
            });
            GitHubDictionaryClient client = CreateClient(handler, delegate
            {
                tokenReadCount++;
                return tokenReadCount == 1 ? "secret-token" : "changed-token";
            });
            SnapshotManifest manifest;
            byte[] snapshotContent = CreateFullSnapshotContent(out manifest);

            string commitSha = await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                snapshotContent, manifest, headSha, "123", CancellationToken.None);

            Assert.AreEqual(newCommitSha, commitSha);
            Assert.AreEqual(1, tokenReadCount);
            Assert.AreEqual(9, handler.Requests.Count);
            Assert.AreEqual(2, blobCount);
            Assert.AreEqual("?ref=" + headSha, handler.Requests[2].Query);
            Assert.IsTrue(handler.Requests[4].ContentLength.HasValue);
            long snapshotBlobPreReadLength = handler.Requests[4].ContentLength.Value;
            RecordedBlobRequest snapshotBlobRequest = DeserializeBlobRequest(handler.Requests[4].Body);
            Assert.AreEqual("base64", snapshotBlobRequest.Encoding);
            CollectionAssert.AreEqual(snapshotContent, Convert.FromBase64String(snapshotBlobRequest.Content));
            Assert.AreEqual(
                snapshotBlobPreReadLength,
                (long)Encoding.UTF8.GetByteCount(handler.Requests[4].Body));
            StringAssert.Contains(handler.Requests[6].Body, "\"base_tree\":\"" + baseTreeSha + "\"");
            StringAssert.Contains(handler.Requests[6].Body, manifest.SnapshotPath.Replace("/", "\\/"));
            StringAssert.Contains(handler.Requests[6].Body, "snapshot\\/manifest.json");
            StringAssert.Contains(handler.Requests[6].Body, "\"sha\":\"" + snapshotBlobSha + "\"");
            StringAssert.Contains(handler.Requests[6].Body, "\"sha\":\"" + manifestBlobSha + "\"");
            StringAssert.Contains(handler.Requests[7].Body, "\"tree\":\"" + newTreeSha + "\"");
            StringAssert.Contains(handler.Requests[7].Body, "\"parents\":[\"" + headSha + "\"]");
            Assert.AreEqual("PATCH", handler.Requests[8].Method);
            StringAssert.Contains(handler.Requests[8].Body, "\"sha\":\"" + newCommitSha + "\"");
            StringAssert.Contains(handler.Requests[8].Body, "\"force\":false");
            RecordedBlobRequest manifestBlobRequest = DeserializeBlobRequest(handler.Requests[5].Body);
            string publishedManifest = Encoding.UTF8.GetString(Convert.FromBase64String(manifestBlobRequest.Content));
            StringAssert.Contains(publishedManifest, "\"Revision\":1");
            StringAssert.Contains(publishedManifest, manifest.SnapshotSha256);
            StringAssert.Contains(publishedManifest, manifest.SnapshotPath.Replace("/", "\\/"));
            for (int index = 0; index < handler.Requests.Count; index++)
            {
                Assert.AreEqual("Bearer secret-token", handler.Requests[index].Authorization);
                Assert.IsFalse((handler.Requests[index].Body ?? string.Empty).Contains("secret-token"));
            }
        }

        /// <summary>XMZADD 20260902 验证完整提交最后推进分支失败时拒绝强制覆盖并给出重新扫描提示。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_RefChanged_DoesNotForceOverwrite()
        {
            string headSha = new string('a', 40);
            string baseTreeSha = new string('b', 40);
            string snapshotBlobSha = new string('c', 40);
            string manifestBlobSha = new string('d', 40);
            string newTreeSha = new string('e', 40);
            string newCommitSha = new string('f', 40);
            int blobCount = 0;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/user", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"id\":123,\"login\":\"publisher\"}");
                }
                if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + headSha + "\"}}");
                }
                if (path.EndsWith("/contents/snapshot/manifest.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, CreateRevisionZeroContentsResponse());
                }
                if (path.EndsWith("/git/commits/" + headSha, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"sha\":\"" + headSha + "\",\"tree\":{\"sha\":\"" + baseTreeSha + "\"}}");
                }
                if (path.EndsWith("/git/blobs", StringComparison.Ordinal))
                {
                    blobCount++;
                    return JsonResponse(HttpStatusCode.Created,
                        blobCount == 1 ? "{\"sha\":\"" + snapshotBlobSha + "\"}" : "{\"sha\":\"" + manifestBlobSha + "\"}");
                }
                if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + newTreeSha + "\"}");
                }
                if (path.EndsWith("/git/commits", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + newCommitSha + "\"}");
                }
                return JsonResponse((HttpStatusCode)422, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            SnapshotManifest manifest;
            byte[] snapshotContent = CreateFullSnapshotContent(out manifest);

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                        snapshotContent, manifest, headSha, "123", CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            StringAssert.Contains(exception.Message, "重新扫描");
            Assert.AreEqual("PATCH", handler.Requests[8].Method);
            StringAssert.Contains(handler.Requests[8].Body, "\"force\":false");
        }

        /// <summary>XMZADD 20260902 验证客户端观察分支头后必须校验该提交上的清单仍为 Revision 0，禁止覆盖已先发布的完整快照。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_HeadAlreadyContainsRevisionOne_DoesNotCreateGitObjects()
        {
            string headSha = new string('a', 40);
            string baseTreeSha = new string('b', 40);
            string newCommitSha = new string('f', 40);
            string revisionOneManifest = "{\"FormatVersion\":1,\"Revision\":1," +
                "\"SnapshotSha256\":\"" + new string('1', 64) + "\"," +
                "\"SnapshotPath\":\"snapshot/revisions/000000001-existing.json.gz\"," +
                "\"GeneratedAtUtc\":\"/Date(1)/\",\"LastEventPath\":\"\"}";
            string encodedManifest = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(revisionOneManifest));
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/user", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"id\":123,\"login\":\"publisher\"}");
                }
                if (path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + headSha + "\"}}");
                }
                if (path.EndsWith("/contents/snapshot/manifest.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"encoding\":\"base64\",\"content\":\"" + encodedManifest + "\"}");
                }
                if (path.EndsWith("/git/commits/" + headSha, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"sha\":\"" + headSha + "\",\"tree\":{\"sha\":\"" + baseTreeSha + "\"}}");
                }
                if (request.Method == HttpMethod.Post)
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + newCommitSha + "\"}");
                }
                return JsonResponse(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + newCommitSha + "\"}}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            SnapshotManifest manifest;
            byte[] snapshotContent = CreateFullSnapshotContent(out manifest);

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                        snapshotContent, manifest, headSha, "123", CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            StringAssert.Contains(exception.Message, "重新扫描");
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual("GET", handler.Requests[1].Method);
            Assert.AreEqual("GET", handler.Requests[2].Method);
            Assert.AreEqual("?ref=" + headSha, handler.Requests[2].Query);
        }

        /// <summary>XMZADD 20260902 验证完整快照哈希或压缩内容损坏时在任何 GitHub 请求前失败。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_CorruptedSnapshot_DoesNotSendAnyRequest()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            SnapshotManifest manifest;
            byte[] snapshotContent = CreateFullSnapshotContent(out manifest);
            snapshotContent[0] = (byte)(snapshotContent[0] ^ 0xFF);

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                        snapshotContent,
                        manifest,
                        new string('a', 40),
                        "123",
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260902 验证完整快照恰好达到统一压缩上限时可继续后续内容校验，且测试不分配百兆数组。</summary>
        [TestMethod]
        public void ValidateFullSnapshotLength_AtCodecMaximum_IsAccepted()
        {
            System.Reflection.MethodInfo method = typeof(GitHubDictionaryClient).GetMethod(
                "ValidateFullSnapshotLength",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { SnapshotCodec.MaximumCompressedBytes });
        }

        /// <summary>XMZADD 20260902 验证完整快照超过统一压缩上限一个字节时返回独立固定容量提示，且测试不分配百兆数组。</summary>
        [TestMethod]
        public void ValidateFullSnapshotLength_AboveCodecMaximum_UsesFixedCapacityMessage()
        {
            System.Reflection.MethodInfo method = typeof(GitHubDictionaryClient).GetMethod(
                "ValidateFullSnapshotLength",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);

            System.Reflection.TargetInvocationException invocationException =
                Assert.ThrowsException<System.Reflection.TargetInvocationException>(
                    delegate
                    {
                        method.Invoke(null, new object[] { SnapshotCodec.MaximumCompressedBytes + 1 });
                    });
            GitHubDictionaryClientException exception = invocationException.InnerException as GitHubDictionaryClientException;

            Assert.IsNotNull(exception);
            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual("完整快照超过 GitHub 单文件 100 MiB 限制。", exception.Message);
        }

        /// <summary>XMZADD 20260902 验证零长度完整快照会在读取 Token 或访问 GitHub 前被拒绝。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_EmptySnapshot_DoesNotReadTokenOrSendAnyRequest()
        {
            int tokenReadCount = 0;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, delegate
            {
                tokenReadCount++;
                return "secret-token";
            });
            SnapshotManifest manifest;
            CreateFullSnapshotContent(out manifest);

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                        new byte[0],
                        manifest,
                        new string('a', 40),
                        "123",
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(0, tokenReadCount);
            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260902 验证流式快照 Blob 对模三余数和内部块边界均生成可解析且可还原的固定 JSON 契约。</summary>
        [DataTestMethod]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(12287)]
        [DataRow(12288)]
        [DataRow(12289)]
        public async Task SnapshotBlobContent_VariedSourceLengths_RoundTripsThroughJson(int sourceLength)
        {
            var source = new byte[sourceLength];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = (byte)(index % 251);
            }

            using (HttpContent content = CreateSnapshotBlobContentForTest(source, CancellationToken.None))
            {
                Assert.IsTrue(content.Headers.ContentLength.HasValue);
                long preReadLength = content.Headers.ContentLength.Value;
                byte[] requestBytes = await content.ReadAsByteArrayAsync();
                string requestJson = Encoding.UTF8.GetString(requestBytes);
                RecordedBlobRequest request = DeserializeBlobRequest(requestJson);

                Assert.AreEqual("application/json", content.Headers.ContentType.MediaType);
                Assert.AreEqual(preReadLength, (long)requestBytes.Length);
                Assert.AreEqual("base64", request.Encoding);
                CollectionAssert.AreEqual(source, Convert.FromBase64String(request.Content));
            }
        }

        /// <summary>XMZADD 20260902 验证流式快照 Blob 捕获的预取消会中止序列化，且请求不会进入发送记录。</summary>
        [TestMethod]
        public async Task SnapshotBlobContent_PreCanceled_DoesNotSendRequest()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.Created, "{\"sha\":\"" + new string('a', 40) + "\"}");
            });
            using (var cancellationSource = new CancellationTokenSource())
            using (var httpClient = new HttpClient(handler))
            {
                cancellationSource.Cancel();
                using (HttpContent content = CreateSnapshotBlobContentForTest(
                    new byte[] { 1, 2, 3, 4 },
                    cancellationSource.Token))
                {
                    bool canceled = false;
                    try
                    {
                        await httpClient.PostAsync(
                            "https://api.github.test/repos/acme/dictionary/git/blobs",
                            content,
                            CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }
                    Assert.IsTrue(canceled);
                }
            }

            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260902 验证流式快照写完前缀和首个 Base64 块后取消时停止后续块，且不会伪装成完整正文。</summary>
        [TestMethod]
        public async Task SnapshotBlobContent_CanceledAfterFirstSourceBlock_StopsPartialCopy()
        {
            const int sourceBlockBytes = 12 * 1024;
            var source = new byte[(sourceBlockBytes * 2) + 1];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = (byte)(index % 251);
            }

            using (var cancellationSource = new CancellationTokenSource())
            using (HttpContent content = CreateSnapshotBlobContentForTest(source, cancellationSource.Token))
            using (var target = new CancelAfterSuccessfulWritesStream(cancellationSource, 2))
            {
                Assert.IsTrue(content.Headers.ContentLength.HasValue);
                long preReadLength = content.Headers.ContentLength.Value;
                bool canceled = false;
                try
                {
                    await content.CopyToAsync(target);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.IsTrue(canceled);
                int prefixLength = Encoding.ASCII.GetByteCount("{\"content\":\"");
                long firstBase64BlockLength = 4L * (sourceBlockBytes / 3);
                Assert.IsTrue(target.Length >= prefixLength + firstBase64BlockLength);
                Assert.IsTrue(target.Length < preReadLength);
            }
        }

        /// <summary>XMZADD 20260902 验证直接调用完整发布边界也会拒绝快照内凭据形态且不访问 GitHub。</summary>
        [TestMethod]
        public async Task PublishFullSnapshotAsync_SecretShape_DoesNotSendAnyRequest()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            SnapshotManifest manifest;
            byte[] snapshotContent = CreateFullSnapshotContent(out manifest, "token=do-not-publish");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await ((IGitHubFullSnapshotPublisher)client).PublishFullSnapshotAsync(
                        snapshotContent,
                        manifest,
                        new string('a', 40),
                        "123",
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260901 验证 Issue 正文严格使用协议代码块并由 JSON 序列化器安全转义用户文本。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_SerializesExactFencedBody()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get)
                {
                    return JsonResponse(HttpStatusCode.OK, CreateEmptySearchResponse());
                }
                return JsonResponse(HttpStatusCode.Created, "{\"number\":42}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            DictionaryChangeBatch batch = CreateBatch("中文\"换行\n内容", "operation-1");
            batch.Overrides.Add(new DictionaryOverride
            {
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = "private-override-sentinel"
            });

            int issueNumber = await client.CreateDictionaryIssueAsync(batch, CancellationToken.None);

            Assert.AreEqual(42, issueNumber);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("Bearer secret-token", handler.Requests[1].Authorization);
            StringAssert.Contains(handler.Requests[1].Body, "[EOS-DICTIONARY-EVENT]");
            string expectedBody = "```json-v1\n" + DictionaryJsonSerializer.SerializePublicBatch(batch) + "\n```";
            StringAssert.Contains(handler.Requests[1].Body, EscapeJsonString(expectedBody));
            Assert.IsFalse(handler.Requests[1].Body.Contains("secret-token"));
            Assert.IsFalse(handler.Requests[1].Body.Contains("private-override-sentinel"));
            StringAssert.Contains(handler.Requests[1].Body, "\\\"Overrides\\\":[]");
            Assert.AreEqual(1, batch.Overrides.Count);
        }

        /// <summary>XMZADD 20260903 验证只有主分支事件及其修订文档都登记 OperationId 才确认本机批次生效。</summary>
        [TestMethod]
        public async Task GetDictionaryOperationsApplyStatusAsync_TrustedEventAndRevision_ReturnsApplied()
        {
            string eventPath = "events/2026/09/03/123/operation-1.json";
            string eventJson = "{\"OperationId\":\"operation-1\",\"AuthorGitHubUserId\":\"123\"," +
                "\"ObjectKey\":\"dbo.T_TEST\",\"FieldKey\":\"FNAME\",\"PropertyName\":\"ChineseName\"," +
                "\"ChangeKind\":\"Set\",\"OldValue\":\"旧名称\",\"NewValue\":\"安全内容\"," +
                "\"Revision\":2,\"AppliedAtUtc\":\"\\/Date(1788397323000)\\/\",\"Evidence\":[]}";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/issues/88", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}}");
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/events/revisions/000000002.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"FormatVersion\":1,\"Revision\":2,\"GeneratedAtUtc\":\"\\/Date(1788397323000)\\/\"," +
                        "\"EventPaths\":[\"" + eventPath + "\"]}");
                }
                return JsonResponse(HttpStatusCode.OK, eventJson);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-1");

            DictionaryIssueApplyStatus status = await client.GetDictionaryOperationsApplyStatusAsync(
                batch,
                88,
                2L,
                CancellationToken.None);

            Assert.AreEqual(DictionaryIssueApplyStatus.Applied, status);
            Assert.AreEqual(4, handler.Requests.Count);
            Assert.AreEqual(
                "https://api.github.com/repos/octo-owner/SHB-EOSDataDictionary/issues/88",
                handler.Requests[0].Uri);
            Assert.AreEqual("Bearer secret-token", handler.Requests[0].Authorization);
            Assert.AreEqual(
                "https://raw.githubusercontent.com/octo-owner/SHB-EOSDataDictionary/main/" + eventPath,
                handler.Requests[1].Uri);
            Assert.IsNull(handler.Requests[1].Authorization);
        }

        /// <summary>XMZADD 20260903 验证 Issue 即使存在，只要主分支没有对应事件就仍保持待生效。</summary>
        [TestMethod]
        public async Task GetDictionaryOperationsApplyStatusAsync_MissingTrustedEvent_ReturnsPending()
        {
            const string issueUri = "https://api.github.com/repos/octo-owner/SHB-EOSDataDictionary/issues/88";
            const string eventUri = "https://raw.githubusercontent.com/octo-owner/SHB-EOSDataDictionary/main/" +
                "events/2026/09/03/123/operation-1.json";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, issueUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123},\"labels\":[]}");
                }
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, eventUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
                }
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-1");

            DictionaryIssueApplyStatus status = await client.GetDictionaryOperationsApplyStatusAsync(
                batch,
                88,
                2L,
                CancellationToken.None);

            Assert.AreEqual(DictionaryIssueApplyStatus.Pending, status);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual(issueUri, handler.Requests[0].Uri);
            Assert.AreEqual("Bearer secret-token", handler.Requests[0].Authorization);
            Assert.AreEqual("GET", handler.Requests[1].Method);
            Assert.AreEqual(eventUri, handler.Requests[1].Uri);
            Assert.IsNull(handler.Requests[1].Authorization);
        }

        /// <summary>XMZADD 20260903 验证事件修订尚未进入当前清单时不得提前完成本机批次。</summary>
        [TestMethod]
        public async Task GetDictionaryOperationsApplyStatusAsync_EventBeyondManifest_ReturnsPending()
        {
            const string issueUri = "https://api.github.com/repos/octo-owner/SHB-EOSDataDictionary/issues/88";
            const string eventUri = "https://raw.githubusercontent.com/octo-owner/SHB-EOSDataDictionary/main/" +
                "events/2026/09/03/123/operation-1.json";
            string eventJson = "{\"OperationId\":\"operation-1\",\"AuthorGitHubUserId\":\"123\"," +
                "\"ObjectKey\":\"dbo.T_TEST\",\"FieldKey\":\"FNAME\",\"PropertyName\":\"ChineseName\"," +
                "\"ChangeKind\":\"Set\",\"NewValue\":\"安全内容\",\"Revision\":3," +
                "\"AppliedAtUtc\":\"\\/Date(1788397323000)\\/\",\"Evidence\":[]}";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, issueUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}}");
                }
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, eventUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, eventJson);
                }
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            DictionaryIssueApplyStatus status = await client.GetDictionaryOperationsApplyStatusAsync(
                CreateBatch("安全内容", "operation-1"),
                88,
                2L,
                CancellationToken.None);

            Assert.AreEqual(DictionaryIssueApplyStatus.Pending, status);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual(issueUri, handler.Requests[0].Uri);
            Assert.AreEqual("Bearer secret-token", handler.Requests[0].Authorization);
            Assert.AreEqual("GET", handler.Requests[1].Method);
            Assert.AreEqual(eventUri, handler.Requests[1].Uri);
            Assert.IsNull(handler.Requests[1].Authorization);
        }

        /// <summary>XMZADD 20260903 验证可信事件缺失且远程标记协议无效时返回已拒绝，并坚持先检查不可变事件事实。</summary>
        [TestMethod]
        public async Task GetDictionaryOperationsApplyStatusAsync_MissingEventWithInvalidLabel_ReturnsRejected()
        {
            const string issueUri = "https://api.github.com/repos/octo-owner/SHB-EOSDataDictionary/issues/88";
            const string eventUri = "https://raw.githubusercontent.com/octo-owner/SHB-EOSDataDictionary/main/" +
                "events/2026/09/03/123/operation-1.json";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, issueUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}," +
                        "\"labels\":[{\"name\":\"dictionary-event-invalid\"}]}");
                }
                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri.AbsoluteUri, eventUri, StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
                }
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            DictionaryIssueApplyStatus status = await client.GetDictionaryOperationsApplyStatusAsync(
                CreateBatch("安全内容", "operation-1"),
                88,
                2L,
                CancellationToken.None);

            Assert.AreEqual(DictionaryIssueApplyStatus.Rejected, status);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual(issueUri, handler.Requests[0].Uri);
            Assert.AreEqual("Bearer secret-token", handler.Requests[0].Authorization);
            Assert.AreEqual("GET", handler.Requests[1].Method);
            Assert.AreEqual(eventUri, handler.Requests[1].Uri);
            Assert.IsNull(handler.Requests[1].Authorization);
        }

        /// <summary>XMZADD 20260903 验证严格匹配的旧版私有覆盖 Issue 会被关闭，并以确定性公开批次安全重建。</summary>
        [TestMethod]
        public async Task RecreateRejectedDictionaryIssueAsync_LegacyOverrides_ClosesOldAndCreatesPublicReplacement()
        {
            const string sentinel = "legacy-private-override-sentinel";
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-recovery");
            batch.Operations[0].FieldKey = "FRECOVERY";
            batch.Operations[0].PropertyName = string.Empty;
            batch.Operations[0].NewValue = null;
            batch.Operations[0].ChangeKind = "AddField";
            batch.Operations[0].FieldPayload = new FieldStructurePayload
            {
                FieldName = "FRECOVERY",
                OwnerTableName = "T_TEST",
                DataType = "nvarchar",
                LengthText = "50",
                IsRequired = true,
                IsPrimaryKey = false,
                IsForeignKey = true
            };
            batch.Operations[0].Evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "RecoverySourceType",
                    SourcePath = "Recovery/FieldDefinition.vb",
                    SourceLine = 73,
                    RuleName = "RecoveryFieldRule",
                    RawValue = "RecoveryRawValue",
                    OriginalText = "RecoveryOriginalText",
                    Explanation = "RecoveryEvidenceExplanation"
                }
            };
            batch.Overrides.Add(new DictionaryOverride
            {
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = sentinel
            });
            string legacyTitle = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string legacyBody = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            string openIssueJson = "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}," +
                "\"state\":\"open\",\"title\":\"" + EscapeJsonString(legacyTitle) + "\",\"body\":\"" +
                EscapeJsonString(legacyBody) + "\",\"labels\":[{\"name\":\"dictionary-event-invalid\"}]}";
            string closedIssueJson = "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}," +
                "\"state\":\"closed\",\"title\":\"" + EscapeJsonString(legacyTitle) + "\",\"body\":\"" +
                EscapeJsonString(legacyBody) + "\",\"labels\":[{\"name\":\"dictionary-event-invalid\"}]}";
            int searchCount = 0;
            string replacementTitle = null;
            string replacementBody = null;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/issues/88", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, openIssueJson);
                }
                if (request.Method.Method == "PATCH" && path.EndsWith("/issues/88", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK, closedIssueJson);
                }
                if (request.Method == HttpMethod.Get && path.EndsWith("/search/issues", StringComparison.Ordinal))
                {
                    searchCount++;
                    return JsonResponse(
                        HttpStatusCode.OK,
                        searchCount == 1
                            ? CreateEmptySearchResponse()
                            : CreateSearchResponse(99, replacementTitle, replacementBody));
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/issues", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"number\":99}");
                }
                return JsonResponse(HttpStatusCode.InternalServerError, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            int issueNumber = await client.RecreateRejectedDictionaryIssueAsync(
                batch,
                88,
                CancellationToken.None);

            Assert.AreEqual(99, issueNumber);
            Assert.AreEqual(4, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            StringAssert.EndsWith(handler.Requests[0].Uri, "/issues/88");
            Assert.AreEqual("PATCH", handler.Requests[1].Method);
            StringAssert.EndsWith(handler.Requests[1].Uri, "/issues/88");
            StringAssert.Contains(handler.Requests[1].Body, "\"state\":\"closed\"");
            Assert.AreEqual("GET", handler.Requests[2].Method);
            StringAssert.Contains(handler.Requests[2].Uri, "/search/issues?");
            Assert.AreEqual("POST", handler.Requests[3].Method);
            StringAssert.EndsWith(handler.Requests[3].Uri, "/issues");

            RecordedIssueRequest replacementRequest = DeserializeIssueRequest(handler.Requests[3].Body);
            replacementTitle = replacementRequest.Title;
            replacementBody = replacementRequest.Body;
            Assert.AreNotEqual(legacyTitle, replacementTitle);
            StringAssert.StartsWith(replacementTitle, "[EOS-DICTIONARY-EVENT] ");
            string replacementBatchId = replacementTitle.Substring("[EOS-DICTIONARY-EVENT] ".Length);
            Guid replacementBatchGuid;
            Assert.IsTrue(Guid.TryParseExact(replacementBatchId, "N", out replacementBatchGuid));
            Assert.IsFalse(replacementBody.Contains(sentinel));
            StringAssert.Contains(replacementBody, "\"Overrides\":[]");
            Assert.IsTrue(replacementBody.StartsWith("```json-v1\n", StringComparison.Ordinal));
            Assert.IsTrue(replacementBody.EndsWith("\n```", StringComparison.Ordinal));
            string replacementJson = replacementBody.Substring(
                "```json-v1\n".Length,
                replacementBody.Length - "```json-v1\n".Length - "\n```".Length);
            DictionaryChangeBatch replacementBatch = DictionaryJsonSerializer.DeserializeBatch(replacementJson);
            Assert.AreEqual(replacementBatchId, replacementBatch.BatchId);
            Assert.AreEqual(batch.AuthorGitHubUserId, replacementBatch.AuthorGitHubUserId);
            Assert.AreEqual(batch.CreatedAtUtc, replacementBatch.CreatedAtUtc);
            Assert.AreEqual(batch.Operations.Count, replacementBatch.Operations.Count);
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation expectedOperation = batch.Operations[index];
                DictionaryChangeOperation replacementOperation = replacementBatch.Operations[index];
                Assert.IsNotNull(replacementOperation);
                Assert.AreEqual(expectedOperation.OperationId, replacementOperation.OperationId);
                Assert.AreEqual(expectedOperation.AuthorGitHubUserId, replacementOperation.AuthorGitHubUserId);
                Assert.AreEqual(expectedOperation.ObjectKey, replacementOperation.ObjectKey);
                Assert.AreEqual(expectedOperation.FieldKey, replacementOperation.FieldKey);
                Assert.AreEqual(expectedOperation.PropertyName, replacementOperation.PropertyName);
                Assert.AreEqual(expectedOperation.OldValue, replacementOperation.OldValue);
                Assert.AreEqual(expectedOperation.NewValue, replacementOperation.NewValue);
                Assert.AreEqual(expectedOperation.ChangeKind, replacementOperation.ChangeKind);
                Assert.AreEqual(expectedOperation.CreatedAtUtc, replacementOperation.CreatedAtUtc);
                Assert.IsNull(expectedOperation.TablePayload);
                Assert.IsNull(replacementOperation.TablePayload);
                Assert.IsNotNull(expectedOperation.FieldPayload);
                Assert.IsNotNull(replacementOperation.FieldPayload);
                Assert.AreEqual(expectedOperation.FieldPayload.FieldName, replacementOperation.FieldPayload.FieldName);
                Assert.AreEqual(expectedOperation.FieldPayload.OwnerTableName, replacementOperation.FieldPayload.OwnerTableName);
                Assert.AreEqual(expectedOperation.FieldPayload.DataType, replacementOperation.FieldPayload.DataType);
                Assert.AreEqual(expectedOperation.FieldPayload.LengthText, replacementOperation.FieldPayload.LengthText);
                Assert.AreEqual(expectedOperation.FieldPayload.IsRequired, replacementOperation.FieldPayload.IsRequired);
                Assert.AreEqual(expectedOperation.FieldPayload.IsPrimaryKey, replacementOperation.FieldPayload.IsPrimaryKey);
                Assert.AreEqual(expectedOperation.FieldPayload.IsForeignKey, replacementOperation.FieldPayload.IsForeignKey);
                Assert.IsNull(expectedOperation.RelationPayload);
                Assert.IsNull(replacementOperation.RelationPayload);
                Assert.IsNotNull(expectedOperation.Evidence);
                Assert.IsNotNull(replacementOperation.Evidence);
                Assert.AreEqual(expectedOperation.Evidence.Count, replacementOperation.Evidence.Count);
                for (int evidenceIndex = 0; evidenceIndex < expectedOperation.Evidence.Count; evidenceIndex++)
                {
                    EvidenceItem expectedEvidence = expectedOperation.Evidence[evidenceIndex];
                    EvidenceItem replacementEvidence = replacementOperation.Evidence[evidenceIndex];
                    Assert.IsNotNull(replacementEvidence);
                    Assert.AreEqual(expectedEvidence.SourceType, replacementEvidence.SourceType);
                    Assert.AreEqual(expectedEvidence.SourcePath, replacementEvidence.SourcePath);
                    Assert.AreEqual(expectedEvidence.SourceLine, replacementEvidence.SourceLine);
                    Assert.AreEqual(expectedEvidence.RuleName, replacementEvidence.RuleName);
                    Assert.AreEqual(expectedEvidence.RawValue, replacementEvidence.RawValue);
                    Assert.AreEqual(expectedEvidence.OriginalText, replacementEvidence.OriginalText);
                    Assert.AreEqual(expectedEvidence.Explanation, replacementEvidence.Explanation);
                }
            }
            Assert.AreEqual(0, replacementBatch.Overrides.Count);
            Assert.AreEqual(
                "```json-v1\n" + DictionaryJsonSerializer.SerializePublicBatch(replacementBatch) + "\n```",
                replacementBody);
            Assert.AreEqual(1, batch.Overrides.Count);

            int retryIssueNumber = await client.RecreateRejectedDictionaryIssueAsync(
                batch,
                88,
                CancellationToken.None);

            Assert.AreEqual(99, retryIssueNumber);
            Assert.AreEqual(7, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[4].Method);
            Assert.AreEqual("PATCH", handler.Requests[5].Method);
            Assert.AreEqual("GET", handler.Requests[6].Method);
            int postCount = 0;
            for (int index = 0; index < handler.Requests.Count; index++)
            {
                if (string.Equals(handler.Requests[index].Method, "POST", StringComparison.Ordinal))
                {
                    postCount++;
                }
                Assert.AreEqual("Bearer secret-token", handler.Requests[index].Authorization);
                Assert.IsFalse(handler.Requests[index].Uri.Contains("secret-token"));
                Assert.IsFalse((handler.Requests[index].Body ?? string.Empty).Contains("secret-token"));
                Assert.IsFalse(handler.Requests[index].Uri.Contains(sentinel));
                Assert.IsFalse((handler.Requests[index].Body ?? string.Empty).Contains(sentinel));
            }
            Assert.AreEqual(1, postCount);
        }

        /// <summary>XMZADD 20260903 验证远程旧 Issue 正文与本地完整批次不一致时拒绝关闭或重新发布。</summary>
        [TestMethod]
        public async Task RecreateRejectedDictionaryIssueAsync_LegacyBodyMismatch_RejectsRemoteModification()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-body-mismatch");
            batch.Overrides.Add(new DictionaryOverride
            {
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = "legacy-private-override-sentinel"
            });
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            DictionaryChangeBatch mismatchedBatch = DictionaryJsonSerializer.DeserializeBatch(
                DictionaryJsonSerializer.SerializeBatch(batch));
            mismatchedBatch.Operations[0].NewValue = "已被远程篡改的业务值";
            string mismatchedBody = "```json-v1\n" +
                DictionaryJsonSerializer.SerializeBatch(mismatchedBatch) + "\n```";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK,
                    "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}," +
                    "\"state\":\"open\",\"title\":\"" + EscapeJsonString(title) + "\",\"body\":\"" +
                    EscapeJsonString(mismatchedBody) + "\"," +
                    "\"labels\":[{\"name\":\"dictionary-event-invalid\"}]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.RecreateRejectedDictionaryIssueAsync(batch, 88, CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260903 验证远程旧 Issue 作者数值身份与本地批次不一致时拒绝关闭或重新发布。</summary>
        [TestMethod]
        public async Task RecreateRejectedDictionaryIssueAsync_AuthorMismatch_RejectsRemoteModification()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-author-mismatch");
            batch.Overrides.Add(new DictionaryOverride
            {
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = "legacy-private-override-sentinel"
            });
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string body = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK,
                    "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":456}," +
                    "\"state\":\"open\",\"title\":\"" + EscapeJsonString(title) + "\",\"body\":\"" +
                    EscapeJsonString(body) + "\",\"labels\":[{\"name\":\"dictionary-event-invalid\"}]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.RecreateRejectedDictionaryIssueAsync(batch, 88, CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260903 验证远程旧 Issue 没有明确无效标签时拒绝关闭或重新发布。</summary>
        [TestMethod]
        public async Task RecreateRejectedDictionaryIssueAsync_MissingInvalidLabel_RejectsRemoteModification()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-missing-invalid-label");
            batch.Overrides.Add(new DictionaryOverride
            {
                ObjectKey = "dbo.T_TEST",
                PropertyName = "ChineseName",
                ManualValue = "legacy-private-override-sentinel"
            });
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string body = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK,
                    "{\"number\":88,\"created_at\":\"2026-09-03T01:02:03Z\",\"user\":{\"id\":123}," +
                    "\"state\":\"open\",\"title\":\"" + EscapeJsonString(title) + "\",\"body\":\"" +
                    EscapeJsonString(body) + "\",\"labels\":[]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.RecreateRejectedDictionaryIssueAsync(batch, 88, CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260901 验证最终 Markdown 正文超过 60KiB 时在任何 GitHub 查询或创建请求前拒绝。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_OversizedFinalBody_IsRejectedBeforeNetwork()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.Created, "{\"number\":42}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            var batch = new DictionaryChangeBatch
            {
                BatchId = "batch-large",
                AuthorGitHubUserId = "123",
                CreatedAtUtc = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc)
            };
            for (int index = 0; index < 20; index++)
            {
                batch.Operations.Add(new DictionaryChangeOperation
                {
                    OperationId = "large-operation-" + index.ToString(),
                    AuthorGitHubUserId = "123",
                    ObjectKey = "dbo.T_TEST",
                    FieldKey = "FNAME",
                    PropertyName = "ChineseName",
                    OldValue = new string('旧', 2000),
                    NewValue = new string('新', 2000),
                    ChangeKind = "Set",
                    CreatedAtUtc = batch.CreatedAtUtc
                });
            }

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.CreateDictionaryIssueAsync(batch, CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260901 验证同一 BatchId 和正文已存在时复用远端 Issue，避免重试产生重复事件。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_ExistingExactBatch_ReturnsExistingWithoutPost()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-existing");
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string body = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, CreateSearchResponse(77, title, body));
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            int issueNumber = await client.CreateDictionaryIssueAsync(batch, CancellationToken.None);

            Assert.AreEqual(77, issueNumber);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            StringAssert.Contains(handler.Requests[0].Uri, "/search/issues?");
        }

        /// <summary>XMZADD 20260901 验证 BatchId 已被不同正文占用时拒绝覆盖，防止一个批次号代表两次业务变更。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_ExistingBatchWithDifferentBody_IsRejected()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-collision");
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, CreateSearchResponse(78, title, "```json-v1\n{}\n```"));
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.CreateDictionaryIssueAsync(batch, CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260901 验证查重响应缺少必填成员时停止发布，禁止把协议损坏误判为尚未创建。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_MissingSearchContract_IsRejectedBeforePost()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.CreateDictionaryIssueAsync(
                        CreateBatch("安全内容", "operation-missing-search-contract"),
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260901 验证候选总数与单页集合不一致时停止发布，防止漏查候选后重复创建。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_InconsistentSearchCount_IsRejectedBeforePost()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{\"total_count\":1,\"items\":[]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.CreateDictionaryIssueAsync(
                        CreateBatch("安全内容", "operation-inconsistent-search-count"),
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260901 验证 GitHub 标记搜索结果不完整时停止发布，避免漏查同 BatchId 后重复创建。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_IncompleteSearch_IsRejectedBeforePost()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"total_count\":0,\"incomplete_results\":true,\"items\":[]}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate
                {
                    await client.CreateDictionaryIssueAsync(
                        CreateBatch("安全内容", "operation-incomplete-search"),
                        CancellationToken.None);
                });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
        }

        /// <summary>XMZADD 20260901 验证创建请求超时但远端已落单时按 BatchId 对账并返回唯一 Issue。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_PostTimeoutAfterRemoteCreation_ReconcilesExistingIssue()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-timeout");
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string body = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            int requestIndex = 0;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requestIndex++;
                if (requestIndex == 1)
                {
                    return JsonResponse(HttpStatusCode.OK, CreateEmptySearchResponse());
                }
                if (requestIndex == 2)
                {
                    throw new TaskCanceledException("模拟提交结果未知");
                }
                return JsonResponse(HttpStatusCode.OK, CreateIssueListResponse(79, title, body));
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            int issueNumber = await client.CreateDictionaryIssueAsync(batch, CancellationToken.None);

            Assert.AreEqual(79, issueNumber);
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual("POST", handler.Requests[1].Method);
            Assert.AreEqual("GET", handler.Requests[2].Method);
            StringAssert.Contains(handler.Requests[2].Uri, "/issues?state=all");
        }

        /// <summary>XMZADD 20260901 验证提交期间收到取消信号时仍使用独立令牌对账远端结果。</summary>
        [TestMethod]
        public async Task CreateDictionaryIssueAsync_PostCancellationAfterRemoteCreation_ReconcilesExistingIssue()
        {
            DictionaryChangeBatch batch = CreateBatch("安全内容", "operation-cancel");
            string title = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string body = "```json-v1\n" + DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            var cancellationSource = new CancellationTokenSource();
            int requestIndex = 0;
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requestIndex++;
                if (requestIndex == 1)
                {
                    return JsonResponse(HttpStatusCode.OK, CreateEmptySearchResponse());
                }
                if (requestIndex == 2)
                {
                    cancellationSource.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                }
                return JsonResponse(HttpStatusCode.OK, CreateIssueListResponse(80, title, body));
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            int issueNumber = await client.CreateDictionaryIssueAsync(batch, cancellationSource.Token);

            Assert.AreEqual(80, issueNumber);
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual("GET", handler.Requests[0].Method);
            Assert.AreEqual("POST", handler.Requests[1].Method);
            Assert.AreEqual("GET", handler.Requests[2].Method);
            StringAssert.Contains(handler.Requests[2].Uri, "/issues?state=all");
            cancellationSource.Dispose();
        }

        /// <summary>XMZADD 20260901 验证单个修订的多个真实作者事件被合并为一次可信仓库重放并保留操作号与结构载荷。</summary>
        [TestMethod]
        public async Task DownloadRevisionAsync_MergesEventsAndMapsReplayPayloads()
        {
            AppliedDictionaryEvent setEvent = CreateAppliedEvent("operation-set", "22", "Set");
            setEvent.NewValue = "物料\"名称";
            AppliedDictionaryEvent removeRelation = CreateAppliedEvent("operation-relation", "33", "RemoveRelation");
            removeRelation.OldRelationPayload = new RelationStructurePayload
            {
                ForeignKeyName = "FK_A_B",
                ParentSchemaName = "dbo",
                ParentTableName = "A",
                ParentFieldName = "ID",
                ChildSchemaName = "dbo",
                ChildTableName = "B",
                ChildFieldName = "AID"
            };
            byte[] setJson = SerializeJson(setEvent, typeof(AppliedDictionaryEvent));
            byte[] relationJson = SerializeJson(removeRelation, typeof(AppliedDictionaryEvent));
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("events/revisions/000000003.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"FormatVersion\":1,\"Revision\":3,\"GeneratedAtUtc\":\"\\/Date(0)\\/\",\"EventPaths\":[\"events/22/operation-set.json\",\"events/33/operation-relation.json\"]}");
                }
                if (request.RequestUri.AbsolutePath.EndsWith("events/22/operation-set.json", StringComparison.Ordinal))
                {
                    return ByteResponse(HttpStatusCode.OK, setJson);
                }
                return ByteResponse(HttpStatusCode.OK, relationJson);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            DictionaryRevisionPackage package = await client.DownloadRevisionAsync(3L, CancellationToken.None);

            Assert.AreEqual(1, package.Batches.Count);
            Assert.AreEqual(2, package.Batches[0].Operations.Count);
            Assert.AreEqual(string.Empty, package.Batches[0].AuthorGitHubUserId);
            Assert.AreEqual("operation-set", package.Batches[0].Operations[0].OperationId);
            Assert.AreEqual("22", package.Batches[0].Operations[0].AuthorGitHubUserId);
            Assert.AreEqual("物料\"名称", package.Batches[0].Operations[0].NewValue);
            Assert.AreEqual("operation-relation", package.Batches[0].Operations[1].OperationId);
            Assert.AreEqual("33", package.Batches[0].Operations[1].AuthorGitHubUserId);
            Assert.AreEqual("FK_A_B", package.Batches[0].Operations[1].RelationPayload.ForeignKeyName);
            for (int index = 0; index < handler.Requests.Count; index++)
            {
                Assert.IsNull(handler.Requests[index].Authorization);
            }
        }

        /// <summary>XMZADD 20260901 验证认证失败、限流和缺失资源被分类且异常不回显服务端正文或 Token。</summary>
        [DataTestMethod]
        [DataRow(HttpStatusCode.Unauthorized, GitHubDictionaryErrorKind.Authentication, false)]
        [DataRow(HttpStatusCode.Forbidden, GitHubDictionaryErrorKind.RateLimited, true)]
        [DataRow((HttpStatusCode)429, GitHubDictionaryErrorKind.RateLimited, true)]
        [DataRow(HttpStatusCode.NotFound, GitHubDictionaryErrorKind.NotFound, false)]
        [DataRow(HttpStatusCode.RequestTimeout, GitHubDictionaryErrorKind.Transient, true)]
        [DataRow(HttpStatusCode.InternalServerError, GitHubDictionaryErrorKind.Transient, true)]
        public async Task GetCurrentUserAsync_HttpFailure_ReturnsSafeClassification(
            HttpStatusCode statusCode,
            GitHubDictionaryErrorKind expectedKind,
            bool expectedTransient)
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = JsonResponse(statusCode, "{\"message\":\"secret-token server details\"}");
                if (statusCode == HttpStatusCode.Forbidden || (int)statusCode == 429)
                {
                    response.Headers.TryAddWithoutValidation("Retry-After", "60");
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                }
                return response;
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetCurrentUserAsync(CancellationToken.None); });

            Assert.AreEqual(expectedKind, exception.Kind);
            Assert.AreEqual(expectedTransient, exception.IsTransient);
            Assert.IsFalse(exception.ToString().Contains("secret-token"));
        }

        /// <summary>XMZADD 20260901 验证调用方取消直接传播而不包装为网络失败。</summary>
        [TestMethod]
        public async Task GetManifestAsync_CallerCancelled_PropagatesCancellation()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return JsonResponse(HttpStatusCode.OK, "{}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");
            var source = new CancellationTokenSource();
            source.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                async delegate { await client.GetManifestAsync(source.Token); });
        }

        /// <summary>XMZADD 20260901 验证非调用方取消产生的超时被归类为可重试网络错误。</summary>
        [TestMethod]
        public async Task GetManifestAsync_Timeout_IsTransientWithoutSensitiveDetails()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new TaskCanceledException("secret-token timeout details");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetManifestAsync(CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Transient, exception.Kind);
            Assert.IsTrue(exception.IsTransient);
            Assert.IsFalse(exception.ToString().Contains("secret-token"));
        }

        /// <summary>XMZADD 20260901 验证凭据提供器自身的异常也被安全包装，避免其消息携带 Token。</summary>
        [TestMethod]
        public async Task GetCurrentUserAsync_TokenProviderFailure_DoesNotLeakProviderMessage()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{}");
            });
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner"
            };
            var client = new GitHubDictionaryClient(
                new HttpClient(handler),
                options,
                delegate { throw new InvalidOperationException("secret-token provider details"); });

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetCurrentUserAsync(CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Authentication, exception.Kind);
            Assert.IsFalse(exception.ToString().Contains("secret-token"));
            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260901 验证注入客户端的默认 Authorization 不得污染匿名公开仓库请求。</summary>
        [TestMethod]
        public void Constructor_DefaultAuthorization_IsRejectedBeforeAnonymousRequest()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK, "{}");
            });
            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner"
            };
            GitHubDictionaryClientException exception = Assert.ThrowsException<GitHubDictionaryClientException>(
                delegate { new GitHubDictionaryClient(httpClient, options, delegate { return "another-token"; }); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Configuration, exception.Kind);
            Assert.AreEqual(0, handler.Requests.Count);
            Assert.IsFalse(exception.ToString().Contains("secret-token"));
        }

        /// <summary>XMZADD 20260901 验证结构新增事件缺少服务端确认的新载荷时在客户端下载阶段即被拒绝。</summary>
        [TestMethod]
        public async Task DownloadRevisionAsync_AddTableWithoutPayload_IsRejected()
        {
            AppliedDictionaryEvent invalidEvent = CreateAppliedEvent("operation-table", "22", "AddTable");
            byte[] eventJson = SerializeJson(invalidEvent, typeof(AppliedDictionaryEvent));
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("events/revisions/000000003.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"FormatVersion\":1,\"Revision\":3,\"GeneratedAtUtc\":\"\\/Date(0)\\/\",\"EventPaths\":[\"events/22/operation-table.json\"]}");
                }
                return ByteResponse(HttpStatusCode.OK, eventJson);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.DownloadRevisionAsync(3L, CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
        }

        /// <summary>XMZADD 20260901 验证远程事件作者必须是非零 ASCII 数字 GitHub 用户 ID。</summary>
        [TestMethod]
        public async Task DownloadRevisionAsync_NonNumericAuthor_IsRejected()
        {
            AppliedDictionaryEvent invalidEvent = CreateAppliedEvent("operation-author", "not-numeric", "Set");
            invalidEvent.NewValue = "名称";
            byte[] eventJson = SerializeJson(invalidEvent, typeof(AppliedDictionaryEvent));
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("events/revisions/000000003.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"FormatVersion\":1,\"Revision\":3,\"GeneratedAtUtc\":\"\\/Date(0)\\/\",\"EventPaths\":[\"events/22/operation-author.json\"]}");
                }
                return ByteResponse(HttpStatusCode.OK, eventJson);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.DownloadRevisionAsync(3L, CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
        }

        /// <summary>XMZADD 20260901 验证单个修订累计事件内容超过安全上限时终止下载，防止多小文件组合耗尽内存。</summary>
        [TestMethod]
        public async Task DownloadRevisionAsync_CumulativeEventBytesExceeded_IsRejected()
        {
            AppliedDictionaryEvent largeEvent = CreateAppliedEvent("large-operation", "22", "Set");
            largeEvent.NewValue = new string('x', 1900000);
            byte[] eventJson = SerializeJson(largeEvent, typeof(AppliedDictionaryEvent));
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("events/revisions/000000003.json", StringComparison.Ordinal))
                {
                    return JsonResponse(HttpStatusCode.OK,
                        "{\"FormatVersion\":1,\"Revision\":3,\"GeneratedAtUtc\":\"\\/Date(0)\\/\",\"EventPaths\":[" +
                        "\"events/22/large-1.json\",\"events/22/large-2.json\",\"events/22/large-3.json\"," +
                        "\"events/22/large-4.json\",\"events/22/large-5.json\",\"events/22/large-6.json\"]}");
                }
                return ByteResponse(HttpStatusCode.OK, eventJson);
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.DownloadRevisionAsync(3L, CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
            Assert.IsTrue(handler.Requests.Count < 7);
        }

        /// <summary>XMZADD 20260901 验证所有远程相对路径拒绝父目录、绝对路径、反斜线和查询注入。</summary>
        [DataTestMethod]
        [DataRow("../snapshot.json.gz")]
        [DataRow("/snapshot/latest.json.gz")]
        [DataRow("snapshot\\latest.json.gz")]
        [DataRow("snapshot/latest.json.gz?token=secret")]
        [DataRow("https://evil.example/file")]
        public async Task DownloadSnapshotAsync_UnsafeRelativePath_IsRejected(string relativePath)
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return ByteResponse(HttpStatusCode.OK, new byte[] { 1 });
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async delegate { await client.DownloadSnapshotAsync(relativePath, CancellationToken.None); });

            Assert.AreEqual(0, handler.Requests.Count);
        }

        /// <summary>XMZADD 20260901 验证仓库参数拒绝路径分隔符和控制字符并保留固定仓库默认值。</summary>
        [TestMethod]
        public void RepositoryOptions_ValidatesRepositoryCoordinates()
        {
            var options = new DictionaryRepositoryOptions();
            Assert.AreEqual("SHB-EOSDataDictionary", options.RepositoryName);
            Assert.AreEqual("main", options.Branch);
            options.Owner = "owner/name";

            Assert.ThrowsException<ArgumentException>(delegate { options.Validate(); });
        }

        /// <summary>XMZADD 20260901 验证 GitHub 服务地址只能使用官方 HTTPS 主机和默认端口。</summary>
        [DataTestMethod]
        [DataRow("https://evil.example/")]
        [DataRow("https://api.github.com:444/")]
        [DataRow("https://user@api.github.com/")]
        [DataRow("https://api.github.com/?token=x")]
        [DataRow("https://api.github.com/#fragment")]
        public void RepositoryOptions_ApiBaseUriRejectsNonOfficialOrInjectedAddress(string address)
        {
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner",
                ApiBaseUri = new Uri(address)
            };

            Assert.ThrowsException<ArgumentException>(delegate { options.Validate(); });
        }

        /// <summary>XMZADD 20260901 验证公开原始文件地址不能切换到非官方主机或非默认端口。</summary>
        [DataTestMethod]
        [DataRow("https://evil.example/")]
        [DataRow("https://raw.githubusercontent.com:444/")]
        public void RepositoryOptions_RawBaseUriRejectsNonOfficialAddress(string address)
        {
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner",
                RawBaseUri = new Uri(address)
            };

            Assert.ThrowsException<ArgumentException>(delegate { options.Validate(); });
        }

        /// <summary>XMZADD 20260901 验证客户端构造后外部突变全部仓库参数也不能改变已验证的请求目标。</summary>
        [TestMethod]
        public async Task Constructor_OptionsMutatedAfterValidation_UsesPrivateImmutableSnapshot()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post)
                {
                    return JsonResponse(HttpStatusCode.Created, "{\"number\":42}");
                }
                if (request.RequestUri.AbsolutePath == "/user")
                {
                    return JsonResponse(HttpStatusCode.OK, "{\"id\":123,\"login\":\"xmz\"}");
                }
                if (request.RequestUri.AbsolutePath == "/search/issues")
                {
                    return JsonResponse(HttpStatusCode.OK, CreateEmptySearchResponse());
                }
                return JsonResponse(HttpStatusCode.OK,
                    "{\"FormatVersion\":1,\"Revision\":7,\"SnapshotSha256\":\"" + new string('a', 64) +
                    "\",\"SnapshotPath\":\"snapshot/latest.json.gz\",\"GeneratedAtUtc\":\"\\/Date(0)\\/\"}");
            });
            var options = new DictionaryRepositoryOptions { Owner = "octo-owner" };
            var client = new GitHubDictionaryClient(new HttpClient(handler), options, delegate { return "secret-token"; });

            options.Owner = "evil-owner";
            options.RepositoryName = "evil-repository";
            options.Branch = "evil-branch";
            options.ApiBaseUri = new Uri("https://evil.example/");
            options.RawBaseUri = new Uri("https://evil.example/");
            options.StateKey = "evil-state";
            options.ScopeKey = "evil-scope";

            await client.GetManifestAsync(CancellationToken.None);
            await client.GetCurrentUserAsync(CancellationToken.None);
            await client.CreateDictionaryIssueAsync(CreateBatch("安全内容", "operation-immutable"), CancellationToken.None);

            Assert.AreEqual(
                "https://raw.githubusercontent.com/octo-owner/SHB-EOSDataDictionary/main/snapshot/manifest.json",
                handler.Requests[0].Uri);
            Assert.AreEqual("https://api.github.com/user", handler.Requests[1].Uri);
            StringAssert.StartsWith(handler.Requests[2].Uri, "https://api.github.com/search/issues?");
            Assert.AreEqual(
                "https://api.github.com/repos/octo-owner/SHB-EOSDataDictionary/issues",
                handler.Requests[3].Uri);
            Assert.IsNull(handler.Requests[0].Authorization);
            Assert.AreEqual("Bearer secret-token", handler.Requests[1].Authorization);
            Assert.AreEqual("Bearer secret-token", handler.Requests[2].Authorization);
            Assert.AreEqual("Bearer secret-token", handler.Requests[3].Authorization);
        }

        /// <summary>XMZADD 20260901 验证极大限流重置时间不会产生范围异常且仍返回受控限流分类。</summary>
        [TestMethod]
        public async Task GetCurrentUserAsync_ExtremeRateLimitReset_ReturnsControlledRateLimitError()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = JsonResponse(HttpStatusCode.Forbidden, "{}");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", long.MaxValue.ToString());
                return response;
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetCurrentUserAsync(CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.RateLimited, exception.Kind);
            Assert.IsTrue(exception.IsTransient);
        }

        /// <summary>XMZADD 20260901 验证极大 DataContract 日期被转换为受控协议错误而不是范围崩溃。</summary>
        [TestMethod]
        public async Task GetManifestAsync_ExtremeDataContractDate_ReturnsValidationError()
        {
            var handler = new RecordingHandler(delegate(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return JsonResponse(HttpStatusCode.OK,
                    "{\"FormatVersion\":1,\"Revision\":7,\"SnapshotSha256\":\"" + new string('a', 64) +
                    "\",\"SnapshotPath\":\"snapshot/latest.json.gz\",\"GeneratedAtUtc\":\"\\/Date(9223372036854775807)\\/\"}");
            });
            GitHubDictionaryClient client = CreateClient(handler, "secret-token");

            GitHubDictionaryClientException exception = await Assert.ThrowsExceptionAsync<GitHubDictionaryClientException>(
                async delegate { await client.GetManifestAsync(CancellationToken.None); });

            Assert.AreEqual(GitHubDictionaryErrorKind.Validation, exception.Kind);
        }

        /// <summary>XMZADD 20260901 创建带固定公开仓库坐标和可观察处理器的客户端。</summary>
        private static GitHubDictionaryClient CreateClient(HttpMessageHandler handler, string token)
        {
            return CreateClient(handler, delegate { return token; });
        }

        /// <summary>XMZADD 20260902 创建可观察动态 Token 提供器的客户端，验证多请求完整提交固定一次认证上下文。</summary>
        private static GitHubDictionaryClient CreateClient(HttpMessageHandler handler, Func<string> tokenProvider)
        {
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner",
                StateKey = "test-state",
                ScopeKey = "test-scope"
            };
            return new GitHubDictionaryClient(new HttpClient(handler), options, tokenProvider);
        }

        /// <summary>XMZADD 20260901 创建满足 Issue 协议的单操作批次以验证用户文本转义。</summary>
        private static DictionaryChangeBatch CreateBatch(string value, string operationId)
        {
            var batch = new DictionaryChangeBatch
            {
                BatchId = "batch-1",
                AuthorGitHubUserId = "123",
                CreatedAtUtc = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc)
            };
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = operationId,
                AuthorGitHubUserId = "123",
                ObjectKey = "dbo.T_TEST",
                FieldKey = "FNAME",
                PropertyName = "ChineseName",
                NewValue = value,
                ChangeKind = "Set",
                CreatedAtUtc = batch.CreatedAtUtc
            });
            return batch;
        }

        /// <summary>XMZADD 20260902 创建可由正式编解码器完整复核的 Revision 1 小型快照及匹配清单。</summary>
        private static byte[] CreateFullSnapshotContent(
            out SnapshotManifest manifest,
            string remarkDescription = null)
        {
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = 1,
                RefreshedAt = new DateTime(2026, 9, 2, 3, 4, 5, DateTimeKind.Utc)
            };
            var table = new TableMetadata
            {
                ScopeKey = "test-scope",
                SchemaName = "dbo",
                ObjectName = "T_TEST",
                ObjectType = "TABLE",
                Category = DictionaryTableCategory.Business,
                ApproximateRowCount = 1L,
                Remark = string.IsNullOrEmpty(remarkDescription)
                    ? null
                    : new MetadataValue
                    {
                        Value = "业务说明",
                        Description = remarkDescription,
                        Evidence = new List<EvidenceItem>()
                    }
            };
            table.Fields.Add(new FieldMetadata
            {
                FieldName = "FID",
                OwnerTableName = "T_TEST",
                DataType = "int"
            });
            snapshot.Tables.Add(table);
            var codec = new SnapshotCodec();
            byte[] content = codec.Encode(snapshot);
            string sha256 = codec.ComputeSha256(content);
            manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 1,
                SnapshotSha256 = sha256,
                SnapshotPath = "snapshot/revisions/000000001-" + sha256 + ".json.gz",
                GeneratedAtUtc = snapshot.RefreshedAt
            };
            return content;
        }

        /// <summary>XMZADD 20260902 创建 GitHub Contents API 返回的 Revision 0 清单，验证完整提交只基于尚未初始化的分支头。</summary>
        private static string CreateRevisionZeroContentsResponse()
        {
            string manifestJson = "{\"FormatVersion\":1,\"Revision\":0,\"SnapshotSha256\":\"\"," +
                "\"SnapshotPath\":\"snapshot/latest.json.gz\"," +
                "\"GeneratedAtUtc\":\"/Date(0)/\",\"LastEventPath\":\"\"}";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestJson));
            return "{\"encoding\":\"base64\",\"content\":\"" + encoded + "\"}";
        }

        /// <summary>XMZADD 20260901 创建带稳定修订和作者的远程事件以测试重放映射。</summary>
        private static AppliedDictionaryEvent CreateAppliedEvent(string operationId, string author, string changeKind)
        {
            return new AppliedDictionaryEvent
            {
                OperationId = operationId,
                AuthorGitHubUserId = author,
                ObjectKey = "dbo.T_TEST",
                FieldKey = "FNAME",
                PropertyName = "ChineseName",
                ChangeKind = changeKind,
                Revision = 3L,
                AppliedAtUtc = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc)
            };
        }

        /// <summary>XMZADD 20260901 使用正式数据契约生成远程事件 JSON，避免测试依赖手写字段转义。</summary>
        private static byte[] SerializeJson(object value, Type type)
        {
            var serializer = new DataContractJsonSerializer(type);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        /// <summary>XMZADD 20260902 解码测试捕获的 blob 请求，核对单 Commit 中实际写入的清单内容。</summary>
        private static RecordedBlobRequest DeserializeBlobRequest(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(RecordedBlobRequest));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(stream) as RecordedBlobRequest;
            }
        }

        /// <summary>XMZADD 20260903 解码测试捕获的 Issue 创建请求，以核对恢复批次的标题和公开正文。</summary>
        private static RecordedIssueRequest DeserializeIssueRequest(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(RecordedIssueRequest));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(stream) as RecordedIssueRequest;
            }
        }

        /// <summary>XMZADD 20260902 通过私有工厂创建正式流式 Blob 内容，使测试覆盖真实分块序列化而不扩大生产 API。</summary>
        private static HttpContent CreateSnapshotBlobContentForTest(
            byte[] source,
            CancellationToken cancellationToken)
        {
            System.Reflection.MethodInfo method = typeof(GitHubDictionaryClient).GetMethod(
                "CreateSnapshotBlobContent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);
            HttpContent content = method.Invoke(null, new object[] { source, cancellationToken }) as HttpContent;
            Assert.IsNotNull(content);
            return content;
        }

        /// <summary>XMZADD 20260901 创建 UTF-8 JSON 响应以模拟 GitHub API 和公开原始文件。</summary>
        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        /// <summary>XMZADD 20260901 创建原始字节响应以模拟事件和压缩快照下载。</summary>
        private static HttpResponseMessage ByteResponse(HttpStatusCode statusCode, byte[] content)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(content)
            };
        }

        /// <summary>XMZADD 20260901 将预期正文转换为 JSON 字符串内部的安全转义形式。</summary>
        private static string EscapeJsonString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>XMZADD 20260901 构造仅含一个候选项的 GitHub 搜索响应，精确复现批次对账输入。</summary>
        private static string CreateSearchResponse(int issueNumber, string title, string body)
        {
            return "{\"total_count\":1,\"incomplete_results\":false,\"items\":[{\"number\":" + issueNumber.ToString(CultureInfo.InvariantCulture) +
                ",\"title\":\"" + EscapeJsonString(title) + "\",\"body\":\"" + EscapeJsonString(body) + "\"}]}";
        }

        /// <summary>XMZADD 20260901 构造无候选项的 GitHub 搜索响应，表示发布前尚未创建对应批次。</summary>
        private static string CreateEmptySearchResponse()
        {
            return "{\"total_count\":0,\"incomplete_results\":false,\"items\":[]}";
        }

        /// <summary>XMZADD 20260901 构造仓库最新 Issue 列表响应，验证未知提交结果不依赖搜索索引刷新。</summary>
        private static string CreateIssueListResponse(int issueNumber, string title, string body)
        {
            return "[{\"number\":" + issueNumber.ToString(CultureInfo.InvariantCulture) +
                ",\"title\":\"" + EscapeJsonString(title) + "\",\"body\":\"" + EscapeJsonString(body) + "\"}]";
        }

        /// <summary>XMZADD 20260901 记录请求中与凭据隔离相关的有限字段，避免测试辅助对象保存 Token 以外的敏感内容。</summary>
        private sealed class RecordedRequest
        {
            public string Method { get; set; }
            public string Uri { get; set; }
            public string Query { get; set; }
            public string Authorization { get; set; }
            public string UserAgentProduct { get; set; }
            public string Accept { get; set; }
            public string ApiVersion { get; set; }
            public long? ContentLength { get; set; }
            public string Body { get; set; }
        }

        /// <summary>XMZADD 20260902 映射测试捕获的 Git blob 请求正文以检查 base64 清单。</summary>
        [DataContract]
        private sealed class RecordedBlobRequest
        {
            [DataMember(Name = "content")]
            public string Content { get; set; }

            [DataMember(Name = "encoding")]
            public string Encoding { get; set; }
        }

        /// <summary>XMZADD 20260903 映射测试捕获的 Issue 标题和正文，验证恢复请求不泄露本地覆盖。</summary>
        [DataContract]
        private sealed class RecordedIssueRequest
        {
            [DataMember(Name = "title")]
            public string Title { get; set; }

            [DataMember(Name = "body")]
            public string Body { get; set; }
        }

        /// <summary>XMZADD 20260902 在指定次数的成功异步写入后触发取消，用于验证流式 Blob 不会继续输出剩余正文。</summary>
        private sealed class CancelAfterSuccessfulWritesStream : MemoryStream
        {
            private readonly CancellationTokenSource _cancellationSource;
            private readonly int _successfulWriteLimit;
            private int _successfulWriteCount;

            /// <summary>XMZADD 20260902 绑定待触发的取消源和成功写入次数边界，精确控制分块取消时点。</summary>
            public CancelAfterSuccessfulWritesStream(
                CancellationTokenSource cancellationSource,
                int successfulWriteLimit)
            {
                _cancellationSource = cancellationSource;
                _successfulWriteLimit = successfulWriteLimit;
            }

            /// <summary>XMZADD 20260902 在当前块成功保存后触发取消，使调用方只能在下一个分块边界观察取消。</summary>
            public override async Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                _successfulWriteCount++;
                if (_successfulWriteCount == _successfulWriteLimit)
                {
                    _cancellationSource.Cancel();
                }
            }
        }

        /// <summary>XMZADD 20260902 在内存中模拟 GitHub 响应并捕获含长度的请求契约，确保测试不访问真实网络。</summary>
        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

            /// <summary>XMZADD 20260901 注入同步响应规则以便每个测试精确控制协议分支。</summary>
            public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            {
                _responder = responder;
                Requests = new List<RecordedRequest>();
            }

            public IList<RecordedRequest> Requests { get; private set; }

            /// <summary>XMZADD 20260902 捕获最小请求及正文长度信息后返回内存响应，避免任何真实网络访问。</summary>
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                long? preReadContentLength = request.Content == null
                    ? null
                    : request.Content.Headers.ContentLength;
                if (request.Content != null)
                {
                    Assert.IsTrue(preReadContentLength.HasValue);
                }
                string body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                if (body != null)
                {
                    Assert.AreEqual(preReadContentLength.Value, (long)Encoding.UTF8.GetByteCount(body));
                }
                Requests.Add(new RecordedRequest
                {
                    Method = request.Method.Method,
                    Uri = request.RequestUri.AbsoluteUri,
                    Query = request.RequestUri.Query,
                    Authorization = request.Headers.Authorization == null ? null : request.Headers.Authorization.ToString(),
                    UserAgentProduct = request.Headers.UserAgent.Count == 0
                        ? null
                        : request.Headers.UserAgent.ToString().Split('/')[0],
                    Accept = request.Headers.Accept.Count == 0 ? null : request.Headers.Accept.ToString(),
                    ApiVersion = request.Headers.Contains("X-GitHub-Api-Version")
                        ? string.Join(",", request.Headers.GetValues("X-GitHub-Api-Version"))
                        : null,
                    ContentLength = preReadContentLength,
                    Body = body
                });
                cancellationToken.ThrowIfCancellationRequested();
                return _responder(request, cancellationToken);
            }
        }
    }
}
