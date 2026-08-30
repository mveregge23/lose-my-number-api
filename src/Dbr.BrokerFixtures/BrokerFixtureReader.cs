// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dbr.BrokerFixtures;

/// <param name="Sets">Every company's fixtures, when nothing is wrong with them.</param>
/// <param name="Problems">
/// Everything wrong, rather than the first thing wrong. Somebody fixing a manifest wants
/// the whole list — a validator that stops at the first error turns one review into four.
/// The same arrangement the legal-basis reader uses, for the same reason.
/// </param>
public sealed record FixtureReadResult(
    IReadOnlyList<BrokerFixtureSet> Sets,
    IReadOnlyList<string> Problems)
{
    /// <summary>One company's fixtures by id, or nothing.</summary>
    public BrokerFixtureSet? Find(string brokerId) =>
        Sets.FirstOrDefault(set => string.Equals(set.BrokerId, brokerId, StringComparison.Ordinal));
}

/// <summary>
/// Reads the recorded broker pages and says whether they are usable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shared half of "one fixture, two consumers".</b> §21.4 asks that a recipe's
/// fixture set be the pipeline test's scenario library as well as the thing CI dry-runs a
/// recipe against, and the way that stays true is for both to arrive at a scenario through
/// this — rather than one of them reading files and the other reading a copy that has since
/// drifted. The server beside this is one of the two consumers, not the owner.
/// </para>
/// <para>
/// <b>Read from disk rather than out of the assembly</b>, which is the opposite of what the
/// legal-basis reader does. That one is embedded because a deployed image must not be able
/// to disagree with a mounted path. A fixture is never deployed — both consumers run from a
/// checkout — so the question is not whether the artifact can disagree with the disk but
/// whether both consumers read the same files, and reading them is the plainest way to say
/// yes.
/// </para>
/// <para>
/// Validation is strict about what a reviewer cannot see. A scenario pointing at a body
/// file that is not there, two scenarios sharing a name, a manifest naming one company from
/// inside another's directory: each reads perfectly well in a diff, and each produces a
/// test that passes by testing nothing.
/// </para>
/// </remarks>
public static class BrokerFixtureReader
{
    /// <summary>Where the fixtures sit relative to whatever is reading them.</summary>
    /// <remarks>
    /// The layout §21.4 specifies — <c>/catalog/brokers/{id}/fixtures/</c> — preserved
    /// through the copy that puts them beside the binary, so the path a contributor edits
    /// and the path a test reads differ only in their root.
    /// </remarks>
    public const string DefaultRoot = "catalog/brokers";

    private const string ManifestName = "fixtures.yaml";

    /// <summary>Reads every company's fixtures from beside the running assembly.</summary>
    public static FixtureReadResult Read() =>
        Read(Path.Combine(AppContext.BaseDirectory, DefaultRoot));

    /// <summary>Reads every company's fixtures under one directory.</summary>
    public static FixtureReadResult Read(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return new FixtureReadResult(
                [],
                [
                    $"There is no fixture directory at '{root}'. Recorded broker pages live in "
                    + $"{DefaultRoot}/{{brokerId}}/fixtures/ and are copied beside whatever reads "
                    + "them; an empty output usually means the copy did not run.",
                ]);
        }

        var sets = new List<BrokerFixtureSet>();
        var problems = new List<string>();

        // Ordered so two runs read the same companies in the same order, which is what makes
        // one failing run's output comparable with another's.
        var directories = Directory
            .EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var set = ReadOne(directory, problems);

            if (set is not null)
            {
                sets.Add(set);
            }
        }

        return new FixtureReadResult(sets, problems);
    }

    /// <summary>
    /// Which outcomes no recorded page anywhere demonstrates.
    /// </summary>
    /// <remarks>
    /// <b>Across the library rather than per company</b>, and the distinction is the whole
    /// point. §21.4 asks that the result type be exercised "somewhere rather than only the
    /// happy path" — not that every broker be made to rate-limit us. Requiring it per
    /// company would turn adding a broker into an exercise in inventing seven pages, most
    /// of which that company never serves.
    /// </remarks>
    public static IReadOnlyList<SearchExpectation> Uncovered(FixtureReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var covered = read.Sets
            .SelectMany(set => set.Scenarios)
            .Select(scenario => scenario.Expect)
            .ToHashSet();

        return [.. FixtureVocabulary.EveryExpectation.Where(expected => !covered.Contains(expected))];
    }

    private static BrokerFixtureSet? ReadOne(string directory, List<string> problems)
    {
        var brokerId = Path.GetFileName(directory);
        var fixtures = Path.Combine(directory, "fixtures");
        var manifestPath = Path.Combine(fixtures, ManifestName);

        if (!File.Exists(manifestPath))
        {
            problems.Add(
                $"'{brokerId}' has no {ManifestName}. A directory of recorded pages with nothing "
                + "saying what they are is a wall of somebody else's markup.");

            return null;
        }

        ManifestFile? manifest;

        try
        {
            manifest = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ManifestFile>(File.ReadAllText(manifestPath));
        }
        catch (YamlException exception)
        {
            problems.Add($"'{brokerId}' has a {ManifestName} that is not valid YAML: {exception.Message}");

            return null;
        }

        if (manifest is null)
        {
            problems.Add($"'{brokerId}' has an empty {ManifestName}.");

            return null;
        }

        if (!string.Equals(manifest.Broker, brokerId, StringComparison.Ordinal))
        {
            // A manifest naming one company from inside another's directory would serve the
            // wrong pages to a recipe that asked for the right ones, and the tests would
            // look like they passed.
            problems.Add(
                $"'{brokerId}' contains a manifest naming '{manifest.Broker}'. The directory is "
                + "the company's identity, so the two have to agree.");

            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            problems.Add($"'{brokerId}' does not say what company it is recording.");
        }

        var scenarios = new List<FixtureScenario>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declared in manifest.Scenarios ?? [])
        {
            var scenario = ReadScenario(brokerId, fixtures, declared, problems);

            if (scenario is null)
            {
                continue;
            }

            if (!names.Add(scenario.Name))
            {
                problems.Add(
                    $"'{brokerId}' declares two scenarios called '{scenario.Name}'. A test asks "
                    + "for one by name, so the second would be unreachable.");

                continue;
            }

            scenarios.Add(scenario);
        }

        if (scenarios.Count == 0)
        {
            problems.Add($"'{brokerId}' has no usable scenarios.");
        }

        return new BrokerFixtureSet(brokerId, manifest.Description ?? string.Empty, scenarios);
    }

    private static FixtureScenario? ReadScenario(
        string brokerId,
        string fixtures,
        ScenarioFile declared,
        List<string> problems)
    {
        var where = $"'{brokerId}' scenario '{declared.Name ?? "(unnamed)"}'";

        if (string.IsNullOrWhiteSpace(declared.Name))
        {
            problems.Add($"'{brokerId}' declares a scenario with no name, so nothing could ask for it.");

            return null;
        }

        if (FixtureVocabulary.ParseKind(declared.Kind) is not { } kind)
        {
            problems.Add(
                $"{where} is of kind '{declared.Kind}', which this build has no meaning for. The "
                + "only kind today is 'search'; the removal side arrives with the result type it "
                + "would be declared against.");

            return null;
        }

        if (FixtureVocabulary.ParseExpectation(declared.Expect) is not { } expect)
        {
            problems.Add(
                $"{where} expects '{declared.Expect}', which is not something a search can "
                + "conclude. A fixture that names no outcome records a page and proves nothing "
                + "about it.");

            return null;
        }

        if (string.IsNullOrWhiteSpace(declared.Description))
        {
            problems.Add(
                $"{where} does not say what it is for. A recorded page is a wall of somebody "
                + "else's markup, and that is the one question a diff cannot answer.");
        }

        var responses = new List<FixtureResponse>();

        foreach (var response in declared.Responses ?? [])
        {
            var read = ReadResponse(where, fixtures, response, problems);

            if (read is not null)
            {
                responses.Add(read);
            }
        }

        if (responses.Count == 0)
        {
            problems.Add($"{where} serves nothing, so a recipe pointed at it would get a 404.");

            return null;
        }

        return new FixtureScenario(
            declared.Name,
            kind,
            expect,
            declared.Description ?? string.Empty,
            responses);
    }

    private static FixtureResponse? ReadResponse(
        string where,
        string fixtures,
        ResponseFile declared,
        List<string> problems)
    {
        var status = declared.Status ?? 200;

        if (status is < 100 or > 599)
        {
            problems.Add($"{where} answers with status {status}, which is not one.");

            return null;
        }

        var body = string.Empty;

        if (!string.IsNullOrWhiteSpace(declared.Body))
        {
            var path = Path.Combine(fixtures, declared.Body);

            if (!File.Exists(path))
            {
                problems.Add(
                    $"{where} points at '{declared.Body}', which is not in its fixtures "
                    + "directory. A scenario serving a page that is not there is a test that "
                    + "passes by testing nothing.");

                return null;
            }

            body = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(body))
            {
                problems.Add($"{where} points at '{declared.Body}', which is empty.");

                return null;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in declared.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{where} declares a header with no name or no value.");

                continue;
            }

            headers[name] = value;
        }

        return new FixtureResponse(
            NormalisePath(declared.Path),
            status,
            headers,
            body,

            // Declared rather than guessed from the extension. A recorded 429 is usually not
            // HTML at all, and a search that branches on the content type should see what the
            // company actually sent.
            declared.ContentType ?? "text/html; charset=utf-8");
    }

    /// <summary>A path a request can be compared against, or nothing for "anything".</summary>
    private static string? NormalisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    // The YAML shapes, kept private and separate from the types everything else uses. What
    // a file is allowed to say and what a scenario is are different questions, and a single
    // type answering both would make every property nullable for the benefit of the parser.
    private sealed class ManifestFile
    {
        public string? Broker { get; set; }

        public string? Description { get; set; }

        public List<ScenarioFile>? Scenarios { get; set; }
    }

    private sealed class ScenarioFile
    {
        public string? Name { get; set; }

        public string? Kind { get; set; }

        public string? Expect { get; set; }

        public string? Description { get; set; }

        public List<ResponseFile>? Responses { get; set; }
    }

    private sealed class ResponseFile
    {
        public string? Path { get; set; }

        public int? Status { get; set; }

        public string? ContentType { get; set; }

        public Dictionary<string, string>? Headers { get; set; }

        public string? Body { get; set; }
    }
}
