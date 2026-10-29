using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Nashi.Windows;

/// Nashi must run at the same integrity level as Explorer. Started as
/// administrator (from an elevated terminal, an installer, a script), Windows'
/// UIPI blocks every OLE drop from Explorer into Nashi's windows: the gesture
/// hook still fires, so the wheel opens, but files never arrive and it stays
/// at "Keep dragging". An elevated start therefore hands itself back to
/// Explorer, which launches Nashi again at the user's normal level.
internal static class Elevation
{
    private const int TokenElevationClass=20;private const uint TOKEN_QUERY=0x0008,PROCESS_QUERY_LIMITED_INFORMATION=0x1000;
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(IntPtr token,int infoClass,out int value,int length,out int returned);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")]private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);

    private static bool? self;
    /// This process runs elevated (cached: it cannot change).
    internal static bool IsElevated=>self??=Elevated(GetCurrentProcess());

    /// The desktop shell is running and is not elevated, so a drop from it into an elevated Nashi would be blocked.
    internal static bool ShellIsUnelevated()
    {
        var shell=GetShellWindow();if(shell==IntPtr.Zero)return false;GetWindowThreadProcessId(shell,out var id);if(id==0)return false;
        var process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,false,id);if(process==IntPtr.Zero)return false;
        try{return ElevatedOrNull(process)==false;}finally{CloseHandle(process);}
    }

    private static bool? ElevatedOrNull(IntPtr process)
    {
        if(!OpenProcessToken(process,TOKEN_QUERY,out var token))return null;
        try{return GetTokenInformation(token,TokenElevationClass,out var value,4,out _)?value!=0:null;}finally{CloseHandle(token);}
    }
    private static bool Elevated(IntPtr process)=>ElevatedOrNull(process)??false;

    /// Asks Explorer to start this executable again with <paramref name="args"/>
    /// (through a shortcut, the one form "explorer.exe PATH" passes arguments
    /// with). True when Explorer took it; the caller then exits.
    internal static bool RelaunchThroughShell(IReadOnlyList<string> args)
    {
        try
        {
            var exe=Environment.ProcessPath;if(exe is null)return false;
            var folder=Path.Combine(Path.GetTempPath(),"Nashi-relaunch");Directory.CreateDirectory(folder);
            foreach(var old in Directory.EnumerateFiles(folder,"*.lnk"))try{if(File.GetCreationTimeUtc(old)<DateTime.UtcNow.AddMinutes(-5))File.Delete(old);}catch(IOException){}
            var link=Path.Combine(folder,"Nashi-"+Guid.NewGuid().ToString("N")+".lnk");
            var type=Type.GetTypeFromProgID("WScript.Shell");if(type is null)return false;
            dynamic shell=Activator.CreateInstance(type)!;dynamic shortcut=shell.CreateShortcut(link);
            try{shortcut.TargetPath=exe;shortcut.Arguments=string.Join(" ",args.Select(Quote));shortcut.WorkingDirectory=Path.GetDirectoryName(exe)!;shortcut.Save();}
            finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
            using var explorer=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){ArgumentList={link},UseShellExecute=false});
            return explorer is not null;
        }
        catch(Exception error)when(error is COMException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException){return false;}
    }
    private static string Quote(string arg)=>arg.Length>0&&!arg.Any(c=>char.IsWhiteSpace(c)||c=='"')?arg:"\""+arg.Replace("\"","\\\"")+"\"";
}
