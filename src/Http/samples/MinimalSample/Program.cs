// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("ratelimited", o => o.BaseAddress = new Uri("http://localhost:5000"))
    .AddHttpMessageHandler(() =>


new RateLimitedHandler(
    new AggregateRateLimitBuilder<HttpRequestMessage>()

    .WithConcurrencyPolicy(request => request.RequestUri.ToString(),
        new ConcurrencyLimiterOptions(2, QueueProcessingOrder.OldestFirst, 2))

    .WithTokenBucketPolicy(request => request.Method.Equals(HttpMethod.Post) ? HttpMethod.Post.Method : null,
        new TokenBucketRateLimiterOptions(1, QueueProcessingOrder.OldestFirst, 1, TimeSpan.FromSeconds(1), 1, true))

    .WithPolicy(request => request.Headers.TryGetValues("cookie", out _) ? "cookie" : null,
        _ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions(1, QueueProcessingOrder.NewestFirst, 1)))

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

var task = app.RunAsync();




var factory = app.Services.GetRequiredService<IHttpClientFactory>();
var client = factory.CreateClient("ratelimited");
var resp = await client.GetAsync("/problem");
resp = await client.GetAsync("/problem-object");
resp = await client.GetAsync("/json");




await task;

class RateLimitedHandler : DelegatingHandler
{
    private readonly AggregatedRateLimiter<HttpRequestMessage> _rateLimiter;

    public RateLimitedHandler(AggregatedRateLimiter<HttpRequestMessage> limiter)
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
        throw new Exception();
    }
}

#nullable enable
namespace System.Threading.RateLimiting
{
    public abstract class AggregatedRateLimiter<TKey> : IAsyncDisposable, IDisposable
    {
        // an inaccurate view of resources
        public abstract int AvailablePermits(TKey resourceID);

        // Fast synchronous attempt to acquire resources
        public abstract RateLimitLease Acquire(TKey resourceID, int requestedCount);

        // Wait until the requested resources are available
        public abstract ValueTask<RateLimitLease> WaitAsync(TKey resourceID, int requestedCount, CancellationToken cancellationToken = default);

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
    public class AggregateRateLimitBuilder<TKey>
    {
        private List<(Func<TKey, string?>, Func<string, RateLimiter>)> _policies = new();
        private TimeSpan _minRefreshInterval = TimeSpan.MaxValue;
        private RateLimiter _defaultRateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions(1, QueueProcessingOrder.OldestFirst, 1));

        public AggregateRateLimitBuilder<TKey> WithPolicy(Func<TKey, string?> keyFactory, Func<string, RateLimiter> limiterFactory)
        {
            _policies.Add((keyFactory, limiterFactory));
            return this;
        }

        public AggregateRateLimitBuilder<TKey> WithConcurrencyPolicy(Func<TKey, string?> keyFactory, ConcurrencyLimiterOptions options)
        {
            _policies.Add((keyFactory, _ => new ConcurrencyLimiter(options)));
            return this;
        }

        // Should there be a RateLimiter abstraction for timer based limiters?
        // public AggregateRateLimitBuilder<TKey> WithTimerPolicy(Func<TKey, string?> keyFactory, TimeBasedRateLimiter)

        public AggregateRateLimitBuilder<TKey> WithTokenBucketPolicy(Func<TKey, string?> keyFactory, TokenBucketRateLimiterOptions options)
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
            _policies.Add((keyFactory, _ => new TokenBucketRateLimiter(options)));
            return this;
        }

        // might want this to be a factory if the builder is re-usable
        public AggregateRateLimitBuilder<TKey> WithDefaultRateLimiter(RateLimiter defaultRateLimiter)
        {
            _defaultRateLimiter = defaultRateLimiter;
            return this;
        }

        public AggregatedRateLimiter<TKey> Build()
        {
            return new Impl<TKey>(_policies, _defaultRateLimiter, _minRefreshInterval);
        }
    }

    internal class Impl<TKey> : AggregatedRateLimiter<TKey>
    {
        private const string _defaultKey = "__default";
        private readonly List<(Func<TKey, string?>, Func<string, RateLimiter>)> _policies;
        private readonly Timer? _timer;
        private bool _disposed;

        private readonly Dictionary<string, RateLimiter> _limiters = new();

        public Impl(List<(Func<TKey, string?>, Func<string, RateLimiter>)> policies, RateLimiter defaultRateLimiter, TimeSpan minRefreshInterval)
        {
            _policies = policies;
            _limiters.Add(_defaultKey, defaultRateLimiter);

            if (minRefreshInterval != TimeSpan.MaxValue)
            {
                _timer = new Timer(Tick, this, minRefreshInterval, minRefreshInterval);
            }
        }

        public override RateLimitLease Acquire(TKey resourceID, int requestedCount)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.Acquire(requestedCount);
        }

        public override int AvailablePermits(TKey resourceID)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.GetAvailablePermits();
        }

        public override ValueTask<RateLimitLease> WaitAsync(TKey resourceID, int requestedCount, CancellationToken cancellationToken = default)
        {
            RateLimiter limiter = GetLimiter(resourceID);

            return limiter.WaitAsync(requestedCount, cancellationToken);
        }

        private RateLimiter GetLimiter(TKey resourceID)
        {
            RateLimiter? limiter = null;
            foreach ((Func<TKey, string?>, Func<string, RateLimiter>) policy in _policies)
            {
                string? id = policy.Item1(resourceID);
                if (id is not null)
                {
                    lock (_policies)
                    {
                        if (!_limiters.TryGetValue(id, out limiter))
                        {
                            limiter = policy.Item2(id);
                            _limiters.Add(id, limiter);
                        }
                    }
                    break;
                }
            }

            if (limiter is null)
            {
                lock (_policies)
                {
                    limiter = _limiters[_defaultKey];
                }
            }
            return limiter;
        }

        private static void Tick(object? obj)
        {
            Impl<TKey> aggregateLimiter = (Impl<TKey>)obj!;

            lock (aggregateLimiter._policies)
            {
                foreach (KeyValuePair<string, RateLimiter> limiter in aggregateLimiter._limiters)
                {
                    if (limiter.Value is TokenBucketRateLimiter tokenBucketRateLimiter)
                    {
                        tokenBucketRateLimiter.TryReplenish();
                    }

                    // Remove limiters that have full permits? Maybe put them in a queue of potential limiters to remove
                    // Or have an abstraction/method that lets us query a rate limiter to see if it's idle
                    //limiter.Value.GetAvailablePermits();
                }
            }
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

                _limiters.Clear();
            }
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Dispose(true);

            return default;
        }
    }

    public class SimpleRateLimiterImpl : AggregatedRateLimiter<HttpRequestMessage>
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

        public override RateLimitLease Acquire(HttpRequestMessage resourceID, int requestedCount)
        {
            return GetRateLimiter(resourceID).Acquire(requestedCount);
        }

        public override int AvailablePermits(HttpRequestMessage resourceID)
        {
            return GetRateLimiter(resourceID).GetAvailablePermits();
        }

        public override ValueTask<RateLimitLease> WaitAsync(HttpRequestMessage resourceID, int requestedCount, CancellationToken cancellationToken = default)
        {
            return GetRateLimiter(resourceID).WaitAsync(requestedCount, cancellationToken);
        }
    }
}
