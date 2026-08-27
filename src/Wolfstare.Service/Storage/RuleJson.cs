using System.Text.Json;
using System.Text.Json.Nodes;
using Wolfstare.Core.Rules;

namespace Wolfstare.Service.Storage;

/// <summary>
/// Serialises the closed <see cref="BlockRule"/> hierarchy with an explicit discriminator.
///
/// Written by hand rather than with polymorphic attributes so the stored shape is stable and
/// readable, and so an unrecognised discriminator can fail loudly. A rule silently dropped on
/// reload would leave the user believing they were blocked when they were not — the exact
/// failure the fail-closed rule in spec §2.2 exists to prevent.
/// </summary>
public static class RuleJson
{
    private const string KindDomain = "domain";
    private const string KindImageName = "app.imageName";
    private const string KindPublisher = "app.publisher";
    private const string KindFileDescription = "app.fileDescription";
    private const string KindPath = "path";

    public static string Serialise(IReadOnlyList<BlockRule> rules)
    {
        var array = new JsonArray();

        foreach (var rule in rules)
        {
            var (kind, value) = Describe(rule);
            array.Add(new JsonObject { ["kind"] = kind, ["value"] = value });
        }

        return array.ToJsonString();
    }

    public static IReadOnlyList<BlockRule> Deserialise(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Stored rules are not valid JSON.", ex);
        }

        if (node is not JsonArray array)
            throw new InvalidDataException("Stored rules are not a JSON array.");

        var rules = new List<BlockRule>(array.Count);

        foreach (var element in array)
        {
            var kind = element?["kind"]?.GetValue<string>()
                       ?? throw new InvalidDataException("A stored rule has no 'kind'.");
            var value = element?["value"]?.GetValue<string>()
                        ?? throw new InvalidDataException($"Stored rule '{kind}' has no 'value'.");

            rules.Add(Construct(kind, value));
        }

        return rules;
    }

    private static (string Kind, string Value) Describe(BlockRule rule) => rule switch
    {
        DomainRule r => (KindDomain, r.Pattern),
        AppRule { Matcher: ImageNameMatcher m } => (KindImageName, m.ImageName),
        AppRule { Matcher: PublisherMatcher m } => (KindPublisher, m.Publisher),
        AppRule { Matcher: FileDescriptionMatcher m } => (KindFileDescription, m.Description),
        PathRule r => (KindPath, r.UrlPattern),
        _ => throw new InvalidDataException($"Cannot serialise rule type '{rule.GetType().Name}'."),
    };

    private static BlockRule Construct(string kind, string value) => kind switch
    {
        KindDomain => new DomainRule(value),
        KindImageName => new AppRule(new ImageNameMatcher(value)),
        KindPublisher => new AppRule(new PublisherMatcher(value)),
        KindFileDescription => new AppRule(new FileDescriptionMatcher(value)),
        KindPath => new PathRule(value),
        _ => throw new InvalidDataException(
            $"Unknown rule kind '{kind}'. Refusing to load a partial rule set."),
    };
}
