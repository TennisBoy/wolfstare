# Wolfstare Phase 1: Core Domain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Wolfstare.Core`, the dependency-free domain library containing all rule matching, lock semantics, clock-tamper handling, and stop-authorisation policy, with a complete unit test suite.

**Architecture:** A pure C# class library with no Windows-specific references. Enforcement is expressed through interfaces that Core defines and later phases implement. Every behaviour that decides whether something is blocked, or whether a lock may be released, lives here — so it can be tested in milliseconds without a service, admin rights, or a hijacked DNS resolver.

**Tech Stack:** .NET 9, C# 13, xUnit v3, `System.Security.Cryptography` (PBKDF2). No third-party dependencies in Core.

**Spec:** `docs/superpowers/specs/2026-08-27-wolfstare-design.md`

## Global Constraints

- Target framework: `net9.0` for `Wolfstare.Core` (NOT `net9.0-windows` — this is enforced).
- `Wolfstare.Core.csproj` must have zero `PackageReference` entries and zero Windows-specific references. A reference to `Microsoft.Win32.Registry`, `System.Management`, or any `*-windows` TFM is a defect.
- `Nullable` enabled, `TreatWarningsAsErrors` enabled, `ImplicitUsings` enabled.
- All domain types are `record` or `sealed record` — immutable by default.
- PBKDF2-HMAC-SHA256, 600,000 iterations, 128-bit salt, 256-bit derived key (spec §4.3).
- Password verification must be constant-time (`CryptographicOperations.FixedTimeEquals`).
- Every failure path fails closed: when a decision is ambiguous or state is invalid, the answer is "blocked" / "still locked" (spec §2.2).
- Commit after every task with passing tests.

---

### Task 1: Solution scaffold and test harness

**Files:**
- Create: `Wolfstare.sln`
- Create: `src/Wolfstare.Core/Wolfstare.Core.csproj`
- Create: `Directory.Build.props`
- Create: `tests/Wolfstare.Core.Tests/Wolfstare.Core.Tests.csproj`
- Create: `tests/Wolfstare.Core.Tests/HarnessTests.cs`
- Create: `.gitattributes`

**Interfaces:**
- Consumes: nothing
- Produces: a buildable solution where `dotnet test` runs. Later tasks add files to these two projects.

- [ ] **Step 1: Create the solution and projects**

```bash
cd "C:/Users/yinxi/source/repos/wolfstare"
dotnet new sln -n Wolfstare
dotnet new classlib -n Wolfstare.Core -o src/Wolfstare.Core -f net9.0
dotnet new xunit3 -n Wolfstare.Core.Tests -o tests/Wolfstare.Core.Tests -f net9.0
rm src/Wolfstare.Core/Class1.cs
dotnet sln add src/Wolfstare.Core/Wolfstare.Core.csproj
dotnet sln add tests/Wolfstare.Core.Tests/Wolfstare.Core.Tests.csproj
dotnet add tests/Wolfstare.Core.Tests reference src/Wolfstare.Core
```

- [ ] **Step 2: Add `Directory.Build.props` enforcing the global constraints**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Add `.gitattributes` to normalise line endings**

```
* text=auto eol=lf
*.sln text eol=crlf
*.csproj text eol=crlf
*.props text eol=crlf
*.cmd text eol=crlf
*.ps1 text eol=crlf
```

- [ ] **Step 4: Write a harness test that proves the wiring**

```csharp
namespace Wolfstare.Core.Tests;

public class HarnessTests
{
    [Fact]
    public void CoreAssemblyIsReferencable()
    {
        var assembly = typeof(Wolfstare.Core.CoreMarker).Assembly;
        Assert.Equal("Wolfstare.Core", assembly.GetName().Name);
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test`
Expected: FAIL — `CoreMarker` does not exist.

- [ ] **Step 6: Add the marker type**

Create `src/Wolfstare.Core/CoreMarker.cs`:

```csharp
namespace Wolfstare.Core;

/// <summary>Anchor type for assembly identity in tests and DI scanning.</summary>
public static class CoreMarker;
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test`
Expected: PASS, 1 test.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add solution scaffold with Core library and xUnit harness"
```

---

### Task 2: Domain normalisation and pattern matching

Spec §4.1. This is the matching engine the DNS sinkhole will call on every lookup.

**Matching semantics (decided here, binding on all later phases):**
- `*` matches every host.
- `reddit.com` matches `reddit.com` **and** every subdomain (`www.reddit.com`, `a.b.reddit.com`). This is what users expect when they type a bare domain.
- `*.reddit.com` matches subdomains **only** — not the apex.
- Matching is case-insensitive, ignores a trailing dot, and compares punycode (so `münchen.de` and `xn--mnchen-3ya.de` are the same host).
- Label-boundary safety: `reddit.com` must NOT match `notreddit.com`.

**Files:**
- Create: `src/Wolfstare.Core/Rules/DomainMatcher.cs`
- Test: `tests/Wolfstare.Core.Tests/Rules/DomainMatcherTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `static string DomainMatcher.Normalize(string host)`
  - `static bool DomainMatcher.Matches(string pattern, string host)`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class DomainMatcherTests
{
    [Theory]
    [InlineData("Reddit.COM", "reddit.com")]
    [InlineData("reddit.com.", "reddit.com")]
    [InlineData("  reddit.com  ", "reddit.com")]
    [InlineData("münchen.de", "xn--mnchen-3ya.de")]
    public void NormalizeCanonicalisesHost(string input, string expected)
        => Assert.Equal(expected, DomainMatcher.Normalize(input));

    [Theory]
    [InlineData("*", "anything.example")]
    [InlineData("*", "reddit.com")]
    public void StarMatchesEverything(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("reddit.com", "reddit.com")]
    [InlineData("reddit.com", "www.reddit.com")]
    [InlineData("reddit.com", "a.b.reddit.com")]
    [InlineData("reddit.com", "REDDIT.com")]
    public void BareDomainMatchesApexAndSubdomains(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("reddit.com", "notreddit.com")]
    [InlineData("reddit.com", "reddit.com.evil.example")]
    [InlineData("reddit.com", "example.com")]
    public void BareDomainRespectsLabelBoundaries(string pattern, string host)
        => Assert.False(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("*.reddit.com", "www.reddit.com")]
    [InlineData("*.reddit.com", "a.b.reddit.com")]
    public void WildcardMatchesSubdomains(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Fact]
    public void WildcardDoesNotMatchApex()
        => Assert.False(DomainMatcher.Matches("*.reddit.com", "reddit.com"));

    [Theory]
    [InlineData("", "reddit.com")]
    [InlineData("reddit.com", "")]
    public void EmptyInputsDoNotMatch(string pattern, string host)
        => Assert.False(DomainMatcher.Matches(pattern, host));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter DomainMatcherTests`
Expected: FAIL — `DomainMatcher` does not exist.

- [ ] **Step 3: Implement `DomainMatcher`**

```csharp
using System.Globalization;

namespace Wolfstare.Core.Rules;

/// <summary>
/// Host normalisation and pattern matching for <see cref="DomainRule"/>.
/// Called on every DNS lookup, so it allocates as little as it reasonably can.
/// </summary>
public static class DomainMatcher
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true, UseStd3AsciiRules = false };

    /// <summary>Lowercases, trims, strips a trailing dot, and converts to punycode.</summary>
    public static string Normalize(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;

        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0) return string.Empty;

        try
        {
            // GetAscii also lowercases ASCII labels as part of its mapping.
            return Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Not a valid IDN — fall back to a plain lowercase comparison rather
            // than throwing. Fail closed: an unparseable host still gets matched
            // against patterns rather than silently permitted.
            return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>
    /// True when <paramref name="host"/> is covered by <paramref name="pattern"/>.
    /// See the plan's Task 2 header for the exact semantics.
    /// </summary>
    public static bool Matches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var p = pattern.Trim();
        if (p == "*") return !string.IsNullOrWhiteSpace(host);

        var h = Normalize(host);
        if (h.Length == 0) return false;

        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = Normalize(p[2..]);
            return suffix.Length != 0 && IsProperSubdomainOf(h, suffix);
        }

        var bare = Normalize(p);
        if (bare.Length == 0) return false;

        return h.Equals(bare, StringComparison.Ordinal) || IsProperSubdomainOf(h, bare);
    }

    /// <summary>True when <paramref name="host"/> sits strictly beneath <paramref name="parent"/>.</summary>
    private static bool IsProperSubdomainOf(string host, string parent)
        => host.Length > parent.Length + 1
           && host.EndsWith(parent, StringComparison.Ordinal)
           && host[host.Length - parent.Length - 1] == '.';
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter DomainMatcherTests`
Expected: PASS, all cases.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add domain normalisation and wildcard pattern matching"
```

---

### Task 3: Rule types and application matching

Spec §4.1. `AppRule` matches on executable *identity*, never install path — this is what makes a rule for a not-yet-installed application work.

**Files:**
- Create: `src/Wolfstare.Core/Rules/BlockRule.cs`
- Create: `src/Wolfstare.Core/Rules/AppMatcher.cs`
- Create: `src/Wolfstare.Core/Rules/ProcessIdentity.cs`
- Test: `tests/Wolfstare.Core.Tests/Rules/AppMatcherTests.cs`

**Interfaces:**
- Consumes: `DomainMatcher` (Task 2)
- Produces:
  - `abstract record BlockRule`; `sealed record DomainRule(string Pattern)`, `sealed record AppRule(AppMatcher Matcher)`, `sealed record PathRule(string UrlPattern)`
  - `abstract record AppMatcher`; `ImageNameMatcher(string ImageName)`, `PublisherMatcher(string Publisher)`, `FileDescriptionMatcher(string Description)`
  - `sealed record ProcessIdentity(string ImageName, string? Publisher, string? FileDescription)`
  - `bool AppMatcher.Matches(ProcessIdentity identity)`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class AppMatcherTests
{
    private static ProcessIdentity Identity(
        string image = "app.exe", string? publisher = null, string? description = null)
        => new(image, publisher, description);

    [Theory]
    [InlineData("4kvideodownloaderplus.exe", "4kvideodownloaderplus.exe")]
    [InlineData("4KVideoDownloaderPlus.EXE", "4kvideodownloaderplus.exe")]
    [InlineData("4kvideodownloaderplus", "4kvideodownloaderplus.exe")]
    public void ImageNameMatchesCaseInsensitivelyAndToleratesMissingExtension(
        string pattern, string actual)
        => Assert.True(new ImageNameMatcher(pattern).Matches(Identity(image: actual)));

    [Fact]
    public void ImageNameDoesNotMatchDifferentExecutable()
        => Assert.False(new ImageNameMatcher("steam.exe").Matches(Identity(image: "notepad.exe")));

    [Fact]
    public void ImageNameIgnoresAnyPathSuppliedByTheCaller()
        => Assert.True(new ImageNameMatcher("steam.exe")
            .Matches(Identity(image: @"C:\Program Files\Steam\steam.exe")));

    [Fact]
    public void PublisherMatchesAsCaseInsensitiveSubstring()
        => Assert.True(new PublisherMatcher("Open Media")
            .Matches(Identity(publisher: "CN=Open Media LLC, O=Open Media LLC, C=CY")));

    [Fact]
    public void PublisherDoesNotMatchWhenIdentityIsUnsigned()
        => Assert.False(new PublisherMatcher("Open Media").Matches(Identity(publisher: null)));

    [Fact]
    public void FileDescriptionMatchesAsCaseInsensitiveSubstring()
        => Assert.True(new FileDescriptionMatcher("video downloader")
            .Matches(Identity(description: "4K Video Downloader Plus")));

    [Fact]
    public void FileDescriptionDoesNotMatchWhenAbsent()
        => Assert.False(new FileDescriptionMatcher("video").Matches(Identity(description: null)));

    [Fact]
    public void RenamedExecutableIsStillCaughtByPublisher()
    {
        // The scenario the ETW backstop exists for: user renames the exe to dodge IFEO.
        var renamed = Identity(image: "totally-not-a-downloader.exe", publisher: "CN=Open Media LLC");
        Assert.False(new ImageNameMatcher("4kvideodownloaderplus.exe").Matches(renamed));
        Assert.True(new PublisherMatcher("Open Media LLC").Matches(renamed));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter AppMatcherTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `ProcessIdentity`**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>
/// The identity of a running or launching process, as observed by the enforcement layer.
/// <paramref name="Publisher"/> and <paramref name="FileDescription"/> are null when the
/// executable is unsigned or carries no version resource.
/// </summary>
/// <param name="ImageName">Executable name. May be a full path; matching uses the filename.</param>
public sealed record ProcessIdentity(string ImageName, string? Publisher, string? FileDescription);
```

- [ ] **Step 4: Implement `AppMatcher` and its cases**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>How an <see cref="AppRule"/> recognises an executable.</summary>
public abstract record AppMatcher
{
    public abstract bool Matches(ProcessIdentity identity);
}

/// <summary>
/// Matches on filename alone, never on install path — which is what lets a rule be written
/// for an application that is not installed yet.
/// </summary>
public sealed record ImageNameMatcher(string ImageName) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
    {
        var wanted = Canonical(ImageName);
        var actual = Canonical(identity.ImageName);
        return wanted.Length != 0 && wanted.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strips any directory and normalises to a lowercase name ending in ".exe".</summary>
    private static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var name = value.Trim().Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }
}

/// <summary>Matches the Authenticode certificate subject as a case-insensitive substring.</summary>
public sealed record PublisherMatcher(string Publisher) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
        => !string.IsNullOrWhiteSpace(Publisher)
           && identity.Publisher is { } actual
           && actual.Contains(Publisher, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Matches the Win32 version-info FileDescription as a case-insensitive substring.</summary>
public sealed record FileDescriptionMatcher(string Description) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
        => !string.IsNullOrWhiteSpace(Description)
           && identity.FileDescription is { } actual
           && actual.Contains(Description, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Implement `BlockRule`**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>A single blocking rule. Closed hierarchy — see spec §4.1.</summary>
public abstract record BlockRule;

/// <summary>Blocks a domain. Pattern may be "*", "example.com", or "*.example.com".</summary>
public sealed record DomainRule(string Pattern) : BlockRule
{
    public bool Matches(string host) => DomainMatcher.Matches(Pattern, host);
}

/// <summary>Blocks an application by identity.</summary>
public sealed record AppRule(AppMatcher Matcher) : BlockRule
{
    public bool Matches(ProcessIdentity identity) => Matcher.Matches(identity);
}

/// <summary>
/// Blocks a specific URL path. Modelled now so the domain does not change when HTTPS
/// inspection lands, but rejected by <c>RuleValidator</c> in v1 (spec §4.1).
/// </summary>
public sealed record PathRule(string UrlPattern) : BlockRule;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter AppMatcherTests`
Expected: PASS, all cases.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add block rule types and application identity matching"
```

---

### Task 4: Rule set evaluation with allowlist precedence

Spec §4.2. Allowlist wins over block rules. This is what makes "block the whole internet except these three sites" expressible.

**Files:**
- Create: `src/Wolfstare.Core/Rules/RuleSet.cs`
- Test: `tests/Wolfstare.Core.Tests/Rules/RuleSetTests.cs`

**Interfaces:**
- Consumes: `BlockRule`, `DomainRule`, `AppRule`, `ProcessIdentity` (Task 3)
- Produces:
  - `enum Decision { Permit, Deny }`
  - `sealed record RuleSet(IReadOnlyList<BlockRule> Rules, IReadOnlyList<BlockRule> Allowlist)`
  - `Decision RuleSet.EvaluateDomain(string host)`
  - `Decision RuleSet.EvaluateApp(ProcessIdentity identity)`
  - `static RuleSet RuleSet.Empty`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class RuleSetTests
{
    private static RuleSet Set(BlockRule[] rules, BlockRule[]? allow = null)
        => new(rules, allow ?? []);

    [Fact]
    public void EmptySetPermitsEverything()
    {
        Assert.Equal(Decision.Permit, RuleSet.Empty.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Permit, RuleSet.Empty.EvaluateApp(new("steam.exe", null, null)));
    }

    [Fact]
    public void MatchingDomainRuleDenies()
    {
        var set = Set([new DomainRule("reddit.com")]);
        Assert.Equal(Decision.Deny, set.EvaluateDomain("www.reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("example.com"));
    }

    [Fact]
    public void AllowlistOverridesBlockRule()
    {
        var set = Set([new DomainRule("reddit.com")], [new DomainRule("old.reddit.com")]);
        Assert.Equal(Decision.Deny, set.EvaluateDomain("www.reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("old.reddit.com"));
    }

    [Fact]
    public void WholeInternetExceptAllowlist()
    {
        var set = Set([new DomainRule("*")], [new DomainRule("github.com"), new DomainRule("docs.microsoft.com")]);
        Assert.Equal(Decision.Deny, set.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Deny, set.EvaluateDomain("news.ycombinator.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("github.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("api.github.com"));
    }

    [Fact]
    public void AppRulesAreEvaluatedIndependentlyOfDomainRules()
    {
        var set = Set([new AppRule(new ImageNameMatcher("steam.exe"))]);
        Assert.Equal(Decision.Deny, set.EvaluateApp(new("steam.exe", null, null)));
        Assert.Equal(Decision.Permit, set.EvaluateApp(new("notepad.exe", null, null)));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("steam.exe"));
    }

    [Fact]
    public void AllowlistOverridesAppRule()
    {
        var set = Set(
            [new AppRule(new PublisherMatcher("Valve"))],
            [new AppRule(new ImageNameMatcher("steam-cleanup.exe"))]);

        Assert.Equal(Decision.Deny, set.EvaluateApp(new("steam.exe", "CN=Valve Corp", null)));
        Assert.Equal(Decision.Permit, set.EvaluateApp(new("steam-cleanup.exe", "CN=Valve Corp", null)));
    }

    [Fact]
    public void PathRulesAreIgnoredByDomainEvaluationInV1()
    {
        // PathRule is modelled but not enforced until HTTPS inspection lands.
        var set = Set([new PathRule("reddit.com/r/all")]);
        Assert.Equal(Decision.Permit, set.EvaluateDomain("reddit.com"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter RuleSetTests`
Expected: FAIL — `RuleSet` does not exist.

- [ ] **Step 3: Implement `RuleSet`**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>The outcome of evaluating a host or process against a <see cref="RuleSet"/>.</summary>
public enum Decision
{
    Permit,
    Deny,
}

/// <summary>
/// A named set of block rules plus an allowlist that overrides them.
/// Evaluation order is allowlist first, so "block everything except these" is expressible
/// as a single <c>DomainRule("*")</c> with a populated allowlist (spec §4.2).
/// </summary>
public sealed record RuleSet(IReadOnlyList<BlockRule> Rules, IReadOnlyList<BlockRule> Allowlist)
{
    public static RuleSet Empty { get; } = new([], []);

    public Decision EvaluateDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Decision.Permit;
        if (AnyDomainMatch(Allowlist, host)) return Decision.Permit;
        return AnyDomainMatch(Rules, host) ? Decision.Deny : Decision.Permit;
    }

    public Decision EvaluateApp(ProcessIdentity identity)
    {
        if (AnyAppMatch(Allowlist, identity)) return Decision.Permit;
        return AnyAppMatch(Rules, identity) ? Decision.Deny : Decision.Permit;
    }

    private static bool AnyDomainMatch(IReadOnlyList<BlockRule> rules, string host)
    {
        for (var i = 0; i < rules.Count; i++)
            if (rules[i] is DomainRule d && d.Matches(host))
                return true;
        return false;
    }

    private static bool AnyAppMatch(IReadOnlyList<BlockRule> rules, ProcessIdentity identity)
    {
        for (var i = 0; i < rules.Count; i++)
            if (rules[i] is AppRule a && a.Matches(identity))
                return true;
        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter RuleSetTests`
Expected: PASS, all cases.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add rule set evaluation with allowlist precedence"
```

---

### Task 5: Critical-process guard and rule validation

Spec §8.3. An IFEO key on `explorer.exe` leaves the machine with no desktop shell, and the uninstaller refuses to run during a lock — so recovery would mean safe mode. This guard lives in Core specifically so it is unit-tested and rejects the rule at **creation** time, not at apply time.

**Files:**
- Create: `src/Wolfstare.Core/Rules/CriticalProcesses.cs`
- Create: `src/Wolfstare.Core/Rules/RuleValidator.cs`
- Test: `tests/Wolfstare.Core.Tests/Rules/RuleValidatorTests.cs`

**Interfaces:**
- Consumes: `BlockRule` and its cases (Task 3)
- Produces:
  - `static bool CriticalProcesses.IsProtected(string imageName)`
  - `static IReadOnlySet<string> CriticalProcesses.Names`
  - `readonly record struct ValidationResult(bool IsValid, string? Error)` with `ValidationResult.Ok`
  - `static ValidationResult RuleValidator.Validate(BlockRule rule)`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class RuleValidatorTests
{
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData("explorer")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData("lsass.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("csrss.exe")]
    [InlineData("services.exe")]
    [InlineData("wolfstare.service.exe")]
    public void CriticalProcessesAreProtected(string imageName)
        => Assert.True(CriticalProcesses.IsProtected(imageName));

    [Theory]
    [InlineData("steam.exe")]
    [InlineData("4kvideodownloaderplus.exe")]
    [InlineData("discord.exe")]
    public void OrdinaryApplicationsAreNotProtected(string imageName)
        => Assert.False(CriticalProcesses.IsProtected(imageName));

    [Fact]
    public void RuleTargetingCriticalProcessIsRejectedWithAnActionableMessage()
    {
        var result = RuleValidator.Validate(new AppRule(new ImageNameMatcher("explorer.exe")));

        Assert.False(result.IsValid);
        Assert.Contains("explorer.exe", result.Error);
        Assert.Contains("critical", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryAppRuleIsAccepted()
        => Assert.True(RuleValidator.Validate(new AppRule(new ImageNameMatcher("steam.exe"))).IsValid);

    [Fact]
    public void PathRuleIsRejectedInV1()
    {
        var result = RuleValidator.Validate(new PathRule("reddit.com/r/all"));

        Assert.False(result.IsValid);
        Assert.Contains("HTTPS inspection", result.Error);
    }

    [Theory]
    [InlineData("reddit.com")]
    [InlineData("*.reddit.com")]
    [InlineData("*")]
    public void ValidDomainRulesAreAccepted(string pattern)
        => Assert.True(RuleValidator.Validate(new DomainRule(pattern)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a domain")]
    [InlineData("http://reddit.com")]
    public void MalformedDomainRulesAreRejected(string pattern)
        => Assert.False(RuleValidator.Validate(new DomainRule(pattern)).IsValid);

    [Fact]
    public void PublisherRuleThatWouldCatchMicrosoftIsRejected()
    {
        // A publisher rule for "Microsoft" would terminate most of the operating system.
        var result = RuleValidator.Validate(new AppRule(new PublisherMatcher("Microsoft Windows")));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void EmptyMatcherValuesAreRejected()
    {
        Assert.False(RuleValidator.Validate(new AppRule(new ImageNameMatcher(""))).IsValid);
        Assert.False(RuleValidator.Validate(new AppRule(new PublisherMatcher("  "))).IsValid);
        Assert.False(RuleValidator.Validate(new AppRule(new FileDescriptionMatcher(""))).IsValid);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter RuleValidatorTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `CriticalProcesses`**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>
/// Executables that may never be blocked, terminated, or IFEO-redirected.
///
/// IFEO on explorer.exe leaves no desktop shell; on lsass.exe or csrss.exe it prevents boot.
/// Because the uninstaller refuses to run during an active lock, recovery from such a rule
/// would require safe mode. The guard is enforced here, in the pure domain, so that it is
/// unit-tested and rejects the rule at creation rather than at apply time (spec §8.3).
/// </summary>
public static class CriticalProcesses
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Boot and session-critical
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe",
        // Shell — blocking these leaves no usable desktop
        "explorer.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "searchhost.exe", "runtimebroker.exe", "dllhost.exe", "taskhostw.exe",
        // Administration — the user must retain the ability to fix their machine
        "taskmgr.exe", "regedit.exe", "mmc.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "conhost.exe", "windowsterminal.exe", "systemsettings.exe",
        "control.exe", "msconfig.exe", "sc.exe", "net.exe",
        // Wolfstare's own executables
        "wolfstare.service.exe", "wolfstare.blockstub.exe", "wolfstare.cli.exe",
    };

    /// <summary>
    /// True when the name refers to a protected executable. Accepts a bare name, a name
    /// without its extension, or a full path — the rule author should not be able to slip
    /// one past the guard by writing it differently.
    /// </summary>
    public static bool IsProtected(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return false;

        var name = imageName.Trim().Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return Names.Contains(name);
    }
}
```

- [ ] **Step 4: Implement `RuleValidator`**

```csharp
namespace Wolfstare.Core.Rules;

/// <summary>The outcome of validating a rule before it is persisted.</summary>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok { get; } = new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

/// <summary>
/// Validates rules at creation time. Every rejection carries a message the UI can show
/// verbatim — a rule silently dropped later is far worse than one refused up front.
/// </summary>
public static class RuleValidator
{
    /// <summary>
    /// Publisher substrings broad enough to match large parts of the operating system.
    /// A rule on "Microsoft" would have the ETW watcher terminating system processes.
    /// </summary>
    private static readonly string[] ForbiddenPublisherSubstrings =
    [
        "microsoft", "windows", "nvidia", "intel", "advanced micro devices", "amd ",
    ];

    public static ValidationResult Validate(BlockRule rule) => rule switch
    {
        DomainRule d => ValidateDomain(d),
        AppRule a => ValidateApp(a),
        PathRule => ValidationResult.Fail(
            "Path rules require HTTPS inspection, which is not enabled. Block the whole domain instead."),
        _ => ValidationResult.Fail($"Unknown rule type '{rule.GetType().Name}'."),
    };

    private static ValidationResult ValidateDomain(DomainRule rule)
    {
        var pattern = rule.Pattern?.Trim() ?? string.Empty;

        if (pattern.Length == 0)
            return ValidationResult.Fail("Domain pattern cannot be empty.");

        if (pattern == "*")
            return ValidationResult.Ok;

        if (pattern.Contains("://", StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' looks like a URL. Enter just the domain, for example 'reddit.com'.");

        if (pattern.Contains('/', StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' contains a path. Path rules require HTTPS inspection, which is not enabled.");

        var body = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;

        if (body.Length == 0)
            return ValidationResult.Fail("Wildcard pattern is missing a domain, for example '*.reddit.com'.");

        if (body.Contains('*', StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' is not supported. Use '*', 'example.com', or '*.example.com'.");

        if (!body.Contains('.', StringComparison.Ordinal))
            return ValidationResult.Fail($"'{pattern}' is not a domain. Did you mean '{body}.com'?");

        foreach (var label in body.Split('.'))
        {
            if (label.Length == 0)
                return ValidationResult.Fail($"'{pattern}' has an empty label.");

            foreach (var c in label)
            {
                if (char.IsLetterOrDigit(c) || c == '-') continue;
                return ValidationResult.Fail($"'{pattern}' contains an invalid character '{c}'.");
            }
        }

        return ValidationResult.Ok;
    }

    private static ValidationResult ValidateApp(AppRule rule) => rule.Matcher switch
    {
        ImageNameMatcher m when string.IsNullOrWhiteSpace(m.ImageName)
            => ValidationResult.Fail("Application name cannot be empty."),

        ImageNameMatcher m when CriticalProcesses.IsProtected(m.ImageName)
            => ValidationResult.Fail(
                $"'{m.ImageName}' is a critical Windows process and cannot be blocked. "
                + "Blocking it would leave the machine unusable."),

        PublisherMatcher m when string.IsNullOrWhiteSpace(m.Publisher)
            => ValidationResult.Fail("Publisher cannot be empty."),

        PublisherMatcher m when IsForbiddenPublisher(m.Publisher)
            => ValidationResult.Fail(
                $"Publisher '{m.Publisher}' matches critical system software and cannot be blocked. "
                + "Target the specific application instead."),

        FileDescriptionMatcher m when string.IsNullOrWhiteSpace(m.Description)
            => ValidationResult.Fail("Description cannot be empty."),

        _ => ValidationResult.Ok,
    };

    private static bool IsForbiddenPublisher(string publisher)
    {
        foreach (var forbidden in ForbiddenPublisherSubstrings)
            if (publisher.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter RuleValidatorTests`
Expected: PASS, all cases.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add critical-process guard and rule validation"
```

---

### Task 6: Password hashing

Spec §4.3. PBKDF2-HMAC-SHA256, 600,000 iterations, constant-time verification.

**Files:**
- Create: `src/Wolfstare.Core/Sessions/PasswordHash.cs`
- Create: `src/Wolfstare.Core/Sessions/IPasswordHasher.cs`
- Create: `src/Wolfstare.Core/Sessions/Pbkdf2PasswordHasher.cs`
- Test: `tests/Wolfstare.Core.Tests/Sessions/Pbkdf2PasswordHasherTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `sealed record PasswordHash(byte[] Hash, byte[] Salt, int Iterations)`
  - `interface IPasswordHasher { PasswordHash Create(string password); bool Verify(PasswordHash stored, string password); }`
  - `sealed class Pbkdf2PasswordHasher : IPasswordHasher`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Sessions;

namespace Wolfstare.Core.Tests.Sessions;

public class Pbkdf2PasswordHasherTests
{
    // 600,000 iterations is deliberately slow, so this suite uses few hashes.
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void CorrectPasswordVerifies()
    {
        var stored = _hasher.Create("correct horse battery staple");
        Assert.True(_hasher.Verify(stored, "correct horse battery staple"));
    }

    [Fact]
    public void IncorrectPasswordDoesNotVerify()
    {
        var stored = _hasher.Create("correct horse battery staple");
        Assert.False(_hasher.Verify(stored, "Correct horse battery staple"));
        Assert.False(_hasher.Verify(stored, ""));
    }

    [Fact]
    public void SaltIsRandomPerHash()
    {
        var a = _hasher.Create("same password");
        var b = _hasher.Create("same password");

        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.True(_hasher.Verify(a, "same password"));
        Assert.True(_hasher.Verify(b, "same password"));
    }

    [Fact]
    public void HashUsesSpecifiedParameters()
    {
        var stored = _hasher.Create("x");

        Assert.Equal(600_000, stored.Iterations);
        Assert.Equal(16, stored.Salt.Length);   // 128-bit salt
        Assert.Equal(32, stored.Hash.Length);   // 256-bit key
    }

    [Fact]
    public void EmptyPasswordIsRejectedAtCreation()
        => Assert.Throws<ArgumentException>(() => _hasher.Create("  "));

    [Fact]
    public void CorruptStoredHashFailsClosed()
    {
        var stored = _hasher.Create("password");
        var corrupt = stored with { Hash = [1, 2, 3] };

        Assert.False(_hasher.Verify(corrupt, "password"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter Pbkdf2PasswordHasherTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `PasswordHash` and `IPasswordHasher`**

```csharp
namespace Wolfstare.Core.Sessions;

/// <summary>A stored password verifier. Never contains the password itself.</summary>
public sealed record PasswordHash(byte[] Hash, byte[] Salt, int Iterations);

public interface IPasswordHasher
{
    /// <summary>Derives a new hash with a fresh random salt.</summary>
    /// <exception cref="ArgumentException">The password is empty or whitespace.</exception>
    PasswordHash Create(string password);

    /// <summary>Constant-time verification. Returns false rather than throwing on corrupt input.</summary>
    bool Verify(PasswordHash stored, string password);
}
```

- [ ] **Step 4: Implement `Pbkdf2PasswordHasher`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (spec §4.3).
///
/// The iteration count is deliberately high because the threat model assumes an adversary
/// with full local access who can read the database — the only real defence for a weak
/// password is making each guess expensive.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    public PasswordHash Create(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations, HashBytes);
        return new PasswordHash(hash, salt, Iterations);
    }

    public bool Verify(PasswordHash stored, string password)
    {
        // Fail closed on anything malformed rather than throwing: a corrupt record must
        // read as "wrong password", never as an error the caller might treat as success.
        if (stored.Hash.Length == 0 || stored.Salt.Length == 0 || stored.Iterations <= 0)
            return false;
        if (string.IsNullOrEmpty(password))
            return false;

        var candidate = Derive(password, stored.Salt, stored.Iterations, stored.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter Pbkdf2PasswordHasherTests`
Expected: PASS, all cases.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add PBKDF2 password hashing with constant-time verification"
```

---

### Task 7: Clock abstraction and tamper-resistant elapsed time

Spec §5. **The most important task in this plan.** A timed lock defeated by `Set-Date` is worthless.

**Files:**
- Create: `src/Wolfstare.Core/Time/IClock.cs`
- Create: `src/Wolfstare.Core/Time/SystemClock.cs`
- Create: `src/Wolfstare.Core/Time/SessionTiming.cs`
- Create: `src/Wolfstare.Core/Time/ElapsedCalculator.cs`
- Test: `tests/Wolfstare.Core.Tests/Time/ElapsedCalculatorTests.cs`
- Test: `tests/Wolfstare.Core.Tests/Time/FakeClock.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `interface IClock { DateTimeOffset UtcNow { get; } long MonotonicMs { get; } }`
  - `sealed class SystemClock : IClock`
  - `sealed record SessionTiming(DateTimeOffset StartedAtUtc, long ElapsedSeconds, DateTimeOffset CheckpointWallUtc, long CheckpointMonotonicMs)` with `static SessionTiming Start(IClock clock)`
  - `static SessionTiming ElapsedCalculator.Advance(SessionTiming timing, IClock clock)`
  - `static SessionTiming ElapsedCalculator.ResumeAfterRestart(SessionTiming timing, IClock clock)`

- [ ] **Step 1: Write `FakeClock`**

```csharp
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Time;

/// <summary>
/// A clock whose wall time and monotonic counter move independently, so tests can
/// simulate a user changing the system clock while the monotonic counter keeps ticking.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public long MonotonicMs { get; private set; }

    /// <summary>Honest passage of time: both clocks advance together.</summary>
    public void Advance(TimeSpan amount)
    {
        UtcNow += amount;
        MonotonicMs += (long)amount.TotalMilliseconds;
    }

    /// <summary>The user changes the system clock. Monotonic is unaffected — as in reality.</summary>
    public void SetWallClock(DateTimeOffset value) => UtcNow = value;

    /// <summary>A reboot: monotonic resets to zero, wall clock keeps its value.</summary>
    public void Reboot(TimeSpan downtime)
    {
        UtcNow += downtime;
        MonotonicMs = 0;
    }
}
```

- [ ] **Step 2: Write the failing tests — the full tamper matrix**

```csharp
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Time;

public class ElapsedCalculatorTests
{
    private static (FakeClock Clock, SessionTiming Timing) StartSession()
    {
        var clock = new FakeClock();
        return (clock, SessionTiming.Start(clock));
    }

    [Fact]
    public void HonestPassageOfTimeAccrues()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);
    }

    [Fact]
    public void RepeatedAdvancesAccumulateWithoutDoubleCounting()
    {
        var (clock, timing) = StartSession();

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            timing = ElapsedCalculator.Advance(timing, clock);
        }

        Assert.Equal(300, timing.ElapsedSeconds);
    }

    [Fact]
    public void MovingClockForwardGrantsNoCredit()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(1));         // one honest minute
        clock.SetWallClock(clock.UtcNow.AddHours(10));  // user jumps the clock forward
        timing = ElapsedCalculator.Advance(timing, clock);

        // Monotonic only saw 60 seconds, so that is all that counts.
        Assert.Equal(60, timing.ElapsedSeconds);
    }

    [Fact]
    public void MovingClockBackwardDoesNotLoseCredit()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);
        Assert.Equal(600, timing.ElapsedSeconds);

        clock.SetWallClock(clock.UtcNow.AddHours(-5));  // user rolls the clock back
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);       // no time lost
    }

    [Fact]
    public void TimeContinuesAccruingNormallyAfterABackwardJump()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.SetWallClock(clock.UtcNow.AddHours(-5));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Advance(TimeSpan.FromMinutes(5));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(900, timing.ElapsedSeconds);       // 10 + 5 minutes
    }

    [Fact]
    public void RepeatedForwardJumpsGrantNoCredit()
    {
        var (clock, timing) = StartSession();

        for (var i = 0; i < 20; i++)
        {
            clock.SetWallClock(clock.UtcNow.AddHours(1));
            timing = ElapsedCalculator.Advance(timing, clock);
        }

        Assert.Equal(0, timing.ElapsedSeconds);         // monotonic never moved
    }

    [Fact]
    public void RebootCreditsWallClockDowntime()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromHours(8));
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);

        // A genuine 8-hour shutdown should count: 600s + 28800s.
        Assert.Equal(29_400, timing.ElapsedSeconds);
    }

    [Fact]
    public void RebootWithClockRolledBackCreditsNothingButLosesNothing()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromHours(-3));           // clock set back across the reboot
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);
    }

    [Fact]
    public void AccrualIsMonotonicUnderAdversarialClockSequence()
    {
        // Property: elapsed never decreases, whatever the user does to the clock.
        var (clock, timing) = StartSession();
        var previous = 0L;

        var jumps = new[] { 3600, -7200, 60, -60, 100_000, -100_000, 0 };
        foreach (var seconds in jumps)
        {
            clock.Advance(TimeSpan.FromSeconds(30));    // honest time between tamper attempts
            clock.SetWallClock(clock.UtcNow.AddSeconds(seconds));
            timing = ElapsedCalculator.Advance(timing, clock);

            Assert.True(timing.ElapsedSeconds >= previous,
                $"elapsed went backwards: {previous} -> {timing.ElapsedSeconds}");
            previous = timing.ElapsedSeconds;
        }

        // Seven honest 30s intervals is the ceiling regardless of the jumps.
        Assert.Equal(210, timing.ElapsedSeconds);
    }

    [Fact]
    public void CheckpointAdvancesSoNextCallMeasuresFromTheNewPoint()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(1));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(clock.UtcNow, timing.CheckpointWallUtc);
        Assert.Equal(clock.MonotonicMs, timing.CheckpointMonotonicMs);
    }

    [Fact]
    public void StartedAtIsPreservedAcrossAdvances()
    {
        var (clock, timing) = StartSession();
        var startedAt = timing.StartedAtUtc;

        clock.Advance(TimeSpan.FromHours(1));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(startedAt, timing.StartedAtUtc);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter ElapsedCalculatorTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 4: Implement `IClock` and `SystemClock`**

```csharp
namespace Wolfstare.Core.Time;

/// <summary>
/// Time as the domain sees it. Two independent readings, because a timed lock must survive
/// the user changing the system clock: <see cref="UtcNow"/> is user-settable, whereas
/// <see cref="MonotonicMs"/> counts forward since boot and cannot be set.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Milliseconds since system start. Resets to zero on reboot; never decreases otherwise.</summary>
    long MonotonicMs { get; }
}

/// <inheritdoc />
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicMs => Environment.TickCount64;
}
```

- [ ] **Step 5: Implement `SessionTiming`**

```csharp
namespace Wolfstare.Core.Time;

/// <summary>
/// The persisted timing state of an active session. Checkpointed to the database every
/// 30 seconds so that a crash or reboot loses at most that much accuracy.
/// </summary>
/// <param name="StartedAtUtc">When the session began. Informational; never used for expiry.</param>
/// <param name="ElapsedSeconds">Accrued time, the only value expiry is computed from.</param>
/// <param name="CheckpointWallUtc">Wall-clock reading at the last accrual.</param>
/// <param name="CheckpointMonotonicMs">Monotonic reading at the last accrual.</param>
public sealed record SessionTiming(
    DateTimeOffset StartedAtUtc,
    long ElapsedSeconds,
    DateTimeOffset CheckpointWallUtc,
    long CheckpointMonotonicMs)
{
    public static SessionTiming Start(IClock clock)
        => new(clock.UtcNow, 0, clock.UtcNow, clock.MonotonicMs);
}
```

- [ ] **Step 6: Implement `ElapsedCalculator`**

```csharp
namespace Wolfstare.Core.Time;

/// <summary>
/// Accrues elapsed time for an active session in a way that resists clock tampering
/// (spec §5).
///
/// The rule is that a tick may credit no more than the monotonic counter actually observed,
/// and never a negative amount. Moving the system clock forward inflates the wall delta but
/// not the monotonic one, so the minimum discards it. Moving it backward makes the wall
/// delta negative, so the clamp discards that instead. Neither direction helps the user.
/// </summary>
public static class ElapsedCalculator
{
    /// <summary>
    /// Accrues time since the last checkpoint and returns updated timing.
    /// Safe to call at any frequency; accrual does not depend on the interval.
    /// </summary>
    public static SessionTiming Advance(SessionTiming timing, IClock clock)
    {
        var nowWall = clock.UtcNow;
        var nowMonotonic = clock.MonotonicMs;

        var wallDelta = (long)(nowWall - timing.CheckpointWallUtc).TotalSeconds;
        var monotonicDelta = (nowMonotonic - timing.CheckpointMonotonicMs) / 1000;

        // A negative monotonic delta means the counter wrapped or the process was restarted
        // without going through ResumeAfterRestart. Credit nothing rather than guessing.
        if (monotonicDelta < 0) monotonicDelta = 0;

        var advance = Math.Min(wallDelta, monotonicDelta);
        if (advance < 0) advance = 0;

        return timing with
        {
            ElapsedSeconds = timing.ElapsedSeconds + advance,
            CheckpointWallUtc = nowWall,
            CheckpointMonotonicMs = nowMonotonic,
        };
    }

    /// <summary>
    /// Resumes a session after a service restart or reboot.
    ///
    /// The monotonic counter resets at boot, so it cannot bound the gap here and wall time is
    /// the only evidence available. Crediting it is deliberate: a genuine overnight shutdown
    /// ought to count against the block. The cost is the accepted residual hole in spec §5.3
    /// — reboot, set the clock forward, boot — which closing would require a network time
    /// source inside an application whose job is severing network access.
    /// </summary>
    public static SessionTiming ResumeAfterRestart(SessionTiming timing, IClock clock)
    {
        var nowWall = clock.UtcNow;
        var gap = (long)(nowWall - timing.CheckpointWallUtc).TotalSeconds;
        if (gap < 0) gap = 0;

        return timing with
        {
            ElapsedSeconds = timing.ElapsedSeconds + gap,
            CheckpointWallUtc = nowWall,
            CheckpointMonotonicMs = clock.MonotonicMs,
        };
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter ElapsedCalculatorTests`
Expected: PASS, all cases.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add tamper-resistant elapsed time accrual"
```

---

### Task 8: Sessions, locks, and stop policy

Spec §4.3 and §10.1. This is where "a timed lock has no early exit" becomes a property of the code rather than a promise in a document.

**Files:**
- Create: `src/Wolfstare.Core/Sessions/Lock.cs`
- Create: `src/Wolfstare.Core/Sessions/BlockSession.cs`
- Create: `src/Wolfstare.Core/Sessions/StopPolicy.cs`
- Test: `tests/Wolfstare.Core.Tests/Sessions/StopPolicyTests.cs`

**Interfaces:**
- Consumes: `PasswordHash`, `IPasswordHasher` (Task 6); `SessionTiming`, `IClock` (Task 7)
- Produces:
  - `abstract record Lock`; `NoLock`, `PasswordLock(PasswordHash Hash)`, `TimedLock`
  - `sealed record BlockSession(Guid Id, Guid BlockListId, Lock Lock, SessionTiming Timing, long? DurationSeconds)` with `bool IsExpired(IClock)`, `long? RemainingSeconds(IClock)`
  - `enum StopOutcome { Allowed, PasswordRequired, PasswordIncorrect, Locked }`
  - `static StopOutcome StopPolicy.CanStop(BlockSession session, string? password, IPasswordHasher hasher, IClock clock)`

- [ ] **Step 1: Write the failing tests**

```csharp
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Time;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Sessions;

public class StopPolicyTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    private static BlockSession Session(Lock lockSpec, IClock clock, long? durationSeconds)
        => new(Guid.NewGuid(), Guid.NewGuid(), lockSpec, SessionTiming.Start(clock), durationSeconds);

    [Fact]
    public void UnlockedSessionCanBeStoppedFreely()
    {
        var clock = new FakeClock();
        var session = Session(new NoLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockCannotBeStoppedEarly()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockDoesNotYieldToAPassword()
    {
        // The point of a timed lock: no credential ends it. Not even a correct one.
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, "any password", _hasher, clock));
    }

    [Fact]
    public void TimedLockCanBeStoppedOnceExpired()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 60);

        clock.Advance(TimeSpan.FromSeconds(61));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockSurvivesAForwardClockJump()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        clock.SetWallClock(clock.UtcNow.AddDays(1));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void PasswordLockRequiresAPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.PasswordRequired, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void PasswordLockRejectsTheWrongPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, "hunter3", _hasher, clock));
    }

    [Fact]
    public void PasswordLockAcceptsTheCorrectPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, "hunter2", _hasher, clock));
    }

    [Fact]
    public void IndefiniteSessionNeverExpires()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: null);

        clock.Advance(TimeSpan.FromDays(365));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.False(session.IsExpired(clock));
        Assert.Null(session.RemainingSeconds(clock));
        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void RemainingSecondsCountsDownAndFloorsAtZero()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 100);

        Assert.Equal(100, session.RemainingSeconds(clock));

        clock.Advance(TimeSpan.FromSeconds(40));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };
        Assert.Equal(60, session.RemainingSeconds(clock));

        clock.Advance(TimeSpan.FromSeconds(500));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };
        Assert.Equal(0, session.RemainingSeconds(clock));
    }

    [Fact]
    public void CorruptPasswordLockFailsClosed()
    {
        // An unreadable verifier must leave the session locked, never open it.
        var clock = new FakeClock();
        var corrupt = new PasswordLock(new PasswordHash([], [], 0));
        var session = Session(corrupt, clock, null);

        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, "anything", _hasher, clock));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter StopPolicyTests`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `Lock`**

```csharp
namespace Wolfstare.Core.Sessions;

/// <summary>How a session is protected from being stopped. Closed hierarchy — see spec §4.3.</summary>
public abstract record Lock;

/// <summary>No protection. The session can be stopped at any time.</summary>
public sealed record NoLock : Lock;

/// <summary>Stopping requires the password.</summary>
public sealed record PasswordLock(PasswordHash Hash) : Lock;

/// <summary>
/// Stopping is impossible until the session's duration elapses.
///
/// There is deliberately no field here — no password, no override, no escape hatch. The
/// absence is the feature: <see cref="StopPolicy"/> has no branch that ends a timed session
/// before expiry, so no caller can request one.
/// </summary>
public sealed record TimedLock : Lock;
```

- [ ] **Step 4: Implement `BlockSession`**

```csharp
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// An active blocking session: one block list, one lock, one span of time.
/// </summary>
/// <param name="DurationSeconds">Null means indefinite — the session runs until stopped.</param>
public sealed record BlockSession(
    Guid Id,
    Guid BlockListId,
    Lock Lock,
    SessionTiming Timing,
    long? DurationSeconds)
{
    /// <summary>True once the session has served its full duration. Indefinite sessions never expire.</summary>
    public bool IsExpired(IClock clock)
        => DurationSeconds is { } duration && Timing.ElapsedSeconds >= duration;

    /// <summary>Seconds left, floored at zero. Null for an indefinite session.</summary>
    public long? RemainingSeconds(IClock clock)
        => DurationSeconds is { } duration
            ? Math.Max(0, duration - Timing.ElapsedSeconds)
            : null;
}
```

- [ ] **Step 5: Implement `StopPolicy`**

```csharp
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>The result of asking whether a session may be stopped.</summary>
public enum StopOutcome
{
    /// <summary>The session may be stopped now.</summary>
    Allowed,

    /// <summary>A password is required and none was supplied.</summary>
    PasswordRequired,

    /// <summary>A password was supplied and it was wrong.</summary>
    PasswordIncorrect,

    /// <summary>The session is locked and no action by the caller can change that.</summary>
    Locked,
}

/// <summary>
/// The single authority on whether a session may end early (spec §2.1: authority is policy,
/// not transport). Every caller — HTTP API, CLI, internal scheduler — routes through here,
/// so it does not matter who is asking.
/// </summary>
public static class StopPolicy
{
    public static StopOutcome CanStop(
        BlockSession session, string? password, IPasswordHasher hasher, IClock clock)
    {
        // An expired session is over regardless of how it was locked.
        if (session.IsExpired(clock)) return StopOutcome.Allowed;

        return session.Lock switch
        {
            NoLock => StopOutcome.Allowed,

            // Note the absence of a password branch. A timed lock has no early exit; adding
            // one here would silently remove the only guarantee this application makes.
            TimedLock => StopOutcome.Locked,

            PasswordLock l when password is null => StopOutcome.PasswordRequired,
            PasswordLock l => hasher.Verify(l.Hash, password)
                ? StopOutcome.Allowed
                : StopOutcome.PasswordIncorrect,

            // Fail closed on an unrecognised lock type rather than defaulting to open.
            _ => StopOutcome.Locked,
        };
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter StopPolicyTests`
Expected: PASS, all cases.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS, every test in the solution.

- [ ] **Step 8: Verify the Core project has no Windows dependencies**

Run: `dotnet list src/Wolfstare.Core/Wolfstare.Core.csproj package`
Expected: no packages. Confirm the TFM in the csproj is `net9.0`, not `net9.0-windows`.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Add session locks and stop authorisation policy"
```

---

## Phase 1 Definition of Done

- `dotnet test` passes with every test green.
- `Wolfstare.Core` targets `net9.0` and has zero package references.
- The clock-tamper matrix in Task 7 covers: forward jump, backward jump, repeated forward jumps, reboot with downtime, reboot with a backward jump, and an adversarial mixed sequence asserting monotonic accrual.
- `StopPolicy` contains no code path that ends a `TimedLock` before expiry, and a test asserts a correct password does not end one.
- `CriticalProcesses` rejects `explorer.exe` and every other name in the guard list, including when written without an extension or as a full path.

## Next Phase

Phase 2 covers SQLite persistence, the ASP.NET Core Minimal API implementing spec §10, and the CLI — turning this library into a runnable service that returns `423 Locked` to every caller.
