using System.Security.Cryptography;

namespace Wolfstare.Service;

/// <summary>
/// Where Wolfstare keeps its state. Defaults to <c>%ProgramData%\Wolfstare</c>; overridable
/// through configuration so tests can run against a temporary directory.
/// </summary>
public sealed record WolfstarePaths(string RootDirectory, int Port)
{
    public string DatabasePath => Path.Combine(RootDirectory, "wolfstare.db");

    public string TokenPath => Path.Combine(RootDirectory, "api.token");

    public static WolfstarePaths Resolve(IConfiguration configuration)
    {
        var root = configuration["Wolfstare:DataDirectory"]
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                       "Wolfstare");

        Directory.CreateDirectory(root);

        var port = int.TryParse(configuration["Wolfstare:Port"], out var configured) ? configured : 8437;
        return new WolfstarePaths(root, port);
    }
}

/// <summary>
/// The bearer token the local UI uses. Regenerated on every service start, so a token that
/// leaks is useless after the next restart.
///
/// It is readable by the interactive user by design — per spec §2.1 this protects against a
/// malicious web page, not against the user, who is already permitted to drive the API.
/// </summary>
public static class ApiToken
{
    public static string CreateAndPersist(WolfstarePaths paths)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(paths.TokenPath, token);
        return token;
    }
}
