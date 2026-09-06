// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Operations;

public sealed class DependencyUnavailableException() : IOException("A required service is temporarily unavailable.");
