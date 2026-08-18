// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Authentication;
using Dbr.Api.Endpoints;
using Dbr.Infrastructure.DependencyInjection;

// The self-hosted composition root. The hosted build differs only in which
// implementations get registered here — never in the domain model or the pipeline,
// so a feature can't quietly become available in one deployment mode and not the
// other without someone editing this file.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbrPersistence(builder.Configuration);
builder.Services.AddDbrPasskeys(builder.Configuration);
builder.Services.AddDbrSessions(builder.Configuration);
builder.Services.AddDbrKeyManagement(builder.Configuration);
builder.Services.AddDbrVault(builder.Configuration);
builder.Services.AddDbrSignup(builder.Configuration);
builder.Services.AddDbrConsent(builder.Configuration);
builder.Services.AddDbrBearerAuthentication();

var app = builder.Build();

// Order is the whole of this. Authentication decides whether the caller holds a valid
// token; the tenant step turns what it found into the account this request acts for,
// which becomes app.tenant_id on every connection the request opens; authorization
// then decides whether that account may reach the endpoint. Moving the tenant step
// above authentication would leave it reading a user nobody had verified.
app.UseAuthentication();
app.UseDbrTenantContext();
app.UseAuthorization();

app.MapGet("/", () => "Hello World!");
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapPasskeyEndpoints();
app.MapProfileEndpoints();
app.MapConsentEndpoints();

app.Run();

// Top-level statements compile into an internal Program class, which the in-process
// test host cannot name from another assembly. Declaring it here is what lets the
// tests boot this exact composition root rather than a copy of it.
public partial class Program;
