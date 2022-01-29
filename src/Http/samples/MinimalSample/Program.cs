// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("RateLimited", o => o.BaseAddress = new Uri("http://localhost:5000"))
    .AddHttpMessageHandler(() =>


new RateLimitedHandler(
    new AggregateRateLimitBuilder<HttpRequestMessage>()

    .WithConcurrencyPolicy(request => request.Method.Equals(HttpMethod.Post) ? HttpMethod.Post : null,
        new ConcurrencyLimiterOptions(1, QueueProcessingOrder.OldestFirst, 10))

    .WithPolicy(request => request.Headers.TryGetValues("cookie", out _) ? "cookie" : null,
        _ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions(1, QueueProcessingOrder.NewestFirst, 1)))

    .WithConcurrencyPolicy(request => request.RequestUri.AbsolutePath.StartsWith("/problem", StringComparison.InvariantCultureIgnoreCase) ? request.RequestUri : null,
        new ConcurrencyLimiterOptions(2, QueueProcessingOrder.OldestFirst, 2))

    .Build()));

//() => new RateLimitedHandler(new SimpleRateLimiterImpl()));

var app = builder.Build();
//var app = WebApplication.Create(args);

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

string Plaintext() => "Hello, World!";
app.MapGet("/plaintext", Plaintext);


object Json() => new { message = "Hello, World!" };
app.MapGet("/json", Json);

string SayHello(string name) => $"Hello, {name}!";
app.MapGet("/hello/{name}", SayHello);

var extensions = new Dictionary<string, object>() { { "traceId", "traceId123" } };

app.MapGet("/problem", () =>
    Results.Problem(statusCode: 500, extensions: extensions));

app.MapGet("/problem-object", () =>
    Results.Problem(new ProblemDetails() { Status = 500, Extensions = { { "traceId", "traceId123" } } }));

var errors = new Dictionary<string, string[]>();

app.MapGet("/validation-problem", () =>
    Results.ValidationProblem(errors, statusCode: 400, extensions: extensions));

app.MapGet("/validation-problem-object", () =>
    Results.Problem(new HttpValidationProblemDetails(errors) { Status = 400, Extensions = { { "traceId", "traceId123" } } }));

app.MapPost("/post", ([FromBody] string obj) => obj);


var appTask = app.RunAsync();



var factory = app.Services.GetRequiredService<IHttpClientFactory>();
var client = factory.CreateClient("RateLimited");
var resp = await client.GetAsync("/problem");
resp = await client.GetAsync("/problem");
resp = await client.GetAsync("/problem-object");
resp = await client.GetAsync("/json");
resp = await client.PostAsJsonAsync("/post", "{\"t\":\"content\"}");
for (var i = 0; i < 10; ++i)
{
    _ = Task.Run(() => client.PostAsJsonAsync("/post", "{\"t\":\"content\"}"));
}




await appTask;

class RateLimitedHandler : DelegatingHandler
{
    private readonly AggregateRateLimiter<HttpRequestMessage> _rateLimiter;

    public RateLimitedHandler(AggregateRateLimiter<HttpRequestMessage> limiter)
    {
        _rateLimiter = limiter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await _rateLimiter.WaitAsync(request, 1, cancellationToken);
        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken);
        }
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.Add(HeaderNames.RetryAfter, ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo));
        }
        return response;
    }
}

#nullable enable
namespace System.Threading.RateLimiting
{
    public abstract class AggregateRateLimiter<TResource> : IAsyncDisposable, IDisposable
    {
        // an inaccurate view of resources
        public abstract int GetAvailablePermits(TResource resourceID);

        // Fast synchronous attempt to acquire resources
        public RateLimitLease Acquire(TResource resourceID, int permitCount = 1)
        {
            if (permitCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(permitCount));
            }

            return AcquireCore(resourceID, permitCount);
        }

        protected abstract RateLimitLease AcquireCore(TResource resourceID, int permitCount);

        // Wait until the requested resources are available
        public ValueTask<RateLimitLease> WaitAsync(TResource resourceID, int permitCount = 1, CancellationToken cancellationToken = default)
        {
            if (permitCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(permitCount));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<RateLimitLease>(Task.FromCanceled<RateLimitLease>(cancellationToken));
            }

            return WaitAsyncCore(resourceID, permitCount, cancellationToken);
        }

        protected abstract ValueTask<RateLimitLease> WaitAsyncCore(TResource resourceID, int permitCount, CancellationToken cancellationToken);

        protected virtual void Dispose(bool disposing) { }

        /// <summary>
        /// Disposes the RateLimiter. This completes any queued acquires with a failed lease.
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// DisposeAsync method for implementations to write.
        /// </summary>
        protected virtual ValueTask DisposeAsyncCore()
        {
            return default;
        }

        /// <summary>
        /// Disposes the RateLimiter asynchronously.
        /// </summary>
        /// <returns>ValueTask representin the completion of the disposal.</returns>
        public async ValueTask DisposeAsync()
        {
            // Perform async cleanup.
            await DisposeAsyncCore().ConfigureAwait(false);

            // Dispose of unmanaged resources.
            Dispose(false);

            // Suppress finalization.
            GC.SuppressFinalize(this);
        }
    }
}

namespace System.Threading.RateLimiting
{
    public class AggregateRateLimitBuilder<TResource>
    {
        private List<Func<TResource, RateLimiter?>> _policies = new();
        private TimeSpan _minRefreshInterval = TimeSpan.MaxValue;
        private RateLimiter _defaultRateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions(1, QueueProcessingOrder.OldestFirst, 1));

        public AggregateRateLimitBuilder<TResource> WithPolicy<TKey>(Func<TResource, TKey?> keyFactory, Func<TKey, RateLimiter> limiterFactory) where TKey : notnull
        {
            Dictionary<TKey, RateLimiter> limiters = new();
            var func = (TResource resource) =>
            {
                if (keyFactory(resource) is TKey key)
                {
                    RateLimiter? limiter;
                    if (!limiters.TryGetValue(key, out limiter))
                    {
                        limiter = limiterFactory(key);
                        limiters.Add(key, limiter);
                    }
                    return limiter;
                }
                return null;
            };
            _policies.Add(func);
            return this;
        }

        public AggregateRateLimitBuilder<TResource> WithConcurrencyPolicy<TKey>(Func<TResource, TKey?> keyFactory, ConcurrencyLimiterOptions options) where TKey : notnull
        {
            Dictionary<TKey, RateLimiter> limiters = new();
            var func = (TResource resource) =>
            {
                if (keyFactory(resource) is TKey key)
                {
                    RateLimiter? limiter;
                    if (!limiters.TryGetValue(key, out limiter))
                    {
                        limiter = new ConcurrencyLimiter(options);
                        limiters.Add(key, limiter);
                    }
                    return limiter;
                }
                return null;
            };
            _policies.Add(func);
            return this;
        }

        // Should there be a RateLimiter abstraction for timer based limiters?
        // public AggregateRateLimitBuilder<TResource> WithTimerPolicy<TKey>(Func<TKey, string?> keyFactory, TimeBasedRateLimiter) where TKey : notnull;

        public AggregateRateLimitBuilder<TResource> WithTokenBucketPolicy<TKey>(Func<TResource, TKey?> keyFactory, TokenBucketRateLimiterOptions options) where TKey : notnull
        {
            if (options.AutoReplenishment)
            {
                options = new TokenBucketRateLimiterOptions(options.TokenLimit, options.QueueProcessingOrder, options.QueueLimit, options.ReplenishmentPeriod,
                    options.TokensPerPeriod, autoReplenishment: false);
            }

            if (_minRefreshInterval > options.ReplenishmentPeriod)
            {
                _minRefreshInterval = options.ReplenishmentPeriod;
            }
            Dictionary<TKey, RateLimiter> limiters = new();
            var func = (TResource resource) =>
            {
                if (keyFactory(resource) is TKey key)
                {
                    RateLimiter? limiter;
                    if (!limiters.TryGetValue(key, out limiter))
                    {
                        limiter = new TokenBucketRateLimiter(options);
                        limiters.Add(key, limiter);
                    }
                    return limiter;
                }
                return null;
            };
            _policies.Add(func);
            return this;
        }

        // might want this to be a factory if the builder is re-usable
        public AggregateRateLimitBuilder<TResource> WithDefaultRateLimiter(RateLimiter defaultRateLimiter)
        {
            _defaultRateLimiter = defaultRateLimiter;
            return this;
        }

        public AggregateRateLimiter<TResource> Build()
        {
            return new Impl<TResource>(_policies, _defaultRateLimiter, _minRefreshInterval);
        }
    }

    internal class Impl<TResource> : AggregateRateLimiter<TResource>
    {
        private readonly RateLimiter _defaultRateLimiter;
        private readonly List<Func<TResource, RateLimiter?>> _policies;
        private readonly Timer? _timer;
        private bool _disposed;

        //private readonly Dictionary<TKey, RateLimiter> _limiters = new();

        public Impl(List<Func<TResource, RateLimiter?>> policies, RateLimiter defaultRateLimiter, TimeSpan minRefreshInterval)
        {
            _policies = policies;
            _defaultRateLimiter = defaultRateLimiter;

            if (minRefreshInterval != TimeSpan.MaxValue)
            {
                _timer = new Timer(Tick, this, minRefreshInterval, minRefreshInterval);
            }
        }

        protected override RateLimitLease AcquireCore(TResource resourceID, int requestedCount)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.Acquire(requestedCount);
        }

        public override int GetAvailablePermits(TResource resourceID)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.GetAvailablePermits();
        }

        protected override ValueTask<RateLimitLease> WaitAsyncCore(TResource resourceID, int requestedCount, CancellationToken cancellationToken = default)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.WaitAsync(requestedCount, cancellationToken);
        }

        private RateLimiter GetLimiter(TResource resourceID)
        {
            lock (_policies)
            {
                foreach (Func<TResource, RateLimiter?> policy in _policies)
                {
                    RateLimiter? rateLimiter = policy(resourceID);
                    if (rateLimiter is not null)
                    {
                        // if (rateLimiter is ReplenishingRateLimiter replenishingRateLimiter)
                        // ...
                        return rateLimiter;
                    }
                }
            }

            return _defaultRateLimiter;
        }

        private static void Tick(object? obj)
        {
            //Impl<TResource, TKey> aggregateLimiter = (Impl<TResource, TKey>)obj!;

            //lock (aggregateLimiter._policies)
            //{
            //    foreach (KeyValuePair<TKey, RateLimiter> limiter in aggregateLimiter._limiters)
            //    {
            //        if (limiter.Value is TokenBucketRateLimiter tokenBucketRateLimiter)
            //        {
            //            tokenBucketRateLimiter.TryReplenish();
            //        }

            //        // Remove limiters that have full permits? Maybe put them in a queue of potential limiters to remove
            //        // Or have an abstraction/method that lets us query a rate limiter to see if it's idle
            //        //limiter.Value.GetAvailablePermits();
            //    }
            //}
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            lock (_policies)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                _timer?.Dispose();
                _policies.Clear();
            }
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Dispose(true);

            return default;
        }
    }

    public class SimpleRateLimiterImpl : AggregateRateLimiter<HttpRequestMessage>
    {
        private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();
        private readonly RateLimiter _defaultLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions(1, QueueProcessingOrder.OldestFirst, 1, TimeSpan.FromSeconds(1), 1, true));

        public SimpleRateLimiterImpl() { }

        private RateLimiter GetRateLimiter(HttpRequestMessage resource)
        {
            if (!_limiters.TryGetValue(resource.RequestUri!.AbsolutePath, out var limiter))
            {
                if (resource.RequestUri!.AbsolutePath.StartsWith("/problem", StringComparison.OrdinalIgnoreCase))
                {
                    limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions(1, QueueProcessingOrder.NewestFirst, 1));
                }
                else
                {
                    // default
                    limiter = _defaultLimiter;
                }
                limiter = _limiters.GetOrAdd(resource.RequestUri!.AbsolutePath, limiter);
            }

            return limiter;
        }

        protected override RateLimitLease AcquireCore(HttpRequestMessage resourceID, int requestedCount)
        {
            return GetRateLimiter(resourceID).Acquire(requestedCount);
        }

        public override int GetAvailablePermits(HttpRequestMessage resourceID)
        {
            return GetRateLimiter(resourceID).GetAvailablePermits();
        }

        protected override ValueTask<RateLimitLease> WaitAsyncCore(HttpRequestMessage resourceID, int requestedCount, CancellationToken cancellationToken = default)
        {
            return GetRateLimiter(resourceID).WaitAsync(requestedCount, cancellationToken);
        }
    }

    public class ConcurrentAggregateLimiter : AggregateRateLimiter<IPAddress>
    {
        private readonly Dictionary<string, int> _items = new();
        private readonly object _lock = new object();
        private const int _defaultPermitCount = 10;

        public override int GetAvailablePermits(IPAddress resourceID)
        {
            lock (_lock)
            {
                if (_items.TryGetValue(resourceID.ToString(), out var value))
                {
                    return value;
                }
                return _defaultPermitCount;
            }
        }

        protected override RateLimitLease AcquireCore(IPAddress resourceID, int permitCount)
        {
            return GetPermits(resourceID, permitCount);
        }

        protected override ValueTask<RateLimitLease> WaitAsyncCore(IPAddress resourceID, int permitCount, CancellationToken cancellationToken)
        {
            return new ValueTask<RateLimitLease>(GetPermits(resourceID, permitCount));
        }

        private RateLimitLease GetPermits(IPAddress resource, int count)
        {
            if (count > _defaultPermitCount)
            {
                return new InternalRateLimitLease(null, null, 0);
            }

            lock (_lock)
            {
                if (!_items.TryGetValue(resource.ToString(), out var value))
                {
                    _items.Add(resource.ToString(), _defaultPermitCount - count);
                }
                else
                {
                    if (value >= count)
                    {
                        _items[resource.ToString()] = value - count;
                    }
                    else
                    {
                        return new InternalRateLimitLease(null, null, 0);
                    }
                }
            }
            return new InternalRateLimitLease(this, resource, count);
        }

        private void Release(IPAddress resource, int count)
        {
            lock (_lock)
            {
                if (_items.TryGetValue(resource.ToString(), out var value))
                {
                    value += count;
                    Diagnostics.Debug.Assert(value <= _defaultPermitCount);
                    _items[resource.ToString()] = value;
                }
            }
        }

        private class InternalRateLimitLease : RateLimitLease
        {
            private int _count;
            private readonly ConcurrentAggregateLimiter? _limiter;
            private IPAddress? _resource;

            public InternalRateLimitLease(ConcurrentAggregateLimiter? limiter, IPAddress? resource, int count)
            {
                _count = count;
                _limiter = limiter;
                _resource = resource;
            }

            public override bool IsAcquired => _count > 0;

            public override IEnumerable<string> MetadataNames => throw new NotImplementedException();

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                throw new NotImplementedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_count > 0)
                    {
                        _limiter?.Release(_resource!, _count);
                        _resource = null;
                        _count = 0;
                    }
                }
            }
        }
    }
}
