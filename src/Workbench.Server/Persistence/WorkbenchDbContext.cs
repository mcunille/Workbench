// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;

namespace Workbench.Server.Persistence;

public class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options) : DbContext(options);
