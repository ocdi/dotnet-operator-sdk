﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dos.Operator.Caching;
using Dos.Operator.DependencyInjection;
using Dos.Operator.Watcher;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dos.Operator.Queue
{
    internal class EntityEventQueue<TEntity> : IDisposable
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        // TODO: Make configurable
        private const int QueueLimit = 512;
        private const double MaxRetrySeconds = 64;

        private readonly Channel<(EntityEventType type, TEntity resource)> _queue =
            Channel.CreateBounded<(EntityEventType type, TEntity resource)>(QueueLimit);

        private static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1);
        private readonly Random _rnd = new Random();
        private readonly ILogger<EntityEventQueue<TEntity>> _logger;
        private readonly EntityCache<TEntity> _cache = new EntityCache<TEntity>();
        private readonly EntityWatcher<TEntity> _watcher;

        private readonly IDictionary<string, EntityTimer<TEntity>> _delayedEnqueue =
            new ConcurrentDictionary<string, EntityTimer<TEntity>>();

        private readonly IDictionary<string, int> _erroredEventsCounter =
            new ConcurrentDictionary<string, int>();

        private CancellationTokenSource? _cancellation;

        public event EventHandler<(EntityEventType type, TEntity resource)>? ResourceEvent;

        public EntityEventQueue()
        {
            _logger = DependencyInjector.Services.GetRequiredService<ILogger<EntityEventQueue<TEntity>>>();
            _watcher = new EntityWatcher<TEntity>();
        }

        public async Task Start()
        {
            _logger.LogTrace(@"Event queue startup for type ""{type}"".", typeof(TEntity));
            _cancellation ??= new CancellationTokenSource();
            _watcher.WatcherEvent += OnWatcherEvent;
            await _watcher.Start();
#pragma warning disable 4014
            Task.Run(async () => await ReadQueue(), _cancellation.Token).ConfigureAwait(false);
#pragma warning restore 4014
        }

        public void Stop()
        {
            _logger.LogTrace(@"Event queue shutdown for type ""{type}"".", typeof(TEntity));
            _cancellation?.Cancel();
            _watcher.Stop();
            _watcher.WatcherEvent -= OnWatcherEvent;
            foreach (var timer in _delayedEnqueue.Values)
            {
                timer.Destroy();
            }

            _delayedEnqueue.Clear();
        }

        public void Dispose()
        {
            if (_cancellation != null && !_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }

            _queue.Writer.Complete();
            _watcher.Dispose();
            _cache.Clear();
            foreach (var timer in _delayedEnqueue.Values)
            {
                timer.Destroy();
            }

            _delayedEnqueue.Clear();
            foreach (var handler in ResourceEvent?.GetInvocationList() ?? new Delegate[] { })
            {
                ResourceEvent -= (EventHandler<(EntityEventType type, TEntity resource)>) handler;
            }
        }

        public async Task Enqueue(TEntity resource, TimeSpan? enqueueDelay = null)
        {
            try
            {
                await Semaphore.WaitAsync();
                if (enqueueDelay != null && enqueueDelay != TimeSpan.Zero)
                {
                    var timer = new EntityTimer<TEntity>(
                        resource,
                        enqueueDelay.Value,
                        async delayedResource =>
                        {
                            _logger.LogTrace(
                                @"Delayed event timer elapsed for ""{kind}/{name}"".",
                                delayedResource.Kind,
                                delayedResource.Metadata.Name);
                            _delayedEnqueue.Remove(delayedResource.Metadata.Uid);
                            var cachedResource = _cache.Get(delayedResource.Metadata.Uid);
                            if (cachedResource == null)
                            {
                                _logger.LogDebug(
                                    @"Resource ""{kind}/{name}"" was not present in the cache anymore. Don't execute delayed timer.",
                                    delayedResource.Kind,
                                    delayedResource.Metadata.Name);
                                return;
                            }

                            await Enqueue(cachedResource);
                        });
                    _delayedEnqueue.Add(resource.Metadata.Uid, timer);

                    _logger.LogDebug(
                        @"Enqueued delayed ({delay}) event for ""{kind}/{name}"".",
                        enqueueDelay.Value,
                        resource.Kind,
                        resource.Metadata.Name);
                    timer.Start();

                    return;
                }

                resource = _cache.Upsert(resource, out var state);
                _logger.LogTrace(
                    @"Resource ""{kind}/{name}"" comparison result ""{comparisonResult}"".",
                    resource.Kind,
                    resource.Metadata.Name,
                    state);

                switch (state)
                {
                    case CacheComparisonResult.New when resource.Metadata.DeletionTimestamp != null:
                    case CacheComparisonResult.Modified when resource.Metadata.DeletionTimestamp != null:
                    case CacheComparisonResult.NotModified when resource.Metadata.DeletionTimestamp != null:
                        await EnqueueEvent(EntityEventType.Finalizing, resource);
                        break;
                    case CacheComparisonResult.New:
                        await EnqueueEvent(EntityEventType.Created, resource);
                        break;
                    case CacheComparisonResult.Modified:
                        await EnqueueEvent(EntityEventType.Updated, resource);
                        break;
                    case CacheComparisonResult.StatusModified:
                        await EnqueueEvent(EntityEventType.StatusUpdated, resource);
                        break;
                    case CacheComparisonResult.NotModified:
                        await EnqueueEvent(EntityEventType.NotModified, resource);
                        break;
                }
            }
            finally
            {
                Semaphore.Release();
            }
        }

        public void EnqueueErrored(EntityEventType type, TEntity resource)
        {
            if (!_erroredEventsCounter.ContainsKey(resource.Metadata.Uid))
            {
                _erroredEventsCounter[resource.Metadata.Uid] = 0;
            }
            else
            {
                _erroredEventsCounter[resource.Metadata.Uid]++;
            }

            var backoff = ExponentialBackoff(_erroredEventsCounter[resource.Metadata.Uid]);
            _logger.LogDebug(
                @"Requeue event ""{eventType}"" with backoff ""{backoff}"" for resource ""{kind}/{name}"".",
                type,
                backoff,
                resource.Kind,
                resource.Metadata.Name);

            var timer = new EntityTimer<TEntity>(
                resource,
                backoff,
                async delayedResource =>
                {
                    _logger.LogTrace(
                        @"Backoff (error) requeue timer elapsed for ""{kind}/{name}"".",
                        delayedResource.Kind,
                        delayedResource.Metadata.Name);
                    _delayedEnqueue.Remove(delayedResource.Metadata.Uid);
                    await EnqueueEvent(type, delayedResource);
                });
            _delayedEnqueue.Add(resource.Metadata.Uid, timer);
            timer.Start();
        }

        public void ClearError(TEntity resource) => _erroredEventsCounter.Remove(resource.Metadata.Uid);

        private async void OnWatcherEvent(object? _, (WatchEventType type, TEntity resource) args)
        {
            var (type, resource) = args;
            switch (type)
            {
                case WatchEventType.Added:
                case WatchEventType.Modified:
                    await Enqueue(resource);
                    break;
                case WatchEventType.Deleted:
                    await EnqueueDeleted(resource);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private async Task EnqueueDeleted(TEntity resource)
        {
            _logger.LogTrace(
                @"Resource ""{kind}/{name}"" was deleted.",
                resource.Kind,
                resource.Metadata.Name);
            try
            {
                await Semaphore.WaitAsync();
                _cache.Remove(resource);
            }
            finally
            {
                Semaphore.Release();
            }

            await EnqueueEvent(EntityEventType.Deleted, resource);
        }

        private async Task EnqueueEvent(EntityEventType type, TEntity resource)
        {
            _logger.LogTrace(
                @"Enqueue event ""{type}"" for resource ""{kind}/{name}"".",
                type,
                resource.Kind,
                resource.Metadata.Name);
            _cancellation ??= new CancellationTokenSource();

            if (_delayedEnqueue.TryGetValue(resource.Metadata.Uid, out var timer))
            {
                _logger.LogDebug(
                    @"Event ""{type}"" for resource ""{kind}/{name}"" already had a delayed timer. Destroy the timer.",
                    type,
                    resource.Kind,
                    resource.Metadata.Name);
                timer.Destroy();
                _delayedEnqueue.Remove(resource.Metadata.Uid);
            }

            await _queue.Writer.WaitToWriteAsync(_cancellation.Token);
            if (!_queue.Writer.TryWrite((type, resource)))
            {
                _logger.LogWarning(
                    @"Queue for type ""{type}"" could not write into output channel.",
                    typeof(TEntity));
            }
        }

        private async Task ReadQueue()
        {
            _logger.LogTrace(@"Start queue reader for type ""{type}"".", typeof(TEntity));

            while (_cancellation != null &&
                   !_cancellation.IsCancellationRequested &&
                   await _queue.Reader.WaitToReadAsync(_cancellation.Token))
            {
                if (!_queue.Reader.TryRead(out var message))
                {
                    continue;
                }

                _logger.LogTrace(
                    @"Read event ""{type}"" for resource ""{resource}"".",
                    message.type,
                    message.resource);
                ResourceEvent?.Invoke(this, message);
            }
        }

        private TimeSpan ExponentialBackoff(int retryCount) => TimeSpan
            .FromSeconds(Math.Min(Math.Pow(2, retryCount), MaxRetrySeconds))
            .Add(TimeSpan.FromMilliseconds(_rnd.Next(0, 1000)));
    }
}