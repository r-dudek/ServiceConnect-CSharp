﻿using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface ITransportSettings
    {
        /// <summary>
        /// Delay (in miliseconds) between bus attempts to redeliver message
        /// </summary>
        int RetryDelay { get; set; }

        /// <summary>
        /// Maximum number of retries
        /// </summary>
        int MaxRetries { get; set; }

        /// <summary>
        /// Messaging host
        /// </summary>
        string Host { get; set; }

        /// <summary>
        /// Messaging host username
        /// </summary>
        string Username { get; set; }

        /// <summary>
        /// Messaging host password
        /// </summary>
        string Password { get; set; }

        string MachineName { get; set; }

        /// <summary>
        /// Custom Error Queue Name
        /// </summary>
        string ErrorQueueName { get; set; }

        /// <summary>
        /// Auditing enabled
        /// </summary>
        bool AuditingEnabled { get; set; }

        /// <summary>
        /// Custom Audit Queue Name
        /// </summary>
        string AuditQueueName { get; set; }

        /// <summary>
        /// Disable sending errors to error queue
        /// </summary>
        bool DisableErrors { get; set; }

        /// <summary>
        /// Custom Heartbeat Queue Name
        /// </summary>
        string HeartbeatQueueName { get; set; }

        /// <summary>
        /// Contains settings specific to client
        /// </summary>
        IDictionary<string, object> ClientSettings { get; set; }

        string QueueName { get; set; }

        bool PurgeQueueOnStartup { get; set; }
    }
}
