// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Every registration lives in one place a test can also call, for the reason written on it.
WorkerComposition.Configure(builder);

var host = builder.Build();
host.Run();
