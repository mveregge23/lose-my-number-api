// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;

// The self-hosted composition root (§2.1). The hosted one differs only in which
// implementations get registered here — never in the domain model or the pipeline.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbrPersistence(builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
