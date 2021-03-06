﻿/*
Copyright 2012 Google Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using NUnit.Framework;

using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Services;
using Google.Apis.Testing;
using Google.Apis.Upload;
using Google.Apis.Util;

namespace Google.Apis.Tests.Apis.Upload
{
    [TestFixture]
    class ResumableUploadTest
    {
        /// <summary> 
        /// Mock string to upload to the media server. It contains 453 bytes, and in most cases we will use a chunk 
        /// size of 100. 
        /// </summary>
        static string UploadTestData = @"Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod 
tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris 
nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore 
eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit 
anim id est laborum.";

        #region Handlers

        /// <summary> Base mock handler which contains the upload Uri. </summary>
        private abstract class BaseMockMessageHandler : CountableMessageHandler
        {
            /// <summary> The upload Uri for uploading the media. </summary>
            protected static Uri uploadUri = new Uri("http://upload.com");
        }

        /// <summary> An handler which handles uploading an empty file. </summary>
        private class EmptyFileMessageHandler : BaseMockMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage();
                switch (Calls)
                {
                    case 1:
                        // First call is initialization
                        Assert.That(request.RequestUri.Query, Is.EqualTo("?uploadType=resumable"));
                        Assert.That(request.Headers.GetValues("X-Upload-Content-Type").First(),
                            Is.EqualTo("text/plain"));
                        Assert.That(request.Headers.GetValues("X-Upload-Content-Length").First(), Is.EqualTo("0"));

                        response.Headers.Location = uploadUri;
                        break;
                    case 2:
                        // Receiving an empty stream
                        Assert.That(request.RequestUri, Is.EqualTo(uploadUri));
                        var range = String.Format("bytes */0");
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));
                        Assert.That(request.Content.Headers.ContentLength, Is.EqualTo(0));
                        break;
                }

                return response;
            }
        }

        /// <summary> An handler which handles a request object on the initialization request. </summary>
        private class RequestResponseMessageHandler<TRequest> : BaseMockMessageHandler
        {
            /// <summary> 
            /// Gets or sets the expected request object. Server checks that the initialization request contains that 
            /// object in the request.
            /// </summary>
            public TRequest ExpectedRequest { get; set; }

            /// <summary>
            /// Gets or sets the expected response object which server returns as a response for the upload request.
            /// </summary>
            public object ExpectedResponse { get; set; }

            /// <summary> Gets or sets the Serializer which is used to serialize and deserialize objects. </summary>
            public ISerializer Serializer { get; set; }

            protected override async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage();
                switch (Calls)
                {
                    case 1:
                        {
                            // initialization and receiving the request object
                            Assert.That(request.RequestUri.Query, Is.EqualTo("?uploadType=resumable"));
                            Assert.That(request.Headers.GetValues("X-Upload-Content-Type").First(),
                                Is.EqualTo("text/plain"));
                            Assert.That(request.Headers.GetValues("X-Upload-Content-Length").First(),
                                Is.EqualTo(UploadTestData.Length.ToString()));
                            response.Headers.Location = uploadUri;

                            var body = await request.Content.ReadAsStringAsync();
                            var reqObject = Serializer.Deserialize<TRequest>(body);
                            Assert.That(reqObject, Is.EqualTo(ExpectedRequest));
                            break;
                        }
                    case 2:
                        {
                            // check that the server received the media
                            Assert.That(request.RequestUri, Is.EqualTo(uploadUri));
                            var range = String.Format("bytes 0-{0}/{1}", UploadTestData.Length - 1,
                                UploadTestData.Length);
                            Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));

                            // Send the response-body
                            var responseObject = Serializer.Serialize(ExpectedResponse);
                            response.Content = new StringContent(responseObject, Encoding.UTF8, "application/json");
                            break;
                        }
                }

                return response;
            }
        }

        /// <summary> An handler which handles a request for single chunk. </summary>
        private class SingleChunkMessageHandler : BaseMockMessageHandler
        {
            /// <summary> Gets or sets the expected stream length. </summary>
            public long StreamLength { get; set; }

            /// <summary> Gets or sets the query parameters which should be part of the initialize request. </summary>
            public string QueryParameters { get; set; }

            /// <summary> Gets or sets the path parameters which should be part of the initialize request. </summary>
            public string PathParameters { get; set; }

            protected override async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage();
                var range = string.Empty;
                switch (Calls)
                {
                    case 1:
                        if (PathParameters == null)
                        {
                            Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/"));
                        }
                        else
                        {
                            Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/" + PathParameters));
                        }
                        Assert.That(request.RequestUri.Query, Is.EqualTo("?uploadType=resumable" + QueryParameters));

                        Assert.That(request.Headers.GetValues("X-Upload-Content-Type").First(),
                            Is.EqualTo("text/plain"));
                        Assert.That(request.Headers.GetValues("X-Upload-Content-Length").First(),
                            Is.EqualTo(StreamLength.ToString()));

                        response.Headers.Location = uploadUri;
                        break;

                    case 2:
                        Assert.That(request.RequestUri, Is.EqualTo(uploadUri));
                        range = String.Format("bytes 0-{0}/{1}", StreamLength - 1, StreamLength);
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));
                        Assert.That(request.Content.Headers.ContentLength, Is.EqualTo(StreamLength));
                        break;
                }

                return response;
            }
        }

        /// <summary> 
        /// An handler which demonstrate a server which reads partial data (e.g. on the first upload request the client
        /// sends X bytes, but the server actually read only Y of them)
        /// </summary>
        private class ReadPartialMessageHandler : BaseMockMessageHandler
        {
            /// <summary> Received stream which contains the data that the server reads. </summary>
            public MemoryStream ReceivedData = new MemoryStream();

            private bool knownSize;
            private int len;
            private int chunkSize;

            const int readInFirstRequest = 120;

            /// <summary>
            /// Constructs a new handler with the given stream length, chunkd size and indication if the length is 
            /// known.
            /// </summary>
            public ReadPartialMessageHandler(bool knownSize, int len, int chunkSize)
            {
                this.knownSize = knownSize;
                this.len = len;
                this.chunkSize = chunkSize;
            }

            protected override async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage();
                byte[] bytes = null;

                switch (Calls)
                {
                    case 1:
                        // initialization request
                        Assert.That(request.RequestUri.Query, Is.EqualTo("?uploadType=resumable"));
                        Assert.That(request.Headers.GetValues("X-Upload-Content-Type").First(),
                            Is.EqualTo("text/plain"));
                        if (knownSize)
                        {
                            Assert.That(request.Headers.GetValues("X-Upload-Content-Length").First(),
                                Is.EqualTo(UploadTestData.Length.ToString()));
                        }
                        else
                        {
                            Assert.False(request.Headers.Contains("X-Upload-Content-Length"));
                        }
                        response.Headers.Location = uploadUri;
                        break;
                    case 2:
                        // first client upload request. server reads only <c>readInFirstRequest</c> bytes and returns 
                        // a response with Range header - "bytes 0-readInFirstRequest"
                        Assert.That(request.RequestUri, Is.EqualTo(uploadUri));
                        var range = String.Format("bytes {0}-{1}/{2}", 0, chunkSize - 1,
                            knownSize ? len.ToString() : "*");

                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));
                        response.StatusCode = (HttpStatusCode)308;
                        response.Headers.Add("Range", "bytes 0-" + (readInFirstRequest - 1));

                        bytes = await request.Content.ReadAsByteArrayAsync();
                        ReceivedData.Write(bytes, 0, readInFirstRequest);
                        break;
                    case 3:
                        // server reads the rest of bytes
                        Assert.That(request.RequestUri, Is.EqualTo(uploadUri));
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(
                            string.Format("bytes {0}-{1}/{2}", readInFirstRequest, len - 1, len)));

                        bytes = await request.Content.ReadAsByteArrayAsync();
                        ReceivedData.Write(bytes, 0, bytes.Length);
                        break;
                }

                return response;
            }
        }

        public enum ServerError
        {
            None,
            Exception,
            ServerUnavailable,
            NotFound
        }

        /// <summary> An handler which demonstrate a client upload which contains multiple chunks. </summary>
        private class MultipleChunksMessageHandler : BaseMockMessageHandler
        {
            public MemoryStream ReceivedData = new MemoryStream();

            /// <summary> The cancellation token we are going to use to cancel a request.</summary>
            public CancellationTokenSource CancellationTokenSource { get; set; }

            /// <summary> The request index we are going to cancel.</summary>
            public int CancelRequestNum { get; set; }

            // on the 4th request - server returns error (if supportedError isn't none)
            // on the 5th request - server returns 308 with "Range" header is "bytes 0-299" (depends on supportedError)
            internal const int ErrorOnCall = 4;

            private ServerError supportedError;

            private bool knownSize;
            private int len;
            private int chunkSize;
            private bool alwaysFailFromFirstError;

            private int bytesRecieved = 0;

            public MultipleChunksMessageHandler(bool knownSize, ServerError supportedError, int len, int chunkSize,
                bool alwaysFailFromFirstError = false)
            {
                this.knownSize = knownSize;
                this.supportedError = supportedError;
                this.len = len;
                this.chunkSize = chunkSize;
                this.alwaysFailFromFirstError = alwaysFailFromFirstError;
            }

            protected override async Task<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (Calls == CancelRequestNum && CancellationTokenSource != null)
                {
                    CancellationTokenSource.Cancel();
                }

                var response = new HttpResponseMessage();
                if (Calls == 1)
                {
                    // initialization request
                    Assert.That(request.RequestUri.Query, Is.EqualTo("?uploadType=resumable"));
                    Assert.That(request.Headers.GetValues("X-Upload-Content-Type").First(), Is.EqualTo("text/plain"));
                    if (knownSize)
                    {
                        Assert.That(request.Headers.GetValues("X-Upload-Content-Length").First(),
                            Is.EqualTo(UploadTestData.Length.ToString()));
                    }
                    else
                    {
                        Assert.False(request.Headers.Contains("X-Upload-Content-Length"));
                    }

                    response.Headers.Location = uploadUri;
                }
                else
                {
                    Assert.That(request.RequestUri, Is.EqualTo(uploadUri));

                    var chunkEnd = Math.Min(len, bytesRecieved + chunkSize) - 1;
                    var range = String.Format("bytes {0}-{1}/{2}", bytesRecieved, chunkEnd,
                    chunkEnd + 1 == len || knownSize ? UploadTestData.Length.ToString() : "*");

                    if (Calls == ErrorOnCall && supportedError != ServerError.None)
                    {
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));
                        if (supportedError == ServerError.ServerUnavailable)
                        {
                            response.StatusCode = HttpStatusCode.ServiceUnavailable;
                        }
                        else if (supportedError == ServerError.NotFound)
                        {
                            response.StatusCode = HttpStatusCode.NotFound;
                            var error = @"{
                                            ""error"": {
                                                ""errors"": [
                                                    {
                                                        ""domain"": ""global"",
                                                        ""reason"": ""required"",
                                                        ""message"": ""Login Required"",
                                                        ""locationType"": ""header"",
                                                        ""location"": ""Authorization""
                                                    }
                                                ],
                                                ""code"": 401,
                                                ""message"": ""Login Required""
                                            }
                                          }";
                            response.Content = new StringContent(error);
                        }
                        else
                        {
                            throw new Exception("ERROR");
                        }
                    }
                    else if (Calls >= ErrorOnCall && alwaysFailFromFirstError)
                    {
                        if (supportedError == ServerError.Exception)
                        {
                            throw new Exception("ERROR");
                        }
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(
                            string.Format(@"bytes */{0}", knownSize ? UploadTestData.Length.ToString() : "*")));
                        response.StatusCode = HttpStatusCode.ServiceUnavailable;
                    }
                    else if (Calls == ErrorOnCall + 1 && supportedError != ServerError.None)
                    {
                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(
                            string.Format(@"bytes */{0}", knownSize ? UploadTestData.Length.ToString() : "*")));
                        response.StatusCode = (HttpStatusCode)308;
                        response.Headers.Add("Range", "bytes 0-" + (bytesRecieved - 1));
                    }
                    else
                    {
                        var bytes = await request.Content.ReadAsByteArrayAsync();
                        ReceivedData.Write(bytes, 0, bytes.Length);

                        Assert.That(request.Content.Headers.GetValues("Content-Range").First(), Is.EqualTo(range));
                        if (chunkEnd != len - 1)
                        {
                            response.StatusCode = (HttpStatusCode)308;
                            response.Headers.Add("Range", string.Format("bytes {0}-{1}", bytesRecieved, chunkEnd));
                        }
                        bytesRecieved += bytes.Length;
                    }
                }
                return response;
            }
        }

        #endregion

        #region ResumableUpload instances

        private class MockResumableUpload : ResumableUpload<object>
        {
            public MockResumableUpload(IClientService service, Stream stream, string contentType)
                : this(service, "path", "PUT", stream, contentType)
            {
            }

            public MockResumableUpload(IClientService service, string path, string method, Stream stream,
                string contentType)
                : base(service, path, method, stream, contentType)
            {
            }
        }

        /// <summary>
        /// A resumable upload class which gets a specific request object and returns a specific response object.
        /// </summary>
        /// <typeparam name="TRequest"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        private class MockResumableUploadWithResponse<TRequest, TResponse> : ResumableUpload<TRequest, TResponse>
        {
            public MockResumableUploadWithResponse(IClientService service,
                Stream stream, string contentType)
                : base(service, "path", "POST", stream, contentType)
            {
            }
        }

        /// <summary> A resumable upload class which contains query and path parameters. </summary>
        private class MockResumableWithParameters : ResumableUpload<object>
        {
            public MockResumableWithParameters(IClientService service, string path, string method,
                Stream stream, string contentType)
                : base(service, path, method, stream, contentType)
            {
            }

            [Google.Apis.Util.RequestParameter("id", RequestParameterType.Path)]
            public int id { get; set; }

            [Google.Apis.Util.RequestParameter("queryA", RequestParameterType.Query)]
            public string querya { get; set; }

            [Google.Apis.Util.RequestParameter("queryB", RequestParameterType.Query)]
            public string queryb { get; set; }
        }

        #endregion

        /// <summary> Mimics a stream whose size is unknown. </summary>
        private class UnknownSizeMemoryStream : MemoryStream
        {
            public UnknownSizeMemoryStream(byte[] buffer) : base(buffer) { }
            public override bool CanSeek
            {
                get { return false; }
            }
        }

        #region Request and Response objects

        /// <summary> A mock request object. </summary>
        public class TestRequest : IEquatable<TestRequest>
        {
            public string Name { get; set; }
            public string Description { get; set; }

            public bool Equals(TestRequest other)
            {
                if (other == null)
                    return false;

                return Name == null ? other.Name == null : Name.Equals(other.Name) &&
                    Description == null ? other.Description == null : Description.Equals(other.Description);
            }
        }

        /// <summary> A mock response object. </summary>
        public class TestResponse : IEquatable<TestResponse>
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }

            public bool Equals(TestResponse other)
            {
                if (other == null)
                    return false;

                return Id.Equals(other.Id) &&
                    Name == null ? other.Name == null : Name.Equals(other.Name) &&
                    Description == null ? other.Description == null : Description.Equals(other.Description);
            }
        }

        #endregion

        /// <summary> Tests uploading a single chunk. </summary>
        [Test]
        public void TestUploadSingleChunk()
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(UploadTestData));
            var handler = new SingleChunkMessageHandler()
                {
                    StreamLength = stream.Length
                };
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {

                var upload = new MockResumableUpload(service, "", "POST", stream, "text/plain");
                // chunk size is bigger than the data we are sending
                upload.ChunkSize = UploadTestData.Length + 10;
                upload.Upload();
            }

            Assert.That(handler.Calls, Is.EqualTo(2));
        }

        /// <summary> Tests uploading a single chunk. </summary>
        [Test]
        public void TestUploadSingleChunk_ExactChunkSize()
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(UploadTestData));
            var handler = new SingleChunkMessageHandler()
                {
                    StreamLength = stream.Length
                };
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var upload = new MockResumableUpload(service, "", "POST", stream, "text/plain");
                // chunk size is the exact size we are sending
                upload.ChunkSize = UploadTestData.Length;
                upload.Upload();
            }

            Assert.That(handler.Calls, Is.EqualTo(2));
        }

        /// <summary> Tests uploading empty file. </summary>
        [Test]
        public void TestUploadEmptyFile()
        {
            var handler = new EmptyFileMessageHandler();
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = new MemoryStream(new byte[0]);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.Upload();
            }

            Assert.That(handler.Calls, Is.EqualTo(2));
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 responses when uploading chunks on a stream with known size. 
        /// </summary>
        [Test]
        public void TestChunkUpload_KnownSize()
        {
            SubtestTestChunkUpload(true);
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 responses when uploading chunks on a stream with unknown size. 
        /// </summary>
        [Test]
        public void TestChunkUpload_UnknownSize()
        {
            SubtestTestChunkUpload(false);
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 and 503 responses when uploading chunks on a stream with known 
        /// size. 
        /// </summary>
        [Test]
        public void TestChunkUpload_ServerUnavailable_KnownSize()
        {
            SubtestTestChunkUpload(true, ServerError.ServerUnavailable);
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 and 503 responses when uploading chunks on a stream with unknown
        /// size.
        /// </summary>
        [Test]
        public void TestChunkUpload_ServerUnavailable_UnknownSize()
        {
            SubtestTestChunkUpload(false, ServerError.ServerUnavailable);
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 and exception on a request when uploading chunks on a stream with 
        /// unknown size.
        /// </summary>
        [Test]
        public void TestChunkUpload_Exception_UnknownSize()
        {
            SubtestTestChunkUpload(false, ServerError.Exception);
        }

        /// <summary> 
        /// Tests that upload fails when server returns an error which the client can't handle (not 5xx).
        /// </summary>
        [Test]
        public void TestChunkUpload_NotFound_KnownSize()
        {
            SubtestTestChunkUpload(true, ServerError.NotFound);
        }

        private void SubtestTestChunkUpload(bool knownSize, ServerError error = ServerError.None)
        {
            // -If error is none - there isn't any error, so there should be 6 calls (1 to initialize and 5 successful
            // upload requests.
            // -If error isn't supported by the media upload (4xx) - the upload fails.
            // Otherwise, we simulate server 503 error or exception, as following:
            // On the 4th chunk (when server received bytes 300-399), we mimic an error. 
            // In the next request we expect the client to send the content range header with "bytes */[size]", and 
            // server return that the upload was interrupted after 299 bytes. From that point the server works as 
            // expected, and received the last chunks successfully
            int chunkSize = 100;
            var payload = Encoding.UTF8.GetBytes(UploadTestData);

            var handler = new MultipleChunksMessageHandler(knownSize, error, payload.Length, chunkSize);
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = knownSize ? new MemoryStream(payload) : new UnknownSizeMemoryStream(payload);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.ChunkSize = chunkSize;

                IUploadProgress lastProgress = null;
                upload.ProgressChanged += (p) => lastProgress = p;
                upload.Upload();

                Assert.NotNull(lastProgress);

                if (error == ServerError.NotFound)
                {
                    // upload fails
                    Assert.That(lastProgress.Status, Is.EqualTo(UploadStatus.Failed));
                    Assert.That(handler.Calls, Is.EqualTo(MultipleChunksMessageHandler.ErrorOnCall));
                    Assert.True(lastProgress.Exception.Message.Contains(
                         @"Message[Login Required] Location[Authorization - header] Reason[required] Domain[global]"),
                         "Error message is invalid");
                }
                else
                {
                    Assert.That(lastProgress.Status, Is.EqualTo(UploadStatus.Completed));
                    Assert.That(payload, Is.EqualTo(handler.ReceivedData.ToArray()));
                    // if server doesn't supports errors there should be 6 request - 1 initialize request and 5 
                    // requests to upload data (the whole data send is divided to 5 chunks). 
                    // if server supports errors, there should be an additional 2 requests (1 to query where the upload 
                    // was interrupted and 1 to resend the data)
                    Assert.That(handler.Calls, error != ServerError.None ? Is.EqualTo(8) : Is.EqualTo(6));
                }
            }
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 responses and reads the "Range" header to know from which point to
        /// continue (stream size is known). 
        /// </summary>
        [Test]
        public void TestChunkUpload_ServerRecievedPartOfRequest_KnownSize()
        {
            SubtestTestChunkUpload_ServerRecievedPartOfRequest(true);
        }

        /// <summary> 
        /// Tests that the upload client accepts 308 responses and reads the "Range" header to know from which point to
        /// continue (stream size is unknown). 
        /// </summary>
        [Test]
        public void TestChunkUpload_ServerRecievedPartOfRequest_UnknownSize()
        {
            SubtestTestChunkUpload_ServerRecievedPartOfRequest(false);
        }

        private void SubtestTestChunkUpload_ServerRecievedPartOfRequest(bool knownSize)
        {
            int chunkSize = 400;
            var payload = Encoding.UTF8.GetBytes(UploadTestData);

            var handler = new ReadPartialMessageHandler(knownSize, payload.Length, chunkSize);
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = knownSize ? new MemoryStream(payload) : new UnknownSizeMemoryStream(payload);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.ChunkSize = chunkSize;
                upload.Upload();

                Assert.That(payload, Is.EqualTo(handler.ReceivedData.ToArray()));
                // 1 initialization request and 2 uploads requests.
                Assert.That(handler.Calls, Is.EqualTo(3));
            }
        }

        /// <summary> Test helper to test a fail uploading by with the given server error.</summary>
        private void SubtestChunkUploadFail(ServerError error)
        {
            int chunkSize = 100;
            var payload = Encoding.UTF8.GetBytes(UploadTestData);

            var handler = new MultipleChunksMessageHandler(true, error, payload.Length,
                chunkSize, true);
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = new MemoryStream(payload);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.ChunkSize = chunkSize;

                IUploadProgress lastProgressStatus = null;
                upload.ProgressChanged += (p) =>
                {
                    lastProgressStatus = p;
                };
                upload.Upload();

                var exepctedCalls = MultipleChunksMessageHandler.ErrorOnCall +
                    service.HttpClient.MessageHandler.NumTries - 1;
                Assert.That(handler.Calls, Is.EqualTo(exepctedCalls));
                Assert.NotNull(lastProgressStatus);
                Assert.NotNull(lastProgressStatus.Exception);
                Assert.That(lastProgressStatus.Status, Is.EqualTo(UploadStatus.Failed));
            }
        }

        /// <summary> 
        /// Tests failed uploading media (server returns 5xx responses all the time from some request).
        /// </summary>
        [Test]
        public void TestChunkUploadFail_ServerUnavailable()
        {
            SubtestChunkUploadFail(ServerError.ServerUnavailable);
        }

        /// <summary> 
        /// Tests failed uploading media (exception is thrown all the time from some request).
        /// </summary>
        [Test]
        public void TestChunkUploadFail_Exception()
        {
            SubtestChunkUploadFail(ServerError.Exception);
        }

        /// <summary> Tests uploading media when cancelling a request in the middle.</summary>
        [Test]
        public void TestChunkUploadFail_Cancel()
        {
            TestChunkUploadFail_Cancel(1); // cancel the request initialization
            TestChunkUploadFail_Cancel(2); // cancel the first media upload data
            TestChunkUploadFail_Cancel(5); // cancel a request in the middle of the upload
        }

        /// <summary> Helper test to test canceling media upload in the middle.</summary>
        /// <param name="cancelRequest">The request index to cancel.</param>
        private void TestChunkUploadFail_Cancel(int cancelRequest)
        {
            int chunkSize = 100;
            var payload = Encoding.UTF8.GetBytes(UploadTestData);

            var handler = new MultipleChunksMessageHandler(true, ServerError.None, payload.Length, chunkSize, false);
            handler.CancellationTokenSource = new CancellationTokenSource();
            handler.CancelRequestNum = cancelRequest;
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = new MemoryStream(payload);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.ChunkSize = chunkSize;

                try
                {
                    var result = upload.UploadAsync(handler.CancellationTokenSource.Token).Result;
                    Assert.Fail();
                }
                catch (AggregateException ae)
                {
                    Assert.IsInstanceOf<TaskCanceledException>(ae.InnerException);
                }

                Assert.That(handler.Calls, Is.EqualTo(cancelRequest));
            }
        }

        /// <summary> Tests that upload function fires progress events as expected. </summary>
        [Test]
        public void TestUploadProgress()
        {
            int chunkSize = 200;
            var payload = Encoding.UTF8.GetBytes(UploadTestData);

            var handler = new MultipleChunksMessageHandler(true, ServerError.None, payload.Length, chunkSize);
            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var stream = new MemoryStream(payload);
                var upload = new MockResumableUpload(service, stream, "text/plain");
                upload.ChunkSize = chunkSize;

                var progressEvents = new List<IUploadProgress>();
                upload.ProgressChanged += (progress) =>
                {
                    progressEvents.Add(progress);
                };

                upload.Upload();

                // Starting (1) + Uploading (2) + Completed (1)
                Assert.That(progressEvents.Count, Is.EqualTo(4));
                Assert.That(progressEvents[0].Status, Is.EqualTo(UploadStatus.Starting));
                Assert.That(progressEvents[1].Status, Is.EqualTo(UploadStatus.Uploading));
                Assert.That(progressEvents[2].Status, Is.EqualTo(UploadStatus.Uploading));
                Assert.That(progressEvents[3].Status, Is.EqualTo(UploadStatus.Completed));
            }
        }

        /// <summary> Tests uploading media with query and path parameters on the initialization request. </summary>
        [Test]
        public void TestUploadWithQueryAndPathParameters()
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(UploadTestData));

            const int id = 123;
            var handler = new SingleChunkMessageHandler()
                {
                    PathParameters = "testPath/" + id.ToString(),
                    QueryParameters = "&queryA=valuea&queryB=VALUEB",
                    StreamLength = stream.Length
                };

            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler)
                }))
            {
                var upload = new MockResumableWithParameters(service, "testPath/{id}", "POST", stream, "text/plain")
                {
                    id = id,
                    querya = "valuea",
                    queryb = "VALUEB",
                };
                upload.Upload();

                Assert.That(handler.Calls, Is.EqualTo(2));
            }
        }

        /// <summary> Tests an upload with JSON request and response body. </summary>
        [Test]
        public void TestUploadWithRequestAndResponseBody()
        {
            var body = new TestRequest()
                {
                    Name = "test object",
                    Description = "the description",
                };

            var handler = new RequestResponseMessageHandler<TestRequest>()
                {
                    ExpectedRequest = body,
                    ExpectedResponse = new TestResponse
                        {
                            Name = "foo",
                            Id = 100,
                            Description = "bar",
                        },
                    Serializer = new NewtonsoftJsonSerializer()
                };

            using (var service = new MockClientService(new BaseClientService.Initializer()
                {
                    HttpClientFactory = new MockHttpClientFactory(handler),
                    GZipEnabled = false // TODO(peleyal): test with GZipEnabled as well
                }))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(UploadTestData));
                var upload = new MockResumableUploadWithResponse<TestRequest, TestResponse>
                    (service, stream, "text/plain")
                    {
                        Body = body
                    };

                TestResponse response = null;
                int reponseReceivedCount = 0;

                upload.ResponseReceived += (r) => { response = r; reponseReceivedCount++; };
                upload.Upload();

                Assert.That(upload.ResponseBody, Is.EqualTo(handler.ExpectedResponse));
                Assert.That(reponseReceivedCount, Is.EqualTo(1));
                Assert.That(handler.Calls, Is.EqualTo(2));
            }
        }
    }
}