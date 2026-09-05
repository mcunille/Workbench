// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Http;

public static class ApiProblemResults
{
    public static IResult AuthenticationFailed() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication failed.",
        type: "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.2");

    public static IResult InvalidRequest(string title) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: title,
        type: "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1");
}
