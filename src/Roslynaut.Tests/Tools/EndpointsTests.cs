using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>aspnet_endpoints, against stand-ins for ASP.NET Core's routing types: controllers and minimal APIs with groups.</summary>
public class EndpointsTests
{
    private const string AspNet = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public class ApiControllerAttribute : System.Attribute { }
            public class NonActionAttribute : System.Attribute { }
            public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
            public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
            public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
        }
        namespace Microsoft.AspNetCore.Authorization { public class AuthorizeAttribute : System.Attribute { } }
        namespace Microsoft.AspNetCore.Routing { public interface IEndpointRouteBuilder { } }
        namespace Microsoft.AspNetCore.Builder
        {
            using Microsoft.AspNetCore.Routing;
            public class RouteGroupBuilder : IEndpointRouteBuilder { }
            public class RouteHandlerBuilder { }
            public static class EndpointRouteBuilderExtensions
            {
                public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, System.Delegate handler) => new();
                public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, System.Delegate handler) => new();
                public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder endpoints, string prefix) => new();
            }
            public static class AuthorizationEndpointConventionBuilderExtensions { public static T RequireAuthorization<T>(this T builder) => builder; }
        }
        """;

    private static readonly Microsoft.CodeAnalysis.Solution Shop = TestSolution.Create(AspNet + """
        namespace Shop
        {
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;

            [ApiController, Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")] public int Get(int id) => id;
                [HttpPost, Microsoft.AspNetCore.Authorization.Authorize] public void Create() { }
                [HttpGet("/health")] public string Health() => "ok";
                [NonAction] public void Helper() { }
            }

            public static class Api
            {
                public static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
                {
                    app.MapGet("/ping", () => "pong");
                    var api = app.MapGroup("/v2");
                    var items = api.MapGroup("items").RequireAuthorization();
                    items.MapGet("{id}", Find).RequireAuthorization();
                    items.MapPost("", (int id) => id);
                }

                private static int Find(int id) => id;
            }
        }
        """);

    [Test]
    public async Task Endpoints_OfControllersAndMinimalApis()
    {
        var endpoints = await EndpointsTool.FindAsync(Shop, CancellationToken.None);

        endpoints.Select(endpoint => $"{endpoint.Method} {endpoint.Route} {endpoint.Handler}").Should().BeEquivalentTo(
            "GET /api/Orders/{id} Shop.OrdersController.Get",
            "POST /api/Orders Shop.OrdersController.Create",
            "GET /health Shop.OrdersController.Health",
            "GET /ping lambda in Shop.Api.Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)",
            "GET /v2/items/{id} Shop.Api.Find",
            "POST /v2/items lambda in Shop.Api.Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)");
        endpoints.Single(endpoint => endpoint.Method == "POST" && endpoint.Route == "/api/Orders").Metadata.Should().Contain("Authorize");
        endpoints.Single(endpoint => endpoint.Route == "/v2/items/{id}").Metadata.Should().Equal("RequireAuthorization");
    }
}
