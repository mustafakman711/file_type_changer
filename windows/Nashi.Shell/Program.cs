using System.Runtime.InteropServices;
namespace Nashi.Shell;

/// Out-of-process COM server (packaged com:ExeServer, started by COM with
/// -Embedding). It registers the two Explorer command classes, serves Explorer
/// while menus are built, and exits after two idle minutes.
public static class Server
{
    internal const string ConvertClass="5e2c7a41-9b3d-4f6a-8c1e-2d7b9a4f6c31",ToolsClass="5e2c7a41-9b3d-4f6a-8c1e-2d7b9a4f6c32";
    private const uint CLSCTX_LOCAL_SERVER=4,REGCLS_MULTIPLEUSE=1,REGCLS_SUSPENDED=4;
    private static readonly TimeSpan IdleExit=TimeSpan.FromMinutes(2);
    private static long lastActivity=Environment.TickCount64;private static int busy;
    private static readonly ClassFactory convertFactory=new(()=>new ConvertMenu()),toolsFactory=new(()=>new ToolsCommand());
    internal static void Touch()=>Interlocked.Exchange(ref lastActivity,Environment.TickCount64);
    internal static void Busy(int change){Interlocked.Add(ref busy,change);Touch();}

    [MTAThread]
    public static int Main(string[] args)
    {
        if(!args.Any(a=>a.TrimStart('-','/').Equals("Embedding",StringComparison.OrdinalIgnoreCase)))return 0; // only COM starts this server
        var convert=new Guid(ConvertClass);var tools=new Guid(ToolsClass);
        if(Native.CoRegisterClassObject(ref convert,convertFactory,CLSCTX_LOCAL_SERVER,REGCLS_MULTIPLEUSE|REGCLS_SUSPENDED,out var first)!=Native.S_OK)return 1;
        if(Native.CoRegisterClassObject(ref tools,toolsFactory,CLSCTX_LOCAL_SERVER,REGCLS_MULTIPLEUSE|REGCLS_SUSPENDED,out var second)!=Native.S_OK){Native.CoRevokeClassObject(first);return 1;}
        Native.CoResumeClassObjects();
        try{while(Volatile.Read(ref busy)>0||Environment.TickCount64-Interlocked.Read(ref lastActivity)<IdleExit.TotalMilliseconds)Thread.Sleep(2000);}
        finally{Native.CoRevokeClassObject(second);Native.CoRevokeClassObject(first);}
        return 0;
    }
}
