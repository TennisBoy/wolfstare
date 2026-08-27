using Wolfstare.Contracts;
using Wolfstare.Core.Rules;

namespace Wolfstare.Service.Api;

/// <summary>Translates between the wire rule shape and the domain hierarchy.</summary>
public static class RuleMapping
{
    /// <summary>
    /// Converts a wire rule, or returns null when the kind is unrecognised so the endpoint can
    /// answer 400 rather than throwing.
    /// </summary>
    public static BlockRule? ToDomain(RuleDto dto) => dto.Kind switch
    {
        "domain" => new DomainRule(dto.Value),
        "app.imageName" => new AppRule(new ImageNameMatcher(dto.Value)),
        "app.publisher" => new AppRule(new PublisherMatcher(dto.Value)),
        "app.fileDescription" => new AppRule(new FileDescriptionMatcher(dto.Value)),
        "path" => new PathRule(dto.Value),
        _ => null,
    };

    public static RuleDto ToDto(BlockRule rule) => rule switch
    {
        DomainRule r => new RuleDto("domain", r.Pattern),
        AppRule { Matcher: ImageNameMatcher m } => new RuleDto("app.imageName", m.ImageName),
        AppRule { Matcher: PublisherMatcher m } => new RuleDto("app.publisher", m.Publisher),
        AppRule { Matcher: FileDescriptionMatcher m } => new RuleDto("app.fileDescription", m.Description),
        PathRule r => new RuleDto("path", r.UrlPattern),
        _ => throw new InvalidOperationException($"Cannot map rule type '{rule.GetType().Name}'."),
    };

    public static BlockListDto ToDto(BlockList list)
        => new(list.Id, list.Name,
            list.Rules.Select(ToDto).ToList(),
            list.Allowlist.Select(ToDto).ToList());
}
