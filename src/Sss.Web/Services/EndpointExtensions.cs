namespace Sss.Web.Services;

public static class EndpointExtensions
{
    /// <summary>Requires the capability on top of the group filter; 403 otherwise.</summary>
    public static TBuilder RequireCap<TBuilder>(this TBuilder b, Cap cap) where TBuilder : IEndpointConventionBuilder =>
        b.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<HelpdeskAccess>().Has(ctx.HttpContext, cap)
                ? await next(ctx)
                : Results.StatusCode(StatusCodes.Status403Forbidden));
}
