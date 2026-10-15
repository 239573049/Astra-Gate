using Astra.Core.Requests;
using Astra.Core.Tokens;

namespace Astra.Core.Tests;

public class RequestErrorCategoriesTests
{
    [Theory]
    [InlineData(RequestStatus.Success, 200, null, null)]
    [InlineData(RequestStatus.ClientCancelled, null, null, RequestErrorCategories.Cancelled)]
    [InlineData(RequestStatus.Blocked, 400, "invalid_request_error", RequestErrorCategories.Blocked)]
    [InlineData(RequestStatus.GatewayError, 429, TokenLimits.RefusedErrorType, RequestErrorCategories.Limit)]
    [InlineData(RequestStatus.GatewayError, 403, TokenLimits.RefusedErrorType, RequestErrorCategories.Limit)]
    [InlineData(RequestStatus.UpstreamError, 504, "timeout", RequestErrorCategories.Timeout)]
    [InlineData(RequestStatus.UpstreamError, 429, "rate_limit_error", RequestErrorCategories.RateLimited)]
    [InlineData(RequestStatus.GatewayError, 403, "permission_error", RequestErrorCategories.Auth)]
    [InlineData(RequestStatus.UpstreamError, 401, "authentication_error", RequestErrorCategories.Unavailable)]
    [InlineData(RequestStatus.UpstreamError, 400, "invalid_request_error", RequestErrorCategories.InvalidRequest)]
    [InlineData(RequestStatus.GatewayError, 501, "api_error", RequestErrorCategories.Unsupported)]
    [InlineData(RequestStatus.UpstreamError, 502, "upstream_unreachable", RequestErrorCategories.Unavailable)]
    [InlineData(RequestStatus.UpstreamError, 529, "overloaded_error", RequestErrorCategories.Unavailable)]
    [InlineData(RequestStatus.GatewayError, 503, "api_error", RequestErrorCategories.Unavailable)]
    [InlineData(RequestStatus.GatewayError, 500, "NullReferenceException", RequestErrorCategories.Internal)]
    [InlineData(RequestStatus.UpstreamError, 418, null, RequestErrorCategories.Other)]
    public void Failures_Map_To_A_Category_Without_Their_Message(string status, int? http, string? errorType, string? expected) =>
        Assert.Equal(expected, RequestErrorCategories.Of(status, http, errorType));
}
