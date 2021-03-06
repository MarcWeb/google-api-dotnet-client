﻿/*
Copyright 2013 Google Inc

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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

using Ionic.Zlib;
using Newtonsoft.Json;

using Google.Apis.Authentication;
using Google.Apis.Discovery;
using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Logging;
using Google.Apis.Requests;
using Google.Apis.Util;

namespace Google.Apis.Services
{
    /// <summary>
    /// A thread-safe base class for a client service which provides common mechanism for all services, like 
    /// serialization and GZip support.
    /// This class adds a special <see cref="Google.Apis.Http.IHttpExecuteInterceptor"/> to the 
    /// <see cref="Google.Apis.Http.ConfigurableMessageHandler"/> execute interceptor list, which uses the given 
    /// Authenticator. It calls to its applying authentication method, and injects the "Authorization" header in the 
    /// request.
    /// If the given Authenticator implements <see cref="Google.Apis.Http.IUnsuccessfulReponseHandler"/>, this class
    /// adds the Authenticator to the <see cref="Google.Apis.Http.ConfigurableMessageHandler"/>'s unsuccessful response
    /// handler list.
    /// </summary>
    public abstract class BaseClientService : IClientService
    {
        /// <summary> The class logger. </summary>
        private static readonly ILogger Logger = ApplicationContext.Logger.ForType<BaseClientService>();

        #region Initializer

        /// <summary> 
        /// Indicates if exponential back-off is used automatically on exception in a service request and\or when 5xx 
        /// response is returned form the server.
        /// </summary>
        [Flags]
        public enum ExponentialBackOffPolicy
        {
            None = 0,
            Exception = 1,
            UnsuccessfulResponse5xx = 2
        }

        /// <summary> An initializer class for the client service. </summary>
        public class Initializer
        {
            /// <summary> 
            /// A factory for creating <see cref="System.Net.Http.HttpClient"/> instance. If this property is not set
            /// the service uses a new <see cref="Google.Apis.Http.HttpClientFactory"/> instance.
            /// </summary>
            public IHttpClientFactory HttpClientFactory { get; set; }

            /// <summary>
            /// An Http client initializer which is able to customize properties on 
            /// <see cref="Google.Apis.Http.ConfigurableHttpClient"/> and 
            /// <see cref="Google.Apis.Http.ConfigurableMessageHandler"/>.
            /// </summary>
            public IConfigurableHttpClientInitializer HttpClientInitializer { get; set; }

            /// <summary>
            /// Get or sets the exponential back-off policy used by the service. Default value is <c>Exception</c> |
            /// <c>UnsuccessfulResponse5xx</c>, which means that exponential back-off is used on any 5xx abnormal Http
            /// response and on any exception whose thrown when sending a request (except task canceled exception).
            /// If the value is set to <c>None</c>, no exponential back-off policy is used, and it's up to user to
            /// configure the <seealso cref="Google.Apis.Http.ConfigurableMessageHandler"/> in an
            /// <seealso cref="Google.Apis.Http.IConfigurableHttpClientInitializer"/> to set a specific back-off
            /// implementation (using <seealso cref="Google.Api.Http.BackOffHandler"/>).
            /// </summary>
            public ExponentialBackOffPolicy DefaultExponentialBackOffPolicy { get; set; }

            /// <summary> Gets and Sets whether this service supports GZip. Default value is <c>true</c>. </summary>
            public bool GZipEnabled { get; set; }

            /// <summary>
            /// Gets and Sets the Serializer. Default value is <see cref="Google.Apis.Json.NewtonsoftJsonSerializer"/>.
            /// </summary>
            public ISerializer Serializer { get; set; }

            /// <summary> Gets and Sets the API Key. Default value is <c>null</c>. </summary>
            public string ApiKey { get; set; }

            /// <summary> 
            /// Gets and Sets the Authenticator. Default value is 
            /// <see cref="Google.Apis.Authentication.NullAuthenticator.Instance"/>.
            /// </summary>
            public IAuthenticator Authenticator { get; set; }

            /// <summary> 
            /// Gets and sets Application name to be used in the User-Agent header. Default value is <c>null</c>. 
            /// </summary>
            public string ApplicationName { get; set; }

            /// <summary> Constructs a new initializer with default values. </summary>
            public Initializer()
            {
                GZipEnabled = true;
                Serializer = new NewtonsoftJsonSerializer();
                Authenticator = NullAuthenticator.Instance;
                DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception |
                    ExponentialBackOffPolicy.UnsuccessfulResponse5xx;
            }
        }

        /// <summary>
        /// An initializer which adds exponential back-off as exception handler and\or unsuccessful response handler by
        /// the given <seealso cref="BaseClientService.ExponentialBackOffPolicy"/>.
        /// </summary>
        private class ExponentialBackOffInitializer : IConfigurableHttpClientInitializer
        {
            private ExponentialBackOffPolicy Policy { get; set; }
            private Func<BackOffHandler> CreateBackOff { get; set; }

            /// <summary>
            /// Constructs a new back-off initializer with the given policy and back-off handler create function.
            /// </summary>
            public ExponentialBackOffInitializer(ExponentialBackOffPolicy policy, Func<BackOffHandler> createBackOff)
            {
                Policy = policy;
                CreateBackOff = createBackOff;
            }

            public void Initialize(ConfigurableHttpClient httpClient)
            {
                var backOff = CreateBackOff();

                // add exception handler and\or unsuccessful response handler
                if ((Policy & ExponentialBackOffPolicy.Exception) == ExponentialBackOffPolicy.Exception)
                {
                    httpClient.MessageHandler.ExceptionHandlers.Add(backOff);
                }

                if ((Policy & ExponentialBackOffPolicy.UnsuccessfulResponse5xx) ==
                    ExponentialBackOffPolicy.UnsuccessfulResponse5xx)
                {
                    httpClient.MessageHandler.UnsuccessfulResponseHandlers.Add(backOff);
                }
            }
        }

        #endregion

        /// <summary> Constructs a new base client with the specified initializer. </summary>
        protected BaseClientService(Initializer initializer)
        {
            // sets the right properties by the initializer's properties
            GZipEnabled = initializer.GZipEnabled;
            Serializer = initializer.Serializer;
            ApiKey = initializer.ApiKey;
            Authenticator = initializer.Authenticator;
            ApplicationName = initializer.ApplicationName;
            if (ApplicationName == null)
            {
                Logger.Warning("Application name is not set. Please set Initializer.ApplicationName property");
            }
            HttpClientInitializer = initializer.HttpClientInitializer;

            // create an Http client for this service
            HttpClient = CreateHttpClient(initializer);
        }

        /// <summary>
        /// Return true if this service contains the specified feature.
        /// </summary>
        private bool HasFeature(Features feature)
        {
            return Features.Contains(feature.GetStringValue());
        }

        private ConfigurableHttpClient CreateHttpClient(Initializer initializer)
        {
            // if factory wasn't set use the default Http client factory
            var factory = initializer.HttpClientFactory ?? new HttpClientFactory();
            var args = new CreateHttpClientArgs
                {
                    GZipEnabled = GZipEnabled,
                    ApplicationName = ApplicationName,
                };

            // add the user's input initializer
            if (HttpClientInitializer != null)
            {
                args.Initializers.Add(HttpClientInitializer);
            }

            // add exponential back-off initializer if necessary
            if (initializer.DefaultExponentialBackOffPolicy != ExponentialBackOffPolicy.None)
            {
                args.Initializers.Add(new ExponentialBackOffInitializer(initializer.DefaultExponentialBackOffPolicy,
                    CreateBackOffHandler));
            }

            // add authenticator initializer to intercept a request and add the "Authorization" header and also handle
            // abnormal 401 responses in case the authenticator is an instance of unsuccessful response handler.
            args.Initializers.Add(new AuthenticatorMessageHandlerInitializer(Authenticator));

            return factory.CreateHttpClient(args);
        }

        /// <summary>
        /// Creates the back-off handler with <seealso cref="Google.Apis.Util.ExponentialBackOff"/>. 
        /// Overrides this method to change the default behavior of back-off handler (e.g. you can change the maximum
        /// waited request's time span, or create a back-off handler with you own implementation of 
        /// <seealso cref="Google.Apis.Util.IBackOff"/>).
        /// </summary>
        protected virtual BackOffHandler CreateBackOffHandler()
        {
            // TODO(peleyal): consider return here interface and not the concrete class
            return new BackOffHandler(new ExponentialBackOff());
        }

        #region IClientService Members

        public ConfigurableHttpClient HttpClient { get; private set; }

        public IConfigurableHttpClientInitializer HttpClientInitializer { get; private set; }

        public bool GZipEnabled { get; private set; }

        public string ApiKey { get; private set; }

        public IAuthenticator Authenticator { get; private set; }

        public string ApplicationName { get; private set; }

        public void SetRequestSerailizedContent(HttpRequestMessage request, object body)
        {
            if (body == null)
            {
                return;
            }

            HttpContent content = null;

            var mediaType = "application/" + Serializer.Format;
            var serializedObject = SerializeObject(body);
            if (GZipEnabled)
            {
                var stream = CreateGZipStream(serializedObject);
                content = new StreamContent(stream);
                content.Headers.ContentEncoding.Add("gzip");
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType)
                    {
                        CharSet = Encoding.UTF8.WebName
                    };
            }
            else
            {
                content = new StringContent(serializedObject, Encoding.UTF8, mediaType);
            }

            request.Content = content;
        }

        #region Serialization

        public ISerializer Serializer { get; private set; }

        public virtual string SerializeObject(object obj)
        {
            if (HasFeature(Discovery.Features.LegacyDataResponse))
            {
                // Legacy path
                var request = new StandardResponse<object> { Data = obj };
                return Serializer.Serialize(request);
            }

            // New v1.0 path
            return Serializer.Serialize(obj);
        }

        public virtual async Task<T> DeserializeResponse<T>(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // If a string is request, don't parse the response.
            if (typeof(T).Equals(typeof(string)))
            {
                return (T)(object)text;
            }

            // Check if there was an error returned. The error node is returned in both paths
            // Deserialize the stream based upon the format of the stream.
            if (HasFeature(Discovery.Features.LegacyDataResponse))
            {
                // Legacy path (deprecated!)
                StandardResponse<T> sr = null;
                try
                {
                    sr = Serializer.Deserialize<StandardResponse<T>>(text);
                }
                catch (JsonReaderException ex)
                {
                    throw new GoogleApiException(Name,
                        "Failed to parse response from server as json [" + text + "]", ex);
                }

                if (sr.Error != null)
                {
                    throw new GoogleApiException(Name, "Server error - " + sr.Error);
                }

                if (sr.Data == null)
                {
                    throw new GoogleApiException(Name, "The response could not be deserialized.");
                }
                return sr.Data;
            }

            // New path: Deserialize the object directly.
            T result = default(T);
            try
            {
                result = Serializer.Deserialize<T>(text);
            }
            catch (JsonReaderException ex)
            {
                throw new GoogleApiException(Name, "Failed to parse response from server as json [" + text + "]", ex);
            }

            // TODO(peleyal): is this the right place to check ETag? it isn't part of deserialization!
            // If this schema/object provides an error container, check it.
            var eTag = response.Headers.ETag != null ? response.Headers.ETag.Tag : null;
            if (result is IDirectResponseSchema && eTag != null)
            {
                (result as IDirectResponseSchema).ETag = eTag;
            }
            return result;
        }

        public virtual async Task<RequestError> DeserializeError(HttpResponseMessage response)
        {
            StandardResponse<object> errorResponse = null;
            try
            {
                var str = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                errorResponse = Serializer.Deserialize<StandardResponse<object>>(str);
                if (errorResponse.Error == null)
                {
                    throw new GoogleApiException(Name,
                        "An Error occurred, but the error response could not be deserialized");
                }
            }
            catch (Exception ex)
            {
                // exception will be thrown in case the response content is empty or it can't be deserialized to 
                // Standard response (which contains data and error properties)
                throw new GoogleApiException(Name,
                    "An Error occurred, but the error response could not be deserialized", ex);
            }

            return errorResponse.Error;
        }

        #endregion

        #region Abstract Memebrs

        public abstract string Name { get; }
        public abstract string BaseUri { get; }
        public abstract string BasePath { get; }

        public abstract IList<string> Features { get; }
        public abstract IDictionary<string, IParameter> ServiceParameters { get; }

        #endregion

        #endregion

        /// <summary> Creates a GZip stream by the given serialized object. </summary>
        private static Stream CreateGZipStream(string serializedObject)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(serializedObject);
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }

                // reset the stream to the beginning. It doesn't work otherwise!
                ms.Position = 0;
                byte[] compressed = new byte[ms.Length];
                ms.Read(compressed, 0, compressed.Length);
                return new MemoryStream(compressed);
            }
        }

        public virtual void Dispose()
        {
            if (HttpClient != null)
            {
                HttpClient.Dispose();
            }
        }
    }
}
