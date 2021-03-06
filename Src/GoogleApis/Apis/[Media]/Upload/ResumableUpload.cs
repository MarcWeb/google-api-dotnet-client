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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Google.Apis.Http;
using Google.Apis.Logging;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Util;

namespace Google.Apis.Upload
{
    /// <summary>
    /// Media upload which uses Google's resumable media upload protocol to upload data.
    /// </summary>
    /// <remarks>
    /// See: https://developers.google.com/drive/manage-uploads#resumable for more information on the protocol.
    /// </remarks>
    /// <typeparam name="TRequest">
    /// The type of the body of this request. Generally this should be the metadata related to the content to be 
    /// uploaded. Must be serializable to/from JSON.
    /// </typeparam>
    public class ResumableUpload<TRequest>
    {
        #region Constants

        /// <summary> The class logger. </summary>
        private static readonly ILogger logger = ApplicationContext.Logger.ForType<ResumableUpload<TRequest>>();

        private const int KB = 0x400;
        private const int MB = 0x100000;

        /// <summary> Minimum chunk size (except the last one). Default value is 256*KB. </summary>
        public const int MinimumChunkSize = 256 * KB;

        /// <summary> Default chunk size. Default value is 10*MB. </summary>
        public const int DefaultChunkSize = 10 * MB;

        /// <summary>
        /// Defines how many bytes are read from the input stream in each stream read action. 
        /// The read will continue until we read <see cref="MinimumChunkSize"/> or we reached the end of the stream.
        /// </summary>
        internal int BufferSize = 4 * KB;

        /// <summary> Indicates the stream's size is unknown. </summary>
        private const int UnknownSize = -1;

        /// <summary> The mime type for the encoded JSON body. </summary>
        private const string JsonMimeType = "application/json; charset=UTF-8";

        /// <summary> Payload description headers, describing the content itself. </summary>
        private const string PayloadContentTypeHeader = "X-Upload-Content-Type";

        /// <summary> Payload description headers, describing the content itself. </summary>
        private const string PayloadContentLengthHeader = "X-Upload-Content-Length";

        /// <summary> Specify the type of this upload (this class supports resumable only). </summary>
        private const string UploadType = "uploadType";

        /// <summary> The uploadType parameter value for resumable uploads. </summary>
        private const string Resumable = "resumable";

        /// <summary> Content-Range header value for the body upload of zero length files. </summary>
        private const string ZeroByteContentRangeHeader = "bytes */0";

        #endregion // Constants

        #region Construction

        /// <summary>
        /// Create a resumable upload instance with the required parameters.
        /// </summary>
        /// <param name="service">The client service.</param>
        /// <param name="path">The path for this media upload method.</param>
        /// <param name="httpMethod">The Http method to start this upload.</param>
        /// <param name="contentStream">The stream containing the content to upload.</param>
        /// <param name="contentType">Content type of the content to be uploaded.</param>
        /// <remarks>
        /// Caller is responsible for maintaining the <paramref name="contentStream"/> open until the upload is 
        /// completed.
        /// Caller is responsible for closing the <paramref name="contentStream"/>.
        /// </remarks>
        protected ResumableUpload(IClientService service, string path, string httpMethod,
            Stream contentStream, string contentType)
        {
            service.ThrowIfNull("service");
            path.ThrowIfNull("path");
            httpMethod.ThrowIfNullOrEmpty("httpMethod");
            contentStream.ThrowIfNull("stream");
            contentType.ThrowIfNull("contentType");

            this.Service = service;
            this.Path = path;
            this.HttpMethod = httpMethod;
            this.ContentStream = contentStream;
            this.ContentType = contentType;

            ChunkSize = DefaultChunkSize;
        }

        #endregion // Construction

        #region Properties

        /// <summary> Gets and sets the service. </summary>
        public IClientService Service { get; private set; }

        /// <summary> 
        /// Gets and sets the path of the method (combined with <see cref="Service.BaseUri"/>) to produce absolute Uri. 
        /// </summary>
        public string Path { get; private set; }

        /// <summary> Gets and sets the Http method of this upload (used to initialize the upload). </summary>
        public string HttpMethod { get; private set; }

        /// <summary> Gets and sets the stream to upload. </summary>
        public Stream ContentStream { get; private set; }

        /// <summary> Gets and sets the stream's Content-Type. </summary>
        public string ContentType { get; private set; }

        /// <summary>
        /// Gets and sets the length of the steam. Will be <see cref="UnknownSize" /> if the media content length is 
        /// unknown. 
        /// </summary>
        private long StreamLength { get; set; }

        /// <summary>
        /// Gets and sets the content of the last buffer request or <c>null</c> for none. It is used when the media 
        /// content length is unknown, for resending it in case of server error.
        /// </summary>
        private byte[] LastMediaRequest { get; set; }

        /// <summary> Gets and sets a cached byte which indicates if end of stream has been reached. </summary>
        private byte[] CachedByte { get; set; }

        /// <summary>
        /// Gets and sets the last request start index or <c>0</c> for none. It is used when the media content length 
        /// is unknown.
        /// </summary>
        private long LastMediaStartIndex { get; set; }

        /// <summary> Gets and sets The last request length. </summary>
        private int LastMediaLength { get; set; }

        /// <summary> 
        /// Gets and sets the resumable session Uri. 
        /// See https://developers.google.com/drive/manage-uploads#save-session-uri" for more details.
        /// </summary>
        private Uri UploadUri { get; set; }

        /// <summary> Gets and sets the amount of bytes sent so far. </summary>
        private long BytesSent { get; set; }

        /// <summary> Gets and sets the body of this request. </summary>
        public TRequest Body { get; set; }

        /// <summary> 
        /// Gets and sets the size of each chunk sent to the server.
        /// Chunks (except the last chunk) must be a multiple of <see cref="MinimumChunkSize"/> to be compatible with 
        /// Google upload servers.
        /// </summary>
        public int ChunkSize { get; set; }

        #endregion // Properties

        #region Events

        /// <summary> Event called whenever the progress of the upload changes. </summary>
        public event Action<IUploadProgress> ProgressChanged;

        #endregion //Events

        #region Error handling (Expcetion and 5xx)

        /// <summary>
        /// Callback class that is invoked on abnormal response or an exception.
        /// This class changes the request to query the current status of the upload in order to find how many bytes  
        /// were successfully uploaded before the error occurred.
        /// See https://developers.google.com/drive/manage-uploads#resume-upload for more details.
        /// </summary>
        class ServerErrorCallback : IHttpUnsuccessfulResponseHandler, IHttpExceptionHandler, IDisposable
        {
            private ResumableUpload<TRequest> Owner { get; set; }

            /// <summary> 
            /// Constructs a new callback and register it as unsuccessful response handler and exception handler on the 
            /// configurable message handler.
            /// </summary>
            public ServerErrorCallback(ResumableUpload<TRequest> resumable)
            {
                this.Owner = resumable;
                Owner.Service.HttpClient.MessageHandler.UnsuccessfulResponseHandlers.Add(this);
                Owner.Service.HttpClient.MessageHandler.ExceptionHandlers.Add(this);
            }

            public bool HandleResponse(HandleUnsuccessfulResponseArgs args)
            {
                var statusCode = (int)args.Response.StatusCode;
                // handle the error if and only if all the following conditions occur:
                // - there is going to be an actual retry
                // - the message request is for media upload with the current Uri (remember that the message handler
                //   can be invoked from other threads \ messages, so we should call server error callback only if the
                //   request is in the current context).
                // - we got a 5xx server error.
                if (args.SupportsRetry && args.Request.RequestUri.Equals(Owner.UploadUri) && statusCode / 100 == 5)
                {
                    return OnServerError(args.Request);
                }
                return false;
            }

            public bool HandleException(HandleExceptionArgs args)
            {
                return args.SupportsRetry && !args.CancellationToken.IsCancellationRequested &&
                    args.Request.RequestUri.Equals(Owner.UploadUri) ? OnServerError(args.Request) : false;
            }

            /// <summary> Changes the request in order to resume the interrupted upload. </summary>
            private bool OnServerError(HttpRequestMessage request)
            {
                // clear all headers and set Content-Range and Content-Length headers
                var range = String.Format("bytes */{0}", Owner.StreamLength < 0 ? "*" : Owner.StreamLength.ToString());
                request.Headers.Clear();
                request.Method = System.Net.Http.HttpMethod.Put;
                request.SetEmptyContent().Headers.Add("Content-Range", range);
                return true;
            }

            public void Dispose()
            {
                Owner.Service.HttpClient.MessageHandler.UnsuccessfulResponseHandlers.Remove(this);
                Owner.Service.HttpClient.MessageHandler.ExceptionHandlers.Remove(this);
            }
        }

        #endregion

        #region Progress Monitoring

        /// <summary> Class that communicates the progress of resumable uploads to a container. </summary>
        private class ResumableUploadProgress : IUploadProgress
        {
            /// <summary>
            /// Create a ResumableUploadProgress instance.
            /// </summary>
            /// <param name="status">The status of the upload.</param>
            /// <param name="bytesSent">The number of bytes sent so far.</param>
            public ResumableUploadProgress(UploadStatus status, long bytesSent)
            {
                Status = status;
                BytesSent = bytesSent;
            }

            /// <summary>
            /// Create a ResumableUploadProgress instance.
            /// </summary>
            /// <param name="exception">An exception that occurred during the upload.</param>
            /// <param name="bytesSent">The number of bytes sent before this exception occurred.</param>
            public ResumableUploadProgress(Exception exception, long bytesSent)
            {
                Status = UploadStatus.Failed;
                BytesSent = bytesSent;
                Exception = exception;
            }

            public UploadStatus Status { get; private set; }
            public long BytesSent { get; private set; }
            public Exception Exception { get; private set; }
        }

        /// <summary>
        /// Current state of progress of the upload.
        /// </summary>
        /// <seealso cref="ProgressChanged"/>
        private ResumableUploadProgress Progress { get; set; }

        /// <summary>
        /// Updates the current progress and call the <see cref="ProgressChanged"/> event to notify listeners.
        /// </summary>
        private void UpdateProgress(ResumableUploadProgress progress)
        {
            Progress = progress;
            if (ProgressChanged != null)
                ProgressChanged(progress);
        }

        /// <summary>
        /// Get the current progress state.
        /// </summary>
        /// <returns>An IUploadProgress describing the current progress of the upload.</returns>
        /// <seealso cref="ProgressChanged"/>
        public IUploadProgress GetProgress()
        {
            return Progress;
        }

        #endregion

        #region Upload Implementation

        /// <summary>
        /// Uploads the content to the server. This method is synchronous and will block until the upload is completed.
        /// </summary>
        /// <remarks>
        /// In case the upload fails the <seealso cref="IUploadProgress.Exception "/> will contain the exception that
        /// cause the failure.</remarks>
        public IUploadProgress Upload()
        {
            return Upload(CancellationToken.None).Result;
        }

        /// <summary>
        /// Uploads the content to the server using the given cancellation token. This method is used for both async 
        /// and sync operation.
        /// </summary>
        private async Task<IUploadProgress> Upload(CancellationToken cancellationToken)
        {
            try
            {
                BytesSent = 0;
                UpdateProgress(new ResumableUploadProgress(UploadStatus.Starting, 0));
                // check if the stream length is known
                StreamLength = ContentStream.CanSeek ? ContentStream.Length : UnknownSize;
                UploadUri = await InitializeUpload(cancellationToken).ConfigureAwait(false);

                logger.Debug("MediaUpload[{0}] - Start uploading...", UploadUri);

                using (var callback = new ServerErrorCallback(this))
                {
                    while (!await SendNextChunk(ContentStream, cancellationToken).ConfigureAwait(false))
                    {
                        UpdateProgress(new ResumableUploadProgress(UploadStatus.Uploading, BytesSent));
                    }
                    UpdateProgress(new ResumableUploadProgress(UploadStatus.Completed, BytesSent));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "MediaUpload[{0}] - Exception occurred while uploading media", UploadUri);
                UpdateProgress(new ResumableUploadProgress(ex, BytesSent));
            }

            return Progress;
        }

        /// <summary> Uploads the content asynchronously to the server.</summary>
        /// <remarks>
        /// In case the upload fails the task will not be completed. In that case the task's 
        /// <seealso cref="System.Threading.Tasks.Task.Exception"/> property will bet set, or its 
        /// <seealso cref="System.Threading.Tasks.Task.IsCanceled"/> property will be true.
        /// </remarks>
        public Task<IUploadProgress> UploadAsync()
        {
            return UploadAsync(CancellationToken.None);
        }

        /// <summary> Uploads the content asynchronously to the server.</summary>
        /// <param name="cancellationToken">The cancellation token to cancel a request in the middle.</param>
        public Task<IUploadProgress> UploadAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<IUploadProgress> tcs = new TaskCompletionSource<IUploadProgress>();
            Task.Factory.StartNew(async () =>
            {
                try
                {
                    var response = await Upload(cancellationToken).ConfigureAwait(false);
                    if (response.Exception != null)
                    {
                        tcs.SetException(response.Exception);
                    }
                    else
                    {
                        tcs.SetResult(response);
                    }
                }
                catch (Exception ex)
                {
                    // exception was thrown - it must be set on the task completion source
                    tcs.SetException(ex);
                }
            }).ConfigureAwait(false);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the resumable upload by calling the resumable rest interface to get a unique upload location.
        /// See https://developers.google.com/drive/manage-uploads#start-resumable for more details.
        /// </summary>
        /// <returns>
        /// The unique upload location for this upload, returned in the Location header
        /// </returns>
        private async Task<Uri> InitializeUpload(CancellationToken cancellationToken)
        {
            HttpRequestMessage request = CreateInitializeRequest();
            var response = await Service.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.EnsureSuccessStatusCode().Headers.Location;
        }

        /// <summary>
        /// Process a response from the final upload chunk call.
        /// </summary>
        /// <param name="httpResponse">The response body from the final uploaded chunk.</param>
        protected virtual void ProcessResponse(HttpResponseMessage httpResponse)
        {
        }

        /// <summary> 
        /// Uploads the next chunk of data to the server.
        /// </summary>
        /// <returns> 
        /// <c>True</c> if the entire media has been completely uploaded.
        /// </returns>
        protected async Task<bool> SendNextChunk(Stream stream, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpRequestMessage request = new RequestBuilder()
                {
                    BaseUri = UploadUri,
                    Method = HttpConsts.Put
                }.CreateRequest();

            // prepare next chunk to send
            if (StreamLength != UnknownSize)
            {
                PrepareNextChunkKnownSize(request, stream, cancellationToken);
            }
            else
            {
                PrepareNextChunkUnknownSize(request, stream, cancellationToken);
            }

            HttpResponseMessage response = await Service.HttpClient.SendAsync(
                request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                MediaCompleted(response);
                return true;
            }
            else if (response.StatusCode == (HttpStatusCode)308)
            {
                // The upload protocol uses 308 to indicate that there is more data expected from the server.
                BytesSent = GetNextByte(response.Headers.GetValues("Range").First());
                logger.Debug("MediaUpload[{0}] - {1} Bytes were sent successfully", UploadUri, BytesSent);
                return false;
            }

            var error = await Service.DeserializeError(response).ConfigureAwait(false);
            throw new GoogleApiException(Service.Name, error.ToString());
        }

        /// <summary> A callback when the media was uploaded successfully. </summary>
        private void MediaCompleted(HttpResponseMessage response)
        {
            logger.Debug("MediaUpload[{0}] - media was uploaded successfully", UploadUri);
            ProcessResponse(response);
            BytesSent += LastMediaLength;

            // clear the last request byte array
            LastMediaRequest = null;
        }

        /// <summary> Prepares the given request with the next chunk in case the steam length is unknown. </summary>
        private void PrepareNextChunkUnknownSize(HttpRequestMessage request, Stream stream,
            CancellationToken cancellationToken)
        {
            // We save the current request, so we would be able to resend those bytes in case of a server error
            if (LastMediaRequest == null)
            {
                LastMediaRequest = new byte[ChunkSize];
            }

            // if the number of bytes received by the sever isn't equal to the sum of saved start index plus 
            // length, it means that we need to resend bytes from the last request
            if (BytesSent != LastMediaStartIndex + LastMediaLength)
            {
                int delta = (int)(BytesSent - LastMediaStartIndex);
                Buffer.BlockCopy(LastMediaRequest, delta, LastMediaRequest, 0, ChunkSize - delta);
                LastMediaLength = ChunkSize - delta;
            }
            else
            {
                LastMediaStartIndex = BytesSent;
                LastMediaLength = 0;
            }

            bool shouldRead = true;
            if (CachedByte == null)
            {
                // create a new cached byte which will be used to verify if we reached the end of stream
                CachedByte = new byte[1];
            }
            else if (LastMediaLength != ChunkSize)
            {
                // read the last cached byte, and add it to the current request
                LastMediaRequest[LastMediaLength] = CachedByte[0];
                LastMediaLength++;
            }
            else
            {
                // the whole bytes from last request should be resent, no need to read data from stream in this request
                // and no need to update the cached byte
                shouldRead = false;
            }

            if (shouldRead)
            {
                int len = 0;
                // read bytes form the stream to lastMediaRequest byte array
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    len = stream.Read(LastMediaRequest, LastMediaLength,
                        (int)Math.Min(BufferSize, ChunkSize - LastMediaLength));
                    LastMediaLength += len;
                    if (len == 0) break;
                }

                // check if there is still data to read from stream, and cache the first byte in catchedByte
                if (0 == stream.Read(CachedByte, 0, 1))
                {
                    // EOF - now we know the stream's length
                    StreamLength = LastMediaLength + BytesSent;
                    CachedByte = null;
                }
            }

            // set Content-Length and Content-Range
            var byteArrayContent = new ByteArrayContent(LastMediaRequest, 0, LastMediaLength);
            byteArrayContent.Headers.Add("Content-Range", GetContentRangeHeader(BytesSent, LastMediaLength));
            request.Content = byteArrayContent;
        }

        /// <summary> Prepares the given request with the next chunk in case the steam length is known. </summary>
        private void PrepareNextChunkKnownSize(HttpRequestMessage request, Stream stream,
            CancellationToken cancellationToken)
        {
            int chunkSize = (int)Math.Min(StreamLength - BytesSent, (long)ChunkSize);

            // stream length is known and it supports seek and position operations.
            // We can change the stream position and read bytes from the last point
            byte[] buffer = new byte[Math.Min(chunkSize, BufferSize)];
            stream.Position = BytesSent;

            MemoryStream ms = new MemoryStream(chunkSize);
            int bytesRead = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // read from input stream and write to output stream
                // TODO(peleyal): write a utility similar to (.NET 4 Stream.CopyTo method)
                int len = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, chunkSize - bytesRead));
                if (len == 0) break;
                ms.Write(buffer, 0, len);
                bytesRead += len;
            }

            // set the stream position to beginning and wrap it with stream content
            ms.Position = 0;
            request.Content = new StreamContent(ms);
            request.Content.Headers.Add("Content-Range", GetContentRangeHeader(BytesSent, chunkSize));

            LastMediaLength = chunkSize;
        }

        /// <summary> Returns the next byte index need to be sent. </summary>
        private long GetNextByte(string range)
        {
            return long.Parse(range.Substring(range.IndexOf('-') + 1)) + 1;
        }

        /// <summary>
        /// Build a content range header of the form: "bytes X-Y/T" where:
        /// <list type="">
        /// <item>X is the first byte being sent.</item>
        /// <item>Y is the last byte in the range being sent (inclusive).</item>
        /// <item>T is the total number of bytes in the range or * for unknown size.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// See: RFC2616 HTTP/1.1, Section 14.16 Header Field Definitions, Content-Range
        /// http://www.w3.org/Protocols/rfc2616/rfc2616-sec14.html#sec14.16
        /// </remarks>
        /// <param name="chunkStart">Start of the chunk.</param>
        /// <param name="chunkSize">Size of the chunk being sent.</param>
        /// <returns>The content range header value.</returns>
        private string GetContentRangeHeader(long chunkStart, long chunkSize)
        {
            string strLength = StreamLength < 0 ? "*" : StreamLength.ToString();

            // If a file of length 0 is sent, one chunk needs to be sent with 0 size.
            // This chunk cannot be specified with the standard (inclusive) range header.
            // In this case, use * to indicate no bytes sent in the Content-Range header.
            if (chunkStart == 0 && chunkSize == 0 && StreamLength == 0)
            {
                return ZeroByteContentRangeHeader;
            }
            else
            {
                long chunkEnd = chunkStart + chunkSize - 1;
                return String.Format("bytes {0}-{1}/{2}", chunkStart, chunkEnd, strLength);
            }
        }

        /// <summary> Creates a request to initialize a request. </summary>
        private HttpRequestMessage CreateInitializeRequest()
        {
            var builder = new RequestBuilder()
            {
                BaseUri = new Uri(Service.BaseUri),
                Path = Path,
                Method = HttpMethod,
            };

            // init parameters
            builder.AddParameter(RequestParameterType.Query, "key", Service.ApiKey);
            builder.AddParameter(RequestParameterType.Query, "uploadType", "resumable");
            SetAllPropertyValues(builder);

            HttpRequestMessage request = builder.CreateRequest();
            request.Headers.Add(PayloadContentTypeHeader, ContentType);

            // if the length is unknown at the time of this request, omit "X-Upload-Content-Length" header
            if (StreamLength != UnknownSize)
            {
                request.Headers.Add(PayloadContentLengthHeader, StreamLength.ToString());
            }

            Service.SetRequestSerailizedContent(request, Body);
            return request;
        }

        /// <summary>
        /// Reflectively enumerate the properties of this object looking for all properties containing the 
        /// RequestParameterAttribute and copy their values into the request builder.
        /// </summary>
        private void SetAllPropertyValues(RequestBuilder requestBuilder)
        {
            Type myType = this.GetType();
            var properties = myType.GetProperties();

            foreach (var property in properties)
            {
                var attribute = property.GetCustomAttribute<Google.Apis.Util.RequestParameterAttribute>();

                if (attribute != null)
                {
                    string name = attribute.Name ?? property.Name.ToLower();
                    object value = property.GetValue(this, new object[] { });
                    if (value != null)
                    {
                        requestBuilder.AddParameter(attribute.Type, name, value.ToString());
                    }
                }
            }
        }

        #endregion Upload Implementation
    }

    /// <summary>
    /// Media upload which uses Google's resumable media upload protocol to upload data.
    /// The version with two types contains both a request object and a response object.
    /// </summary>
    /// <remarks>
    /// See: http://code.google.com/apis/gdata/docs/resumable_upload.html for
    /// information on the protocol.
    /// </remarks>
    /// <typeparam name="TRequest">
    /// The type of the body of this request. Generally this should be the metadata related 
    /// to the content to be uploaded. Must be serializable to/from JSON.
    /// </typeparam>
    /// <typeparam name="TResponse">
    /// The type of the response body.
    /// </typeparam>
    public class ResumableUpload<TRequest, TResponse> : ResumableUpload<TRequest>
    {
        #region Construction

        /// <summary>
        /// Create a resumable upload instance with the required parameters.
        /// </summary>
        /// <param name="service">The client service.</param>
        /// <param name="path">The path for this media upload method.</param>
        /// <param name="httpMethod">The Http method to start this upload.</param>
        /// <param name="contentStream">The stream containing the content to upload.</param>
        /// <param name="contentType">Content type of the content to be uploaded.</param>
        /// <remarks>
        /// The stream <paramref name="contentStream"/> must support the "Length" property.
        /// Caller is responsible for maintaining the <paramref name="contentStream"/> open until the 
        /// upload is completed.
        /// Caller is responsible for closing the <paramref name="contentStream"/>.
        /// </remarks>
        protected ResumableUpload(IClientService service, string path, string httpMethod,
            Stream contentStream, string contentType)
            : base(service, path, httpMethod, contentStream, contentType) { }

        #endregion // Construction

        #region Properties

        /// <summary>
        /// The response body.
        /// </summary>
        /// <remarks>
        /// This property will be set during upload. The <see cref="ResponseReceived"/> event
        /// is triggered when this has been set.
        /// </remarks>
        public TResponse ResponseBody { get; private set; }

        #endregion // Properties

        #region Events

        /// <summary> Event which is called when the response metadata is processed. </summary>
        public event Action<TResponse> ResponseReceived;

        #endregion // Events

        #region Overrides

        /// <summary> Process the response body </summary>
        protected override void ProcessResponse(HttpResponseMessage response)
        {
            base.ProcessResponse(response);
            ResponseBody = Service.DeserializeResponse<TResponse>(response).Result;

            if (ResponseReceived != null)
                ResponseReceived(ResponseBody);
        }

        #endregion // Overrides
    }
}