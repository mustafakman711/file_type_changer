using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nashi.Core;
namespace Nashi.Windows;

/// Per-user (HKCU, no administrator) integration: the sign-in Run entry and the
/// classic Explorer verbs that appear under "Show more options". The Windows 11
/// top-level entry comes from the signed sparse package (NashiShell.exe) and
/// does not depend on these keys, so either keeps working without the other.
internal static class ExplorerIntegration
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run",Classes=@"Software\Classes",ConvertVerb="Nashi.Convert",ToolsVerb="Nashi.Tools",StateKey=@"Software\Nashi";
    [DllImport("shell32.dll")]private static extern void SHChangeNotify(int eventId,uint flags,IntPtr item1,IntPtr item2);
    /// Explorer/benchmark isolation: never touch the user's registry.
    internal static bool Isolated=>Environment.GetEnvironmentVariable("NASHI_ISOLATED")=="1";
    internal static string Executable=>Environment.ProcessPath??Path.Combine(AppContext.BaseDirectory,"Nashi.exe");
    internal static string IconPath=>Path.Combine(Path.GetDirectoryName(Executable)!,"NashiMenu.ico");

    internal static bool LaunchesAtLogin(){using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("Nashi") is string;}
    internal static void SyncLaunchAtLogin(bool enabled)
    {
        if(Isolated)return;
        using var run=Registry.CurrentUser.CreateSubKey(RunKey);var command="\""+Executable+"\" --background";
        if(!enabled){run.DeleteValue("Nashi",false);return;}
        if(run.GetValue("Nashi") as string!=command)run.SetValue("Nashi",command);
    }

    /// Input extensions (with the aliases FeatureCatalog.Extension accepts)
    /// and the formats each can become.
    internal static IEnumerable<(string Extension,string[] Formats)> ConvertibleExtensions()
    {
        var aliases=new Dictionary<string,string[]>{["jpg"]=["jpeg"],["tiff"]=["tif"],["heic"]=["heif"],["aiff"]=["aif"],["md"]=["markdown"],["gz"]=["gzip","tgz"]};
        foreach(var (input,formats) in FeatureCatalog.Conversions.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            if(formats.Length==0||input is "pages" or "rtfd")continue;
            yield return(input,formats);foreach(var alias in aliases.GetValueOrDefault(input,[]))yield return(alias,formats);
        }
    }
    internal static string ConvertCommand(string format)=>"\""+Executable+"\" --convert "+format+" \"%1\"";
    internal static string ToolsCommand=>"\""+Executable+"\" --wheel \"%1\"";

    /// Writes or removes the classic verbs; rewrites them only when the
    /// install folder, language or catalog changed.
    internal static void SyncClassicMenu(bool enabled,string language)
    {
        if(Isolated)return;
        var catalog=string.Join("|",Executable,AppLocalization.Normalize(language),string.Join(",",ConvertibleExtensions().Select(e=>e.Extension+":"+string.Join(".",e.Formats))));
        var signature=enabled?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalog)))[..16]:"";
        using(var state=Registry.CurrentUser.CreateSubKey(StateKey)){if(state.GetValue("ExplorerMenu") as string==signature)return;}
        RemoveClassicMenu();
        if(enabled)
        {
            var convert=AppLocalization.Text("Convert with Nashi",language);var tools=AppLocalization.Text("Nashi tools…",language);
            foreach(var (extension,formats) in ConvertibleExtensions())
            {
                using var verb=Registry.CurrentUser.CreateSubKey($@"{Classes}\SystemFileAssociations\.{extension}\shell\{ConvertVerb}");
                verb.SetValue("MUIVerb",convert);verb.SetValue("Icon",IconPath);verb.SetValue("SubCommands","");
                foreach(var format in formats)
                {
                    using var item=verb.CreateSubKey(@"shell\"+format);item.SetValue("MUIVerb",format.ToUpperInvariant());item.SetValue("MultiSelectModel","Player");
                    using var command=item.CreateSubKey("command");command.SetValue("",ConvertCommand(format));
                }
            }
            foreach(var type in new[]{"*","Directory"})
            {
                using var verb=Registry.CurrentUser.CreateSubKey($@"{Classes}\{type}\shell\{ToolsVerb}");
                verb.SetValue("MUIVerb",tools);verb.SetValue("Icon",IconPath);verb.SetValue("MultiSelectModel","Player");
                using var command=verb.CreateSubKey("command");command.SetValue("",ToolsCommand);
            }
        }
        using(var state=Registry.CurrentUser.CreateSubKey(StateKey))state.SetValue("ExplorerMenu",signature);
        SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }
    internal static void RemoveClassicMenu()
    {
        using(var associations=Registry.CurrentUser.OpenSubKey(Classes+@"\SystemFileAssociations"))
            foreach(var extension in associations?.GetSubKeyNames()??[])
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\SystemFileAssociations\{extension}\shell\{ConvertVerb}",false);
        foreach(var type in new[]{"*","Directory"})Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{type}\shell\{ToolsVerb}",false);
        using var state=Registry.CurrentUser.OpenSubKey(StateKey,true);state?.DeleteValue("ExplorerMenu",false);
    }
}
