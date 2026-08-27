namespace Wolfstare.Core.Rules;

/// <summary>
/// The identity of a running or launching process, as observed by the enforcement layer.
/// </summary>
/// <param name="ImageName">
/// Executable name. May be a full path; matching uses the filename only, because a rule must
/// be writable for an application that is not installed yet and therefore has no path.
/// </param>
/// <param name="Publisher">Authenticode subject, or null when the executable is unsigned.</param>
/// <param name="FileDescription">
/// Win32 version-info description, or null when the executable carries no version resource.
/// </param>
public sealed record ProcessIdentity(string ImageName, string? Publisher, string? FileDescription);
