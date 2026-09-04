// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.InternalEdge;
using Dbr.Infrastructure.Monitoring;
using Dbr.Infrastructure.Removals;
using Dbr.Worker;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

// The same pipeline the API uses, and this is the process it matters most in: it is
// the one that will hold released fields, and the one running third-party page scripts
// while it does.
builder.AddDbrLogging();

builder.Services.AddDbrPersistence(builder.Configuration);

// A scheduled scan checks permission the same way a requested one does, which is the case
// the check exists for: somebody who withdrew consent last week is not searched for this
// month, and nothing has to remember to stop the schedule. That makes the consent policy
// version this process's configuration too.
builder.Services.AddDbrConsent(builder.Configuration);
builder.Services.AddDbrScanScheduling(builder.Configuration);

// Whether this process can reach the edge decides whether it can search at all, so it is
// read before the lanes are declared rather than discovered when the first leg arrives.
var internalApi = new InternalClientOptions();
builder.Configuration.GetSection(InternalClientOptions.SectionName).Bind(internalApi);

// One lane per broker, paced by that broker's catalog row, and now with something in them.
// Asking a company what it holds is the first of the two kinds of work these were built
// for; telling it to stop is still its own story.
//
// The lane is declared only when this deployment has been given certificates for the
// internal edge. A worker that cannot spend a grant cannot search — it would take a leg
// out of the queue, fail to open the identity, and record every company as unreachable.
// No consumer means the work stays in the lane until a worker that can reach the edge
// drains it, which is the difference between a scan that is waiting and one that has
// been answered wrongly.
builder.Services.AddDbrMessaging(builder.Configuration, lanes =>
{
    if (internalApi.Enabled)
    {
        lanes.Handle<ScanBrokerWork, ScanBrokerWorkHandler>();
    }

    // Telling a company to stop holding somebody's data, which is the other kind of work
    // these lanes were built for and the one that shares a company's pace with the first.
    //
    // Behind the same condition as the scan lane, now that an attempt carries a grant. A
    // worker that cannot reach the edge cannot open the identity a demand is made on behalf
    // of, so it would take an attempt out of the queue and record every company as
    // unreachable. No consumer means the work waits until a worker that can reach the edge
    // drains it, which is the difference between a demand that is queued and one that has
    // been answered wrongly.
    if (internalApi.Enabled)
    {
        lanes.Handle<RemovalJobWork, RemovalJobWorkHandler>();
    }
});

// Finding the runs nobody has started, and turning each into one piece of work per
// company. Deliberately no vault and no key manager: minting a grant writes a row of
// random bytes against the core store, so this process can plan a scan without ever
// acquiring the ability to open one.
builder.Services.AddDbrScanDispatch(builder.Configuration);

// And finding the demands nobody has sent, turning each into one piece of work for one
// company. The same deliberate absence: claiming a demand and writing an attempt are core
// store, so this process can send a demand without being able to open the identity it is
// made on behalf of.
builder.Services.AddDbrRemovalDispatch(builder.Configuration);

// Deliberately no key management here. This process drives browsers against
// third-party sites, so a credential that can decrypt would be a standing decryption
// right sitting in the most exposed part of the system. When a job needs a tenant's
// fields, it asks for a short-lived release of only those fields from the service
// that does hold the keys — which can refuse, and records that it was asked.
//
// This is that asking, and it is the whole of this process's reach into the vault: one
// grant at a time, to one address, holding a certificate that says which machine it is.
// It cannot enumerate grants, choose a different one, or ask for more of an identity
// than the grant it holds was minted for.
builder.Services.AddDbrInternalApiClient(builder.Configuration);

builder.Services.AddHostedService<Worker>();

// On unless a deployment turns it off. A scan somebody asked for and that nothing ever
// starts is the failure this exists to remove, so leaving it out should take a deliberate
// act rather than a missing setting.
var dispatch = new ScanDispatchOptions();
builder.Configuration.GetSection(ScanDispatchOptions.SectionName).Bind(dispatch);
dispatch.Validate();

if (dispatch.Enabled)
{
    builder.Services.AddHostedService<ScanDispatchService>();
}

// The same arrangement for demands, and its own switch: an operator who needs to stop
// sending demands while leaving scanning alone should not have to stop the process.
var removalDispatch = new RemovalDispatchOptions();
builder.Configuration.GetSection(RemovalDispatchOptions.SectionName).Bind(removalDispatch);
removalDispatch.Validate();

if (removalDispatch.Enabled)
{
    builder.Services.AddHostedService<RemovalDispatchService>();
}

var schedule = new ScanScheduleOptions();
builder.Configuration.GetSection(ScanScheduleOptions.SectionName).Bind(schedule);
schedule.Validate();

if (schedule.Enabled)
{
    builder.Services.AddQuartz(quartz =>
    {
        var job = new JobKey(nameof(ScheduledScanJob));

        quartz.AddJob<ScheduledScanJob>(job);

        // Daily, at an hour the operator picks. The monthly rhythm is not here — it comes
        // from the account id, which is what keeps every instance from asking every broker
        // on the same date.
        quartz.AddTrigger(trigger => trigger
            .ForJob(job)
            .WithIdentity($"{nameof(ScheduledScanJob)}-daily")
            .WithCronSchedule(
                $"0 0 {schedule.DailyAtHourUtc} ? * *",
                cron => cron.InTimeZone(TimeZoneInfo.Utc)));
    });

    // Waits for a running job rather than killing it mid-account on shutdown. A run
    // interrupted between two accounts is fine — tomorrow's run finds the same people due
    // — but one interrupted mid-write is a transaction the database has to sort out.
    builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
}

builder.Services.AddSingleton(TimeProvider.System);

var host = builder.Build();
host.Run();
