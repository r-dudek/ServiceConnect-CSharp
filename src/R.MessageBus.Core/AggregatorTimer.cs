﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Common.Logging;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class AggregatorTimer : IDisposable
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IAggregatorPersistor _aggregatorPersistor;
        private readonly IBusContainer _container;
        private readonly HandlerReference _aggregatorReference;
        private Timer _timer;
        private Type _type;

        public AggregatorTimer(IAggregatorPersistor aggregatorPersistor, IBusContainer container, HandlerReference aggregatorReference)
        {
            _aggregatorReference = aggregatorReference;
            _aggregatorPersistor = aggregatorPersistor;
            _container = container;
        }

        public void StartTimer<T>(TimeSpan timeout)
        {
            _type = typeof (T);
            _timer = new Timer(Callback, timeout, timeout, timeout);
        }

        private void Callback(object state)
        {
            if (_aggregatorPersistor.Count(_type.AssemblyQualifiedName) > 0)
            {
                object aggregator = _container.GetInstance(_aggregatorReference.HandlerType);
                var messages = _aggregatorPersistor.GetData(_type.AssemblyQualifiedName);
                try
                {
                    _aggregatorReference.HandlerType.GetMethod("Execute", new[] { _type }).Invoke(aggregator, new object[] { Convert.ChangeType(messages, _type) });
                }
                catch (Exception)
                {
                    Logger.Error("Error executing aggregator execute method");
                    throw;
                }

                _aggregatorPersistor.RemoveAll(_type.AssemblyQualifiedName);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}