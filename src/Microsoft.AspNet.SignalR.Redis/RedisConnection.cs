﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Microsoft.AspNet.SignalR.Redis
{
    public class RedisConnection : IRedisConnection
    {
        private StackExchange.Redis.ISubscriber _redisSubscriber;
        private ConnectionMultiplexer _connection;
        private TraceSource _trace;
        private ulong _latestMessageId;
        private ConfigurationOptions _options;
        private ConfigurationOptions _sentinelConfiguration;
        private ConnectionMultiplexer _sentinelConnection;

				public async Task ConnectAsync(string connectionString, TraceSource trace)
        {
            _connection = await ConnectToRedis(connectionString);
            _connection.ConnectionFailed += OnConnectionFailed;
            _connection.ConnectionRestored += OnConnectionRestored;
            _connection.ErrorMessage += OnError;

            _trace = trace;

            _redisSubscriber = _connection.GetSubscriber();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1026:DefaultParametersShouldNotBeUsed")]
        public void Close(string key, bool allowCommandsToComplete = true)
        {
            if (_redisSubscriber != null)
            {
                _redisSubscriber.Unsubscribe(key);
            }

            if (_connection != null)
            {
                _connection.Close(allowCommandsToComplete);
            }

            _connection.Dispose();
        }

        public async Task SubscribeAsync(string key, Action<int, RedisMessage> onMessage)
        {
            await _redisSubscriber.SubscribeAsync(key, (channel, data) =>
            {
                var message = RedisMessage.FromBytes(data, _trace);
                onMessage(0, message);

                // Save the last message id in just in case redis shuts down
                _latestMessageId = message.Id;
            });
        }

        public void Dispose()
        {
            if (_connection != null)
            {
                _connection.Dispose();
            }
        }

        public Task ScriptEvaluateAsync(int database, string script, string key, byte[] messageArguments)
        {
            if (_connection == null)
            {
                throw new InvalidOperationException(Resources.Error_RedisConnectionNotStarted);
            }

            var keys = new RedisKey[] { key };

            var arguments = new RedisValue[] { messageArguments };

          return _connection.GetDatabase(database).ScriptEvaluateAsync(script, keys, arguments);
        }

        public async Task RestoreLatestValueForKey(int database, string key)
        {
            try
            {
                // Workaround for StackExchange.Redis/issues/61 that sometimes Redis connection is not connected in ConnectionRestored event 
                while (!_connection.GetDatabase(database).IsConnected(key))
                {
                    await Task.Delay(200);
                }

                var redisResult = await _connection.GetDatabase(database).ScriptEvaluateAsync(
                   @"local newvalue = redis.call('GET', KEYS[1])
                    if newvalue < ARGV[1] then
                        return redis.call('SET', KEYS[1], ARGV[1])
                    else
                        return nil
                    end",
                   new RedisKey[] { key },
                   new RedisValue[] { _latestMessageId });

                if (!redisResult.IsNull)
                {
                    _trace.TraceInformation("Restored Redis Key {0} to the latest Value {1} ", key, _latestMessageId);
                }
            }
            catch (Exception ex)
            {
                _trace.TraceError("Error while restoring Redis Key to the latest Value: " + ex);
            }
        }

        public event Action<Exception> ConnectionFailed;

        public event Action<Exception> ConnectionRestored;

        public event Action<Exception> ErrorMessage;

        private void OnConnectionFailed(object sender, ConnectionFailedEventArgs args)
        {
            var handler = ConnectionFailed;
            handler(args.Exception);
        }

        private void OnConnectionRestored(object sender, ConnectionFailedEventArgs args)
        {
            var handler = ConnectionRestored;
            handler(args.Exception);
        }

        private void OnError(object sender, RedisErrorEventArgs args)
        {
            var handler = ErrorMessage;
            handler(new InvalidOperationException(args.Message));
        }
        private async Task<ConnectionMultiplexer> ConnectToRedis(string connectionString)
        {
          _options = ConfigurationOptions.Parse(connectionString);
          if (!string.IsNullOrEmpty(_options.ServiceName))
          {
            _sentinelConfiguration = CreateSentinellConfiguration(_options);
            ModifyEndpointsForSentinelConfiguration(_options);
          }
          return await ConnectionMultiplexer.ConnectAsync(_options);
        }

        private void ModifyEndpointsForSentinelConfiguration(ConfigurationOptions options)
        {
          ConnectToSentinel();
          EndPoint masterEndPoint = _sentinelConnection.GetServer(_sentinelConnection.GetEndPoints().First()).SentinelGetMasterAddressByName(_sentinelConfiguration.ServiceName);
          options.EndPoints.Clear();
          options.EndPoints.Add(masterEndPoint);
        }

        private ConfigurationOptions CreateSentinellConfiguration(ConfigurationOptions configOption)
        {
          var sentinelConfiguration = new ConfigurationOptions
          {
            CommandMap = CommandMap.Sentinel,
            TieBreaker = "",
            ServiceName = configOption.ServiceName,
            SyncTimeout = configOption.SyncTimeout,
          };
          foreach (var endPoint in configOption.EndPoints)
          {
            sentinelConfiguration.EndPoints.Add(endPoint);
          }
          return sentinelConfiguration;
        }

        private void ConnectToSentinel()
        {
          _sentinelConnection = ConnectionMultiplexer.Connect(_sentinelConfiguration);
          var subscriber = _sentinelConnection.GetSubscriber();
          subscriber.Subscribe("+switch-master", MasterWasSwitched);
          _sentinelConnection.ConnectionFailed += SentinelConnectionFailed;
					_sentinelConnection.ConnectionRestored += SentinelConnectionRestored;
				}

        private void SentinelConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
          _sentinelConnection.ConnectionFailed -= SentinelConnectionFailed;
					_sentinelConnection.ConnectionRestored -= SentinelConnectionRestored;
					_sentinelConnection.Close();
          ConnectToSentinel();
        }

				private void SentinelConnectionRestored(object sender, ConnectionFailedEventArgs e)
				{
					// Workaround for StackExchange.Redis/issues/61 that sometimes Redis connection is not connected in ConnectionRestored event 
					while (!_sentinelConnection.IsConnected)
					{
						Task.Delay(200).Wait();
					}
					var subscriber = _sentinelConnection.GetSubscriber();
					subscriber.Subscribe("+switch-master", MasterWasSwitched);
				}

				private void MasterWasSwitched(RedisChannel redisChannel, RedisValue redisValue)
        {
	        _connection.ConnectionFailed -= OnConnectionFailed;
	        _connection.ConnectionRestored -= OnConnectionRestored;
	        _connection.ErrorMessage -= OnError;
          _connection.Close();
          if (redisValue.IsNullOrEmpty) return;
          var message = redisValue.ToString();
          var messageParts = message.Split(' ');
          var ip = IPAddress.Parse(messageParts[3]);
          var port = int.Parse(messageParts[4]);
          EndPoint newMasterEndpoint = new IPEndPoint(ip, port);
          if (_options.EndPoints.Any() && newMasterEndpoint == _options.EndPoints[0]) return;
          _options.EndPoints.Clear();
          _options.EndPoints.Add(newMasterEndpoint);

          _connection = ConnectionMultiplexer.Connect(_options);
          _connection.ConnectionFailed += OnConnectionFailed;
          _connection.ConnectionRestored += OnConnectionRestored;
          _connection.ErrorMessage += OnError;
          _redisSubscriber = _connection.GetSubscriber();
          var handler = ConnectionRestored;
          if (handler != null) handler(new ApplicationException("Redis master was switched"));
        }
    }
}
