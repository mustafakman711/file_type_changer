using System.Runtime.InteropServices;
namespace Nashi.Windows;

/// A WH_MOUSE_LL hook on its own message-only thread. The callback never
/// blocks and never touches WPF: it records a button press/release and the
/// latest pointer position, and posts at most one pending message to the UI
/// window. Moves are ignored entirely unless a button is held, so an idle
/// desktop costs Nashi nothing.
internal sealed class MouseHookThread:IDisposable
{
    internal const int DownMessage=0x8000+741,UpMessage=0x8000+742,MoveMessage=0x8000+743;
    private const int WH_MOUSE_LL=14,WM_MOUSEMOVE=0x200,WM_LBUTTONDOWN=0x201,WM_LBUTTONUP=0x202,WM_RBUTTONDOWN=0x204,WM_RBUTTONUP=0x205,WM_QUIT=0x12,WM_APP_REFRESH=0x8000+744;
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)]private struct Msg{public IntPtr Window;public uint Message;public IntPtr W,L;public uint Time;public int X,Y;}
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int type,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]private static extern int GetMessage(out Msg message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Msg message,IntPtr window,uint min,uint max,uint remove);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);

    private readonly IntPtr target;private readonly HookProc proc;private readonly Thread thread;private readonly ManualResetEventSlim ready=new();
    private IntPtr hook;private uint threadId;private int trackedButton;private int pendingMove;private long latest;private volatile bool disposed;
    internal bool Installed=>hook!=IntPtr.Zero;
    /// Latest pointer position while a button is held, packed like an LPARAM.
    internal (int X,int Y) Latest=>Unpack(Interlocked.Read(ref latest));

    internal MouseHookThread(IntPtr target)
    {
        this.target=target;proc=Callback;
        thread=new Thread(Run){IsBackground=true,Name="Nashi drag gesture",Priority=ThreadPriority.AboveNormal};thread.Start();ready.Wait(TimeSpan.FromSeconds(5));
    }
    private void Run()
    {
        threadId=GetCurrentThreadId();PeekMessage(out _,IntPtr.Zero,0,0,0); // create the thread's queue before anyone posts to it
        Install();ready.Set();
        while(GetMessage(out var message,IntPtr.Zero,0,0)>0)
        {
            if(message.Message==WM_APP_REFRESH){Uninstall();Install();}
        }
        Uninstall();
    }
    // A refresh keeps trackedButton, so a drag in progress still reports its release.
    private void Install()=>hook=SetWindowsHookEx(WH_MOUSE_LL,proc,GetModuleHandle(null),0);
    private void Uninstall(){if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
    /// Windows silently removes a low-level hook that ever exceeded its
    /// timeout. Reinstalling is cheap and is done after heavy work.
    internal void Refresh(){if(!disposed&&threadId!=0)PostThreadMessage(threadId,WM_APP_REFRESH,IntPtr.Zero,IntPtr.Zero);}
    internal void MoveHandled()=>Volatile.Write(ref pendingMove,0);

    private IntPtr Callback(int code,IntPtr message,IntPtr data)
    {
        if(code>=0)
        {
            var kind=(int)message;
            if(kind==WM_MOUSEMOVE)
            {
                if(trackedButton!=0)
                {
                    Interlocked.Exchange(ref latest,Pack(data));
                    if(Interlocked.Exchange(ref pendingMove,1)==0)PostMessage(target,MoveMessage,IntPtr.Zero,IntPtr.Zero);
                }
            }
            else if(kind is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                if(trackedButton==0){trackedButton=kind;var point=Pack(data);Interlocked.Exchange(ref latest,point);PostMessage(target,DownMessage,IntPtr.Zero,new IntPtr(point));}
            }
            else if(kind is WM_LBUTTONUP or WM_RBUTTONUP)
            {
                if(trackedButton!=0&&kind==trackedButton+1){trackedButton=0;PostMessage(target,UpMessage,IntPtr.Zero,IntPtr.Zero);}
            }
        }
        return CallNextHookEx(hook,code,message,data);
    }
    // MSLLHOOKSTRUCT begins with the screen POINT.
    private static long Pack(IntPtr data){var x=Marshal.ReadInt32(data,0);var y=Marshal.ReadInt32(data,4);return (long)(ushort)(short)Math.Clamp(x,short.MinValue,short.MaxValue)|((long)(ushort)(short)Math.Clamp(y,short.MinValue,short.MaxValue)<<16);}
    internal static (int X,int Y) Unpack(long value)=>((short)(value&0xFFFF),(short)((value>>16)&0xFFFF));
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(threadId!=0)PostThreadMessage(threadId,WM_QUIT,IntPtr.Zero,IntPtr.Zero);thread.Join(TimeSpan.FromSeconds(2));ready.Dispose();
    }
}
