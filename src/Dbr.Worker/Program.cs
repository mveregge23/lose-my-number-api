// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Monitoring;
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

// One lane per broker, paced by that broker's catalog row. No consumers yet: the two
// kinds of work that will run in these lanes — asking a broker what it holds, and telling
// it to stop — are their own stories, and a lane declared for a consumer that does not
// exist would accept work nothing drains. What this gives them is somewhere to be
// registered that already knows how fast each company may be spoken to.
builder.Services.AddDbrMessaging(builder.Configuration);

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
