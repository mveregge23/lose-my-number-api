// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Worker;

var builder = Host.CreateApplicationBuilder(args);

// The same pipeline the API uses, and this is the process it matters most in: it is
// the one that will hold released fields, and the one running third-party page scripts
// while it does.
builder.AddDbrLogging();

builder.Services.AddDbrPersistence(builder.Configuration);

// Deliberately no key management here. This process drives browsers against
// third-party sites, so a credential that can decrypt would be a standing decryption
// right sitting in the most exposed part of the system. When a job needs a tenant's
// fields, it will ask for a short-lived release of only those fields from the service
// that does hold the keys — which can refuse, and can record that it was asked.
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
