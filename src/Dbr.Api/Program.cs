// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;
using Dbr.Infrastructure.DependencyInjection;

// The self-hosted composition root. The hosted build differs only in which
// implementations get registered here — never in the domain model or the pipeline,
// so a feature can't quietly become available in one deployment mode and not the
// other without someone editing this file.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbrPersistence(builder.Configuration);
builder.Services.AddDbrPasskeys(builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.MapAuthEndpoints();

app.Run();
