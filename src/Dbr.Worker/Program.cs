// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbrPersistence(builder.Configuration);

// The worker will need this to read the fields a removal job was granted. Registered
// here as well as in the API so that a misconfigured key manager stops the worker at
// startup rather than at the first job that needs to decrypt something.
builder.Services.AddDbrKeyManagement(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
