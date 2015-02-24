﻿using System;
using System.Collections.Generic;
using System.Text;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ConsumerEventHandler = R.MessageBus.Interfaces.ConsumerEventHandler;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Consumer :  IConsumer
    {
        private static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ITransportSettings _transportSettings;
        private IConnection _connection;
        private IModel _model;
        private ConsumerEventHandler _consumerEventHandler;
        private string _errorExchange;
        private string _auditExchange;
        private readonly int _retryDelay;
        private readonly int _maxRetries;
        private string _queueName;
        private string _retryQueueName;
        private bool _exclusive;
        private bool _autoDelete;
        private bool _connectionClosed;
        private readonly string[] _hosts;
        private int _activeHost;
        private readonly bool _errorsDisabled;
        private readonly bool _heartbeatEnabled;
        private readonly ushort _heartbeatTime;
        private readonly bool _purgeQueuesOnStartup;

        public Consumer(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;

            _hosts = transportSettings.Host.Split(',');
            _activeHost = 0;

            _retryDelay = transportSettings.RetryDelay;
            _maxRetries = transportSettings.MaxRetries;
            _exclusive = transportSettings.Queue.Exclusive;
            _autoDelete = transportSettings.Queue.AutoDelete;
            _errorsDisabled = transportSettings.DisableErrors;
            _heartbeatEnabled = !transportSettings.ClientSettings.ContainsKey("HeartbeatEnabled") || (bool)transportSettings.ClientSettings["HeartbeatEnabled"];
            _heartbeatTime = transportSettings.ClientSettings.ContainsKey("HeartbeatTime") ? Convert.ToUInt16((int)transportSettings.ClientSettings["HeartbeatTime"]) : Convert.ToUInt16(120);
            _purgeQueuesOnStartup = transportSettings.Queue.PurgeOnStartup;
        }

        /// <summary>
        /// Event fired on HandleBasicDeliver
        /// </summary>
        /// <param name="consumer"></param>
        /// <param name="args"></param>
        public void Event(IBasicConsumer consumer, BasicDeliverEventArgs args)
        {
            ConsumeEventResult result;

            try
            {
                var headers = args.BasicProperties.Headers;

                SetHeader(args, "TimeReceived", DateTime.UtcNow.ToString("O"));
                SetHeader(args, "DestinationMachine", Environment.MachineName);
                SetHeader(args, "DestinationAddress", _transportSettings.Queue.Name);

                if (!headers.ContainsKey("FullTypeName"))
                {
                    const string errMsg = "Error processing message, Message headers must contain FullTypeName.";
                    Logger.Error(errMsg);
                    throw new Exception(errMsg);
                }

                var typeName = Encoding.UTF8.GetString((byte[])headers["FullTypeName"]);

                result = _consumerEventHandler(args.Body, typeName, headers);
                _model.BasicAck(args.DeliveryTag, false);

                SetHeader(args, "TimeProcessed", DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                result = new ConsumeEventResult
                {
                    Exception = ex,
                    Success = false
                };
            }

            if (!result.Success)
            {
                int retryCount = 0;

                if (args.BasicProperties.Headers.ContainsKey("RetryCount"))
                {
                    retryCount = (int)args.BasicProperties.Headers["RetryCount"];
                }

                if (retryCount < _maxRetries)
                {
                    retryCount++;
                    SetHeader(args, "RetryCount", retryCount);

                    _model.BasicPublish(string.Empty, _retryQueueName, args.BasicProperties, args.Body);
                }
                else
                {
                    if (result.Exception != null)
                    {
                        string jsonException = string.Empty;
                        try
                        {
                            jsonException = JsonConvert.SerializeObject(result.Exception);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("Error serializing exception", ex);
                        }

                        SetHeader(args, "Exception", JsonConvert.SerializeObject(new
                        {
                            TimeStamp = DateTime.Now,
                            ExceptionType = result.Exception.GetType().FullName,
                            Message = GetErrorMessage(result.Exception),
                            result.Exception.StackTrace,
                            result.Exception.Source,
                            Exception = jsonException
                        }));
                    }

                    Logger.ErrorFormat("Max number of retries exceeded. MessageId: {0}", args.BasicProperties.MessageId);
                    _model.BasicPublish(_errorExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
            else if (!_errorsDisabled)
            {
                if (_transportSettings.AuditingEnabled)
                {
                    _model.BasicPublish(_auditExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            _consumerEventHandler = messageReceived;
            _queueName = queueName;

            if (exclusive.HasValue)
                _exclusive = exclusive.Value;

            if (autoDelete.HasValue)
                _autoDelete = autoDelete.Value;

            CreateConsumer();
        }

        private void CreateConsumer()
        {
            Logger.DebugFormat("Creating consumer on queue {0}", _queueName);

            var connectionFactory = new ConnectionFactory
            {
                HostName = _hosts[_activeHost],
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (_heartbeatEnabled)
            {
                connectionFactory.RequestedHeartbeat = _heartbeatTime;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Username))
            {
                connectionFactory.UserName = _transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Password))
            {
                connectionFactory.Password = _transportSettings.Password;
            }

            _connection = connectionFactory.CreateConnection();

            _model = _connection.CreateModel();

            // WORK QUEUE
            var queueName = ConfigureQueue();

            // RETRY QUEUE
            ConfigureRetryQueue();

            // ERROR QUEUE
            _errorExchange = ConfigureErrorExchange();
            var errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(_errorExchange))
            {
                _model.QueueBind(errorQueue, _errorExchange, string.Empty, null);
            }

            // Purge all messages on queue
            if (_purgeQueuesOnStartup)
            {
                Logger.Debug("Purging queue");
                _model.QueuePurge(queueName);
            }

            // AUDIT QUEUE
            if (_transportSettings.AuditingEnabled)
            {
                _auditExchange = ConfigureAuditExchange();
                var auditQueue = ConfigureAuditQueue();

                if (!string.IsNullOrEmpty(_auditExchange))
                {
                    _model.QueueBind(auditQueue, _auditExchange, string.Empty, null);
                }
            }

            var consumer = new EventingBasicConsumer();
            consumer.Received += Event;
            consumer.Shutdown += ConsumerShutdown;
            _model.BasicConsume(queueName, false, consumer);

            Logger.Debug("Started consuming");
        }

        public void ConsumeMessageType(string messageTypeName)
        {
            string exchange = ConfigureExchange(messageTypeName);

            if (!string.IsNullOrEmpty(exchange))
            {
                _model.QueueBind(_queueName, exchange, string.Empty);
            }
        }

        public string Type
        {
            get
            {
                return "RabbitMQ";
            }
        }

        private void ConsumerShutdown(object sender, ShutdownEventArgs e)
        {
            if (_connectionClosed)
            {
                Logger.Debug("Heartbeat missed but connection has been closed so not reconnecting");

                return;
            }

            if (_hosts.Length > 1)
            {
                if (_activeHost < _hosts.Length - 1)
                {
                    _activeHost++;
                }
                else
                {
                    _activeHost = 0;
                }
            }

            Logger.Debug("Heartbeat missed reconnecting to queue");

            Retry.Do(CreateConsumer, ex => Logger.Error("Error connecting to queue - {0}", ex), new TimeSpan(0, 0, 0, 10));
        }

        private string GetErrorMessage(Exception exception)
        {
            var sbMessage = new StringBuilder();
            sbMessage.Append(exception.Message + Environment.NewLine);
            var ie = exception.InnerException;
            while (ie != null)
            {
                sbMessage.Append(ie.Message + Environment.NewLine);
                ie = ie.InnerException;
            }

            return sbMessage.ToString();
        }

        private static void SetHeader<T>(BasicDeliverEventArgs args, string key, T value)
        {
            if (Equals(value, default(T)))
            {
                args.BasicProperties.Headers.Remove(key);
            }
            else
            {
                args.BasicProperties.Headers[key] = value;
            }
        }

        private string ConfigureQueue()
        {
            Logger.Debug("Configuring queue");

            var arguments = _transportSettings.Queue.Arguments;
            try
            {
                _model.QueueDeclare(_queueName, _transportSettings.Queue.Durable, _exclusive, _autoDelete, arguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue - {0}", ex.Message));
            }
            return _queueName;
        }

        private void ConfigureRetryQueue()
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            _retryQueueName = _queueName + ".Retries";
            string retryDeadLetterExchangeName = _queueName + ".Retries.DeadLetter";

            Logger.Debug("Configuring retry exchange");

            try
            {
                _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct",  _transportSettings.Queue.Durable, _autoDelete, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring dead letter exchange - {0}", ex.Message));
            }

            try
            {
                _model.QueueBind(_queueName, retryDeadLetterExchangeName, _retryQueueName); // only redeliver to the original queue (use _queueName as routing key)
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error binding dead letter queue - {0}", ex.Message));
            }

            Logger.Debug("Configuring retry queue");

            var arguments = new Dictionary<string, object>
            {
                {"x-dead-letter-exchange", retryDeadLetterExchangeName},
                {"x-message-ttl", _retryDelay}
            };

            try
            {
                _model.QueueDeclare(_retryQueueName, _transportSettings.Queue.Durable, _exclusive, _autoDelete, arguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue {0}", ex.Message));
            }
        }

        private string ConfigureErrorQueue()
        {
            Logger.Debug("Configuring error queue");

            try
            {
                _model.QueueDeclare(_transportSettings.ErrorQueueName, true, false, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring error queue {0}", ex.Message));
            }
            return _transportSettings.ErrorQueueName;
        }

        private string ConfigureAuditQueue()
        {
            Logger.Debug("Configuring audit queue");

            try
            {
                _model.QueueDeclare(_transportSettings.AuditQueueName, true, false, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring audit queue {0}", ex.Message));
            }
            return _transportSettings.AuditQueueName;
        }

        private string ConfigureExchange(string exchangeName)
        {
            Logger.Debug("Configuring exchange");

            try
            {
                _model.ExchangeDeclare(exchangeName, "fanout",  _transportSettings.Queue.Durable, _autoDelete, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange {0}", ex.Message));
            }

            return exchangeName;
        }

        private string ConfigureErrorExchange()
        {
            Logger.Debug("Configuring error exchange");

            try
            {
                _model.ExchangeDeclare(_transportSettings.ErrorQueueName, "direct");
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring error exchange {0}", ex.Message));
            }

            return _transportSettings.ErrorQueueName;
        }

        private string ConfigureAuditExchange()
        {
            Logger.Debug("Configuring audit exchange");

            try
            {
                _model.ExchangeDeclare(_transportSettings.AuditQueueName, "direct");
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring audit exchange {0}", ex.Message));
            }

            return _transportSettings.AuditQueueName;
        }

        public void StopConsuming()
        {
            Dispose();
        }

        public void Dispose()
        {
            _connectionClosed = true;

            if (_connection != null)
            {
                _connection.Close(500);
            }
            if (_model != null)
            {
                _model.Abort();
            }

            Logger.Debug("Disposing message bus consumer");
        }
    }
}