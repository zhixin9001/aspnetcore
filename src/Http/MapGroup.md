# Goals

- Define a route pattern prefix in a single place
    - The prefix should support slugs, route constraints, etc...
- Allow adding metadata to a specified collection (group) of endpoints using a single API call.
- Any endpoint that can be added to an `IEndpointRouteBuilder` can also be added to a group.
    - `MapGet`, `MapPost`, etc...
    - `MapControllers`
    - `MapHub<THub>`/`MapConnections`/`MapConnectionHandler`
    - `MapFallbackToFile`
    - `MapHealthChecks`
    - etc...
- Existing `IEndpointConventionBuilder` extension methods can be applied to the entire group.
    - `RequireAuthorization`
    - `RequireCors`
    - `WithGroupName`
    - `WithMetadata`
    - `WithName`/`WithDisplayName`? Should this be an error?
    - etc...
- The `Action<EndpointBuilder>` passed to the `IEndpointConventionBuilder` should be run per endpoint so previously applied metadata can be observed and possibly mutated.
- We should make nested group structure observable at runtime via endpoint metadata.

# API suggestions

## MapGroup returns a GroupRouteBuilder

```csharp
public class GroupRouteBuilder : IEndpointRouteBuilder, IEndpointConventionBuilder
{
    public RoutePattern GroupPrefix { get; }

    public void Configure<TBuilder>(Action<TBuilder> configure) where TBuilder : IEndpointRouteBuilder;

    // The following wouldn't work because we cannot combine arbitrary IEndpointConventionBuilders
    //public TBuilder Configure<TBuilder>() where TBuilder : IEndpointRouteBuilder;
}
public static class GroupEndpointRouteBuilderExtensions
{
    public static GroupRouteBuilder MapGroup(this IEndpointRouteBuilder endpoints, string pattern);
}
public interface IGroupedEndpointConventionBuilder : IEndpointConventionBuilder
{
    void AddPrefix(Action<EndpointBuilder> convention);
}
// This would be implemented EndpointDataSources that support grouping
public interface IGroupedEndpointDataSource
{
    IEnumerable<IGroupedEndpointConventionBuilder> ConventionBuilders { get; }
}
```

```csharp
var app = WebApplication.Create(args);

var group = app.MapGroup("/todos");
group.MapGet("/", (int id, TodoDb db) => db.ToListAsync());
group.MapGet("/{id}", (int id, TodoDb db) => db.GetAsync(id));
group.MapPost("/", (Todo todo, TodoDb db) => db.AddAsync(todo));

var nestedGroup = group.MapGroup("/{org}");
nestedGroup.MapGet("/", (string org, TodoDb db) => db.Filter(todo => todo.Org == org).ToListAsync());

// RequireCors applies to both MapGet and MapPost
group.RequireCors("AllowAll");

// Call extension methods tied to a specific type derived from IEndpointRouteBuilder
group.Configure<RouteHandlerBuilder>(builder =>
{
    builder.WithTags("todos");
});

// Would RequireCors apply to the following? I think it would, but this either way
// it's confusing because it's not entirely clear what should happen.
// Feedback: It would apply.
group.MapDelete("/{id}", (int id, TodoDb db) => db.DeleteAsync(id));
```

## MapGroup returns a GroupConventionBuilder

```csharp
public class GroupRouteBuilder : IEndpointRouteBuilder { }
public class GroupConventionBuilder : IEndpointConventionBuilder
{
    public void Configure<TBuilder>(Action<TBuilder> configure) where TBuilder : IEndpointRouteBuilder;
}
public static class GroupEndpointRouteBuilderExtensions
{
    public static GroupConventionBuilder MapGroup(this IEndpointRouteBuilder endpoints, string pattern, Action<GroupRouteBuilder> configureGroup);
}
```

```csharp
var app = WebApplication.Create(args);
app.MapGroup("/todos", group =>
{
    group.MapGet("/", (int id, TodoDb db) => db.ToListAsync());
    group.MapGet("/{id}", (int id, TodoDb db) => db.GetAsync(id));
    group.MapPost("/", (Todo todo, TodoDb db) => db.AddAsync(todo));

    // string org cannot be an argument to the configureGroup callback because that would require MapGet and other
    // IEndpointRouteBuilder extension methods to be repeatedly called fore every request.
    group.MapGroup("/{org}", nestedGroup =>
    {
        nestedGroup.MapGet("/", (string org, TodoDb db) => db.Filter(todo => todo.Org == org).ToListAsync());
    }).RequireAuthorization();
}).RequireCors("AllowAll");
```

## MapGroup takes GroupRouteBuilder as a parameter

```csharp
public class GroupRouteBuilder : IEndpointRouteBuilder
{
    public GroupRouteBuilder(string pattern);
    // ...
}
public class GroupConventionBuilder : IEndpointConventionBuilder { }
public static class GroupEndpointRouteBuilderExtensions
{
    public static GroupConventionBuilder MapGroup(this IEndpointRouteBuilder endpoints, GroupeRouteBuilder group);
}
```

```csharp
var app = WebApplication.Create(args);

var group = new GroupRouteBuilder("/todos");

group.MapGet("/", (int id, TodoDb db) => db.ToListAsync());
group.MapGet("/{id}", (int id, TodoDb db) => db.GetAsync(id));
group.MapPost("/", (Todo todo, TodoDb db) => db.AddAsync(todo));

var nestedGroup = new GroupRouteBuilder("/{org}");
nestedGroup.MapGet("/", (string org, TodoDb db) => db.Filter(todo => todo.Org == org).ToListAsync());
group.MapGroup(nestedGroup);

app.MapGroup(group).RequireCors("AllowAll");

// Or, do we prefer to define the route prefix later?
// In the following example, GroupRouteBuilder would just have a default constructor.
app.MapGroup("/todos", group).RequireCors("AllowAll");
app.MapGroup("/todos2", group).RequireCors("AllowAll");

// Would we allow adding endpoints after calling MapGroup? If so, this has similar problems to option 1. 
group.MapDelete("/{id}", (int id, TodoDb db) => db.DeleteAsync(id));


// We'll also have to guard against recursion. The following would need to throw even if done before the call to app.MapGroup(group).
//nestedGroup.MapGroup(group);
```

# Open Questions
- What do we do if any of the `DataSources` in `GroupRouteBuilder.DataSources` don't expose their `IEndpointConventionBuilder`?
- What do we do if `EndpointBuilder` is not an `RouteEndpointBuilder` preventing us from prefixing the route pattern?
- What happens if a change token fires on one of the `DataSources` in `GroupRouteBuilder.DataSources`?
    - Do we support adding endpoints to a group after startup when the token fires? What about metadata/filters?
- Can we define a middleware that runs every time a route handler in a given group is hit before running the endpoint and similar middleware from inner groups?
    - How do we deal with middleware that checks the request path? Can we trim the group prefix from the path?
    - What if no endpoint is matched, but the middleware would have been terminal?
- Do we automatically add the group template as a ApiExplorer "controller" (OpenAPI ~group~ tag)?
    - Early consensus is ... no
    - Would the group name be the template prefix?
        - This could lead to unusual characters in tag names.
    - What about tags?
    - Does this affect OpenAPI client generators?
    - What if the method is already defined in a class?
        - Do we continue to use the class as the tag name?
        - We wouldn't want to override the tag for MVC controllers.
    - Of the above API Proposals, what should we choose?
        - Option 1 and Option 2 together.

# Problems
- How do we deal with extension methods for specific `IEndpointConventionBuilder` implementations like `RouteHandlerBuilder`, `ControllerActionEndpointConventionBuilder`, `HubEndpointConventionBuilder`, etc.. With the exception of `OpenApiRouteHandlerBuilderExtensions` and now `RouteHandlerFilterExtensions`, it doesn't look like we ship many of these.
    1. New versions of the extensions methods are created that work on a more common type.
        - This could make intellisense noisy.
        - These new versions could add `Metadata` via the `IEndpointConventionBuilder` interface.
        - Or maybe they could attempt to cast the `IEndpointConventionBuilder` to a the expected derived type. (e.g. cast to `RouteHandlerBuilder` to access `RouteHandlerFilters`)?
    2. Or, we create multiple group builder types.
        - Is there someway to leverage generics here? I doesn't seem likely.
- Even if we resolve the type issues, an `IEndpointRouteBuilder` has no ability to add metadata to an `Endpoint` added to an `EndpointDataSource` in `DataSources` today.
    - Can we expand `ModelEndpointDataSource` to allow access to it's `IEndpointConventionBuilder`s?
        - `MapController`'s `ControllerActionEndpointDataSource` already exposes this via its `DefaultBuilder` property.
        - These are internal types though. Do we define a public interface we attempt to as-cast `EndpointDataSource`s to?
    - How do we support `RouteHandlerBuilder.AddFilter()`?
        - The `IEndpointConventionBuilder` in `ModelEndpointDataSource` is the inner `DefaultEndpointConventionBuilder` and not the `RouteHandlerBuilder` that wraps this. This would casting to `RouteHandlerBuilder` to access `RouteHandlerFilters` impossible.
        - Can we construct `ModelEndpointDataSource` differently to expose the outer `RouteHandlerBuilder`?

# Appendix

## Important Types

### IEndpointRouteBuilder

```csharp
/// <summary>
/// Defines a contract for a route builder in an application. A route builder specifies the routes for
/// an application.
/// </summary>
public interface IEndpointRouteBuilder
{
    /// <summary>
    /// Creates a new <see cref="IApplicationBuilder"/>.
    /// </summary>
    /// <returns>The new <see cref="IApplicationBuilder"/>.</returns>
    IApplicationBuilder CreateApplicationBuilder();

    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> used to resolve services for routes.
    /// </summary>
    IServiceProvider ServiceProvider { get; }

    /// <summary>
    /// Gets the endpoint data sources configured in the builder.
    /// </summary>
    ICollection<EndpointDataSource> DataSources { get; }
}
```

### EndpointDataSource

```csharp
/// <summary>
/// Provides a collection of <see cref="Endpoint"/> instances.
/// </summary>
public abstract class EndpointDataSource
{
    /// <summary>
    /// Gets a <see cref="IChangeToken"/> used to signal invalidation of cached <see cref="Endpoint"/>
    /// instances.
    /// </summary>
    /// <returns>The <see cref="IChangeToken"/>.</returns>
    public abstract IChangeToken GetChangeToken();

    /// <summary>
    /// Returns a read-only collection of <see cref="Endpoint"/> instances.
    /// </summary>
    public abstract IReadOnlyList<Endpoint> Endpoints { get; }
}
```

### ModelEndpointDataSource

```csharp
internal class ModelEndpointDataSource : EndpointDataSource
{
    private readonly List<DefaultEndpointConventionBuilder> _endpointConventionBuilders;

    public ModelEndpointDataSource()
    {
        _endpointConventionBuilders = new List<DefaultEndpointConventionBuilder>();
    }

    public IEndpointConventionBuilder AddEndpointBuilder(EndpointBuilder endpointBuilder)
    {
        var builder = new DefaultEndpointConventionBuilder(endpointBuilder);
        _endpointConventionBuilders.Add(builder);

        return builder;
    }

    public override IChangeToken GetChangeToken()
    {
        return NullChangeToken.Singleton;
    }

    public override IReadOnlyList<Endpoint> Endpoints => _endpointConventionBuilders.Select(e => e.Build()).ToArray();

    // for testing
    internal IEnumerable<EndpointBuilder> EndpointBuilders => _endpointConventionBuilders.Select(b => b.EndpointBuilder);
}
```

### Endpoint
```csharp
/// <summary>
/// Represents a logical endpoint in an application.
/// </summary>
public class Endpoint
{
    // ctor...

    /// <summary>
    /// Gets the informational display name of this endpoint.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the collection of metadata associated with this endpoint.
    /// </summary>
    public EndpointMetadataCollection Metadata { get; }

    /// <summary>
    /// Gets the delegate used to process requests for the endpoint.
    /// </summary>
    public RequestDelegate? RequestDelegate { get; }

    /// <summary>
    /// Returns a string representation of the endpoint.
    /// </summary>
    public override string? ToString() => DisplayName ?? base.ToString();
}
```

### RouteEndpoint

```csharp
/// <summary>
/// Represents an <see cref="Endpoint"/> that can be used in URL matching or URL generation.
/// </summary>
public sealed class RouteEndpoint : Endpoint
{
    // ctor...

    /// <summary>
    /// Gets the order value of endpoint.
    /// </summary>
    /// <remarks>
    /// The order value provides absolute control over the priority
    /// of an endpoint. Endpoints with a lower numeric value of order have higher priority.
    /// </remarks>
    public int Order { get; }

    /// <summary>
    /// Gets the <see cref="RoutePattern"/> associated with the endpoint.
    /// </summary>
    public RoutePattern RoutePattern { get; }
}
```

### EndpointBuilder

```csharp
/// <summary>
/// A base class for building an new <see cref="Endpoint"/>.
/// </summary>
public abstract class EndpointBuilder
{
    /// <summary>
    /// Gets or sets the delegate used to process requests for the endpoint.
    /// </summary>
    public RequestDelegate? RequestDelegate { get; set; }

    /// <summary>
    /// Gets or sets the informational display name of this endpoint.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets the collection of metadata associated with this endpoint.
    /// </summary>
    public IList<object> Metadata { get; } = new List<object>();

    /// <summary>
    /// Creates an instance of <see cref="Endpoint"/> from the <see cref="EndpointBuilder"/>.
    /// </summary>
    /// <returns>The created <see cref="Endpoint"/>.</returns>
    public abstract Endpoint Build();
}
```

### RouteEndpointBuilder

```csharp
/// <summary>
/// Supports building a new <see cref="RouteEndpoint"/>.
/// </summary>
public sealed class RouteEndpointBuilder : EndpointBuilder
{
    /// <summary>
    /// Gets or sets the <see cref="RoutePattern"/> associated with this endpoint.
    /// </summary>
    public RoutePattern RoutePattern { get; set; }

    /// <summary>
    ///  Gets or sets the order assigned to the endpoint.
    /// </summary>
    public int Order { get; set; }

    // ctors...

    /// <inheritdoc />
    public override Endpoint Build()
    {
        if (RequestDelegate is null)
        {
            throw new InvalidOperationException($"{nameof(RequestDelegate)} must be specified to construct a {nameof(RouteEndpoint)}.");
        }

        var routeEndpoint = new RouteEndpoint(
            RequestDelegate,
            RoutePattern,
            Order,
            new EndpointMetadataCollection(Metadata),
            DisplayName);

        return routeEndpoint;
    }
}

```

### IEndpointConventionBuilder

```csharp
/// <summary>
/// Builds conventions that will be used for customization of <see cref="EndpointBuilder"/> instances.
/// </summary>
/// <remarks>
/// This interface is used at application startup to customize endpoints for the application.
/// </remarks>
public interface IEndpointConventionBuilder
{
    /// <summary>
    /// Adds the specified convention to the builder. Conventions are used to customize <see cref="EndpointBuilder"/> instances.
    /// </summary>
    /// <param name="convention">The convention to add to the builder.</param>
    void Add(Action<EndpointBuilder> convention);
}
```

### DefaultEndpointConventionBuilder

```csharp
internal class DefaultEndpointConventionBuilder : IEndpointConventionBuilder
{
    internal EndpointBuilder EndpointBuilder { get; }

    private List<Action<EndpointBuilder>>? _conventions;

    public DefaultEndpointConventionBuilder(EndpointBuilder endpointBuilder)
    {
        EndpointBuilder = endpointBuilder;
        _conventions = new();
    }

    public void Add(Action<EndpointBuilder> convention)
    {
        var conventions = _conventions;

        if (conventions is null)
        {
            throw new InvalidOperationException("Conventions cannot be added after building the endpoint");
        }

        conventions.Add(convention);
    }

    public Endpoint Build()
    {
        // Only apply the conventions once
        var conventions = Interlocked.Exchange(ref _conventions, null);

        if (conventions is not null)
        {
            foreach (var convention in conventions)
            {
                convention(EndpointBuilder);
            }
        }

        return EndpointBuilder.Build();
    }
}
```

### EndpointRouteBuilderExtensions.Map

```csharp
private static RouteHandlerBuilder Map(
    this IEndpointRouteBuilder endpoints,
    RoutePattern pattern,
    Delegate handler,
    bool disableInferBodyFromParameters)
{
    // ...

    var builder = new RouteEndpointBuilder(
        pattern,
        defaultOrder)
    {
        DisplayName = pattern.RawText ?? pattern.DebuggerToString(),
    };

    // ...

    var dataSource = endpoints.DataSources.OfType<ModelEndpointDataSource>().FirstOrDefault();
    if (dataSource is null)
    {
        dataSource = new ModelEndpointDataSource();
        endpoints.DataSources.Add(dataSource);
    }

    var routeHandlerBuilder = new RouteHandlerBuilder(dataSource.AddEndpointBuilder(builder));
    routeHandlerBuilder.Add(endpointBuilder =>
    {
        // ...

        // filteredRequestDelegateResult.RequestDelegate is derived from routeHandlerBuilder which is captured
        // by the lambda.
        endpointBuilder.RequestDelegate =  filteredRequestDelegateResult.RequestDelegate;
    });

    return routeHandlerBuilder;
}
```