namespace Wolfstare.Enforcement.Apps;

/// <summary>
/// Builds the text the block stub shows when it is launched in an application's place.
///
/// This is the tested reference implementation. The stub itself (<c>Wolfstare.BlockStub</c>)
/// keeps its own two-line copy rather than referencing this assembly, so it stays tiny and
/// dependency-free — a stub that failed to launch because a dependency was missing would let a
/// blocked app through. If the wording changes, change it in both places.
/// </summary>
public static class BlockStubMessage
{
    public static string ForCommandLine(string[] args)
    {
        var name = args.Length > 0 ? ExeName(args[0]) : "This application";
        return $"{name} is blocked by an active Wolfstare session.";
    }

    private static string ExeName(string path)
    {
        var trimmed = path.Trim().Replace('/', '\\');
        var slash = trimmed.LastIndexOf('\\');
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? "This application" : name;
    }
}
