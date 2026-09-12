using System.Runtime.InteropServices;

// Wolfstare block stub.
//
// Windows launches this in place of an application whose IFEO "Debugger" value points here
// (spec §8.1). IFEO hands us the original command line, so args[0] is the blocked executable's
// path. We tell the user what was blocked and exit non-zero so nothing treats the launch as a
// success.
//
// This program references nothing outside the framework, on purpose: it must start even when
// the rest of the machine is misconfigured. The message wording mirrors
// Wolfstare.Enforcement.Apps.BlockStubMessage, which is the tested reference copy — keep them
// in step by hand rather than adding a dependency here.

const uint MB_OK = 0x00000000;
const uint MB_ICONWARNING = 0x00000030;
const uint MB_TOPMOST = 0x00040000;

var name = args.Length > 0 ? ExeName(args[0]) : "This application";
var message = $"{name} is blocked by an active Wolfstare session.";

MessageBoxW(IntPtr.Zero, message, "Blocked by Wolfstare", MB_OK | MB_ICONWARNING | MB_TOPMOST);
return 1;

static string ExeName(string path)
{
    var trimmed = path.Trim().Replace('/', '\\');
    var slash = trimmed.LastIndexOf('\\');
    var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    return name.Length == 0 ? "This application" : name;
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
