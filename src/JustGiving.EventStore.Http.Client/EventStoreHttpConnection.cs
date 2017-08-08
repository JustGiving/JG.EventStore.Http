﻿using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using JustGiving.EventStore.Http.Client.Common.Utils;
using JustGiving.EventStore.Http.Client.Exceptions;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JustGiving.EventStore.Http.Client
{
    /// <summary>
    /// EventStore Http connection.
    /// <para>
    /// As this connection is backed by <see cref="HttpClient"/>, clients are subject to connection management per <see cref="ServicePointManager"/>.
    /// See https://msdn.microsoft.com/en-us/library/system.net.servicepoint%28v=vs.110%29.aspx for details on configuration options.
    /// </para>
    /// </summary>
    /// <remarks>Clients of instances of this type should dispose of it, to release any resources it may have in use.</remarks>
    public class EventStoreHttpConnection : IEventStoreHttpConnection, IDisposable
    {
        private readonly ConnectionSettings _settings;
        private readonly IHttpClientProxy _httpClientProxy;
        private readonly ILog _log;
        private readonly string _endpoint;
        private readonly string _connectionName;
        private readonly Action<IEventStoreHttpConnection, Exception> _errorHandler;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Creates a new <see cref="IEventStoreHttpConnection"/> to single node using default <see cref="ConnectionSettings"/>
        /// </summary>
        /// <param name="connectionSettings">The <see cref="ConnectionSettings"/> to apply to the new connection</param>
        /// <param name="endpoint">The endpoint to connect to.</param>
        /// <returns>a new <see cref="IEventStoreHttpConnection"/></returns>
        public static EventStoreHttpConnection Create(ConnectionSettings connectionSettings, string endpoint)
        {
            return new EventStoreHttpConnection(connectionSettings, endpoint);
        }

        /// <summary>
        /// Creates a new <see cref="IEventStoreHttpConnection"/> to single node using default <see cref="ConnectionSettings"/>
        /// </summary>
        /// <param name="endpoint">The endpoint to connect to.</param>
        /// <returns>a new <see cref="IEventStoreHttpConnection"/></returns>
        public static EventStoreHttpConnection Create(string endpoint)
        {
            return new EventStoreHttpConnection(ConnectionSettings.Default, endpoint);
        }

        /// <summary>
        /// Creates a new <see cref="IEventStoreHttpConnection"/> to single node using specific <see cref="ConnectionSettings"/>
        /// </summary>
        /// <param name="settings">The <see cref="ConnectionSettings"/> to apply to the new connection</param>
        /// <param name="endpoint">The endpoint to connect to.</param>
        /// <returns>a new <see cref="IEventStoreHttpConnection"/></returns>
        internal EventStoreHttpConnection(ConnectionSettings settings, string endpoint)
        {
            Ensure.NotNull(settings, "settings");
            Ensure.NotNull(endpoint, "endpoint");

            _httpClientProxy = settings.HttpClientProxy;
            _settings = settings;
            _log = settings.Log;
            _endpoint = endpoint;
            _errorHandler = settings.ErrorHandler;
            _connectionName = settings.ConnectionName;
            _httpClient = GetClient();
        }

        public string ConnectionName => _connectionName;

        public string Endpoint => _endpoint;

        public async Task DeleteStreamAsync(string stream, int expectedVersion)
        {
            await DeleteStreamAsync(stream, expectedVersion, false).ConfigureAwait(false);
        }

        public async Task DeleteStreamAsync(string stream, int expectedVersion, bool hardDelete)
        {
            Log.Info(_log, "Deleting stream {0} (hard={1})", stream, hardDelete);
            using (var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint + "/streams/" + stream))
            {
                request.Headers.Add("ES-ExpectedVersion", expectedVersion.ToString());

                if (hardDelete)
                {
                    request.Headers.Add("ES-HardDelete", "true");
                }

                var result = await _httpClientProxy.SendAsync(_httpClient, request).ConfigureAwait(false);

                if (!result.IsSuccessStatusCode)
                {
                    Log.Error(_log, "Error deleting stream {0} (hard={1}, expectedVersion={2})", stream, hardDelete, expectedVersion);
                    throw new EventStoreHttpException(result.Content.ToString(), result.ReasonPhrase, result.StatusCode);
                }
            }
        }

        public async Task AppendToStreamAsync<T>(string stream, params T[] events)
        {
            await AppendToStreamAsync(stream, ExpectedVersion.Any, events.Select(x => NewEventData.Create(x)).ToArray()).ConfigureAwait(false);
        }

        public async Task AppendToStreamAsync<T>(string stream, object metadata, T @event)
        {
            await AppendToStreamAsync(stream, ExpectedVersion.Any, NewEventData.Create(@event, metadata)).ConfigureAwait(false);
        }

        public async Task AppendToStreamAsync(string stream, params NewEventData[] events)
        {
            await AppendToStreamAsync(stream, ExpectedVersion.Any, events).ConfigureAwait(false);
        }

        public async Task AppendToStreamAsync(string stream, int expectedVersion, params NewEventData[] events)
        {
            var url = _endpoint + "/streams/" + stream;
            Log.Info(_log, "Appending {0} events to {1}", events?.Length ?? 0, stream);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(JsonConvert.SerializeObject(events), Encoding.UTF8, "application/vnd.eventstore.events+json");
                request.Headers.Add("ES-ExpectedVersion", expectedVersion.ToString());
                var result = await _httpClientProxy.SendAsync(_httpClient, request).ConfigureAwait(false);

                if (!result.IsSuccessStatusCode)
                {
                    Log.Error(_log, "Error appending {0} events to {1}", events?.Length ?? 0, url);
                    throw new EventStoreHttpException(result.Content.ToString(), result.ReasonPhrase, result.StatusCode);
                }
            }
        }

        public async Task<EventReadResult> ReadEventAsync(string stream, int position)
        {
            var url = GetCanonicalURIFor(stream, position);
            return await ReadEventAsync(url).ConfigureAwait(false);
        }

        public async Task<EventReadResult> ReadEventAsync(string url)
        {
            Log.Info(_log, "Reading event from {0}", url);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.eventstore.atom+json"));
                var result = await _httpClientProxy.SendAsync(_httpClient, request).ConfigureAwait(false);

                if (result.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Warning(_log, "Read Event: Not Found {0}", url);
                    return new EventReadResult(EventReadStatus.NotFound, null);
                }

                if (result.StatusCode == HttpStatusCode.Gone)
                {
                    Log.Warning(_log, "Read Event: Gone: {0}", url);
                    return new EventReadResult(EventReadStatus.StreamDeleted, null);
                }

                if (!result.IsSuccessStatusCode)
                {
                    Log.Error(_log, "Read Event: Other Error ({0}): {1}", result.StatusCode.ToString(), url);
                    throw new EventStoreHttpException(result.Content.ToString(), result.ReasonPhrase, result.StatusCode);
                }

                try
                {
                    var content = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var eventInfo = JsonConvert.DeserializeObject<EventInfo>(content);

                    return new EventReadResult(EventReadStatus.Success, eventInfo);
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Log.Error(_log, ex, "Error deserialising content from {0}", url);
                    throw;
                }
            }
        }

        public async Task<JObject> ReadEventBodyAsync(string stream, int eventNumber)
        {
            var url = GetCanonicalURIFor(stream, eventNumber);
            return await ReadEventBodyAsync(url).ConfigureAwait(false);
        }

        public async Task<JObject> ReadEventBodyAsync(string url)
        {
            Log.Info(_log, "Reading event body from {0}", url);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Add("accept", "application/json");
                var result = await _httpClientProxy.SendAsync(_httpClient, request).ConfigureAwait(false);

                if (result.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Warning(_log, "Read Event: Not Found {0}", url);
                    throw new EventNotFoundException(url, result.StatusCode, result.Content.ToString());
                }

                if (result.StatusCode == HttpStatusCode.Gone)
                {
                    Log.Warning(_log, "Read Event: Gone: {0}", url);
                    return null;
                }

                if (!result.IsSuccessStatusCode)
                {
                    Log.Error(_log, "Read Event: Other Error ({0}): {1}", result.StatusCode.ToString(), url);
                    throw new EventStoreHttpException(result.Content.ToString(), result.ReasonPhrase, result.StatusCode);
                }

                try
                {
                    var content = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(content))
                    {
                        return null;
                    }
                    return JObject.Parse(content);
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Log.Error(_log, ex, "Error deserialising content from {0}", url);
                    throw;
                }
            }
        }

        public async Task<T> ReadEventBodyAsync<T>(string stream, int eventNumber) where T: class
        {
            var url = GetCanonicalURIFor(stream, eventNumber);
            return await ReadEventBodyAsync<T>(url).ConfigureAwait(false);
        }

        public async Task<object> ReadEventBodyAsync(Type eventType, string stream, int eventNumber)
        {
            var url = GetCanonicalURIFor(stream, eventNumber);
            var intermediary = await ReadEventBodyAsync(url).ConfigureAwait(false);

            return intermediary?.ToObject(eventType);
        }

        public async Task<T> ReadEventBodyAsync<T>(string url) where T: class
        {
            var intermediary = await ReadEventBodyAsync(url).ConfigureAwait(false);
            return intermediary?.ToObject<T>();
        }

        public async Task<object> ReadEventBodyAsync(Type eventType, string url)
        {
            var intermediary = await ReadEventBodyAsync(url).ConfigureAwait(false);
            return intermediary?.ToObject(eventType);
        }

        public async Task<StreamEventsSlice> ReadStreamEventsForwardAsync(string stream, int start, int count, TimeSpan? longPollingTimeout)
        {
            return await ReadStreamEventsAsync(stream, start, count, longPollingTimeout, "forward");
        }

        public async Task<StreamEventsSlice> ReadStreamEventsBackwardAsync(string stream, int start, int count, TimeSpan? longPollingTimeout)
        {
            return await ReadStreamEventsAsync(stream, start, count, longPollingTimeout, "backward");
        }

        private async Task<StreamEventsSlice> ReadStreamEventsAsync(string stream, int start, int count, TimeSpan? longPollingTimeout, string direction)
        {
            var url = string.Concat(_endpoint, "/streams/", stream, "/", start, "/", direction, "/", count, "?embed=rich");

            Log.Debug(_log, "Reading {0}s from {1}", direction, url);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.eventstore.atom+json"));
                if (longPollingTimeout.HasValue && longPollingTimeout.Value.TotalSeconds >= 1)
                {
                    request.Headers.Add("ES-LongPoll", longPollingTimeout.Value.TotalSeconds.ToString());
                }

                try
                {
                    var result = await _httpClientProxy.SendAsync(_httpClient, request).ConfigureAwait(false);

                    if (result.StatusCode == HttpStatusCode.NotFound)
                    {
                        Log.Warning(_log, "Event slice not found: {0}", url);
                        return StreamEventsSlice.StreamNotFound();
                    }

                    if (result.StatusCode == HttpStatusCode.Gone)
                    {
                        Log.Warning(_log, "Event slice gone: {0}", url);
                        return StreamEventsSlice.StreamDeleted();
                    }

                    if (!result.IsSuccessStatusCode)
                    {
                        Log.Warning(_log, "Event slice: other error ({0}): {1}", result.StatusCode.ToString(), url);

                        var message = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new EventStoreHttpException(message, result.ReasonPhrase, result.StatusCode);
                    }

                    var content = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var eventInfo = JsonConvert.DeserializeObject<StreamEventsSlice>(content);
                    eventInfo.Status = StreamReadStatus.Success;
                    eventInfo.Entries.Reverse(); //atom lists things backwards

                    return eventInfo;
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    throw;
                }
            }
        }

        public string GetCanonicalURIFor(string stream, int position)
        {
            var url = string.Concat(_endpoint, "/streams/", stream, "/", position == StreamPosition.End ? "head" : position.ToString());
            return url;
        }

        /// <summary>
        /// Create a new <see cref="HttpClient"/> with the configured timeout.
        /// </summary>
        public HttpClient GetClient()
        {
            var handler = GetHandler();

            var client = new HttpClient(handler, disposeHandler: true);

            if (_settings.ConnectionTimeout.HasValue)
            {
                client.Timeout = _settings.ConnectionTimeout.Value;
            }

            return client;
        }

        public HttpClientHandler GetHandler()
        {
            var defaultCredentials = _settings.DefaultUserCredentials;
            var credentials = defaultCredentials == null
                ? null
                : new NetworkCredential(defaultCredentials.Username, defaultCredentials.Password);

            return new HttpClientHandler { Credentials = credentials };
        }

        public void HandleError(Exception ex)
        {
            _errorHandler?.Invoke(this, ex);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
