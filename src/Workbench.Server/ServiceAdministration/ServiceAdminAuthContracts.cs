// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.ServiceAdministration;

public sealed record ServiceAdminLoginRequest(string Email, string Password);
public sealed record CurrentServiceAdminResponse(Guid AccountId, string Email);
