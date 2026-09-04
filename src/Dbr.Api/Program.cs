// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Serialization;
using Dbr.Api.Authentication;
using Dbr.Api.Endpoints;
using Dbr.Api.InternalEdge;
using Dbr.Infrastructure.DependencyInjection;

// The self-hosted composition root. The hosted build differs only in which
// implementations get registered here — never in the domain model or the pipeline,
// so a feature can't quietly become available in one deployment mode and not the
// other without someone editing this file.
var builder = WebApplication.CreateBuilder(args);

// First, so that anything the rest of this file logs while starting up is already
// going through the redaction step rather than around it.
builder.AddDbrLogging();

builder.Services.AddDbrPersistence(builder.Configuration);
builder.Services.AddDbrPasskeys(builder.Configuration);
builder.Services.AddDbrSessions(builder.Configuration);
builder.Services.AddDbrKeyManagement(builder.Configuration);
builder.Services.AddDbrVault(builder.Configuration);
builder.Services.AddDbrSignup(builder.Configuration);
builder.Services.AddDbrConsent(builder.Configuration);
builder.Services.AddDbrCatalog();
builder.Services.AddDbrMonitoring();
builder.Services.AddDbrRemovals(builder.Configuration);

// Here and not in the worker, which is the whole point of it. This process holds the
// vault connection and the key-manager token, so it is the one that can turn a grant into
// plaintext; the process that talks to broker sites holds neither and asks this one.
builder.Services.AddDbrIdentityReleases(builder.Configuration);

// Where it asks. Off unless this deployment has been given certificates, in which case
// there is no internal listener and the routes behind it are mapped nowhere at all.
builder.AddDbrInternalEdge();

builder.Services.AddDbrBearerAuthentication();

// A field this API does not implement is refused rather than ignored. The default is to
// drop it silently, which on most routes is merely untidy and on POST /scans is a
// correctness problem: a client sending a name would get back a perfectly good scan of
// the caller's own profile, and its author would reasonably conclude that name-based
// search works. Refusing makes the shape of the request the answer to that question,
// which is what §10.4 asks the API to be. Applied everywhere rather than to one route,
// because "the fields we ignore" is not a contract worth having anywhere.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

var app = builder.Build();

// Before everything public, and that ordering is the point. A connection arriving on the
// internal listener turns off into its own branch here and never runs a step of the
// pipeline below — no bearer authentication, no tenant middleware, no public route table.
// The two edges share a process and a container; they do not share a request path.
app.UseDbrInternalEdge();

// Explicit, and deliberately after the branch above. Left implicit, the host inserts
// routing at the very front of the pipeline, where it matches a public route before the
// internal branch has been entered — the branch then has to undo that match rather than
// simply not make it. Both halves are in place; this is the one that means an internal
// request never touches the public route table at all.
app.UseRouting();

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
app.MapCatalogEndpoints();
app.MapScanEndpoints();
app.MapExposureEndpoints();
app.MapRemovalEndpoints();

app.Run();

// Top-level statements compile into an internal Program class, which the in-process
// test host cannot name from another assembly. Declaring it here is what lets the
// tests boot this exact composition root rather than a copy of it.
public partial class Program;
