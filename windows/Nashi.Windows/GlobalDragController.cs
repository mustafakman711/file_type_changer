using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Nashi.Core;
namespace Nashi.Windows;

/// Reads only the three configured modifiers and primary mouse button. It never
/// records keystrokes or polls the clipboard. Actual files/links arrive through
/// OLE drag/drop after the user chooses a wheel segment.
///
/// Input is event driven: a low-level mouse hook on a dedicated thread reports
/// presses, releases and (only while a button is held) moves. Modifier checks
/// run solely between a drag crossing the 10 px threshold and the release, at
/// the same 40 ms cadence as before plus on every move, so the wheel opens with
/// the same latency while an idle Nashi performs no periodic work at all. If
/// the hook cannot be installed the original 40 ms polling is used instead.
public sealed class GlobalDragController:IDisposable
{
    [StructLayout(LayoutKind.Sequential)]internal struct NativePoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr window,int id);
    [DllImport("oleacc.dll")]private static extern int AccessibleObjectFromPoint(NativePoint point,[MarshalAs(UnmanagedType.Interface)]out object accessible,[MarshalAs(UnmanagedType.Struct)]out object child);
    private readonly Func<bool> available;private readonly PreferencesStore preferences;private readonly Action<string,IDataObject,string?> dropped;private readonly Action link;private readonly System.Windows.Threading.DispatcherTimer timer;
    private readonly DragGesture gesture=new();private readonly bool registerShortcut;
    private Task<string?>? hint;private GlobalDropWheel? wheel;private HwndSource? source;private IntPtr handle;private bool? shortcutEnabled;private MouseHookThread? mouse;private bool polling;private bool disposed;
    internal GlobalDropWheel? ActiveWheel=>wheel;
    internal bool EventDriven=>mouse?.Installed==true;
    /// True while a drag is being evaluated; false means Nashi is idle.
    internal bool Checking=>timer.IsEnabled;
    /// Raised when a drag ends, so the resident host can trim memory later.
    internal event Action? GestureEnded;
    public bool LinkShortcutRegistered{get;private set;}
    private bool OwnerAvailable=>available();
    public GlobalDragController(Window owner,PreferencesStore preferences,Action<string,IDataObject,string?> dropped,Action link)
        :this(()=>DesktopGeometry.IsInteractive(owner),preferences,dropped,link,true)
    {
        if(new WindowInteropHelper(owner).Handle!=IntPtr.Zero)Register(HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle));else owner.SourceInitialized+=(_,_)=>Register(HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle));
    }
    /// For the light resident instance: input arrives on a hidden message window.
    internal GlobalDragController(HwndSource window,Func<bool> available,PreferencesStore preferences,Action<string,IDataObject,string?> dropped,Action link,bool registerShortcut=true)
        :this(available,preferences,dropped,link,registerShortcut)=>Register(window);
    private GlobalDragController(Func<bool> available,PreferencesStore preferences,Action<string,IDataObject,string?> dropped,Action link,bool registerShortcut)
    {
        this.available=available;this.preferences=preferences;this.dropped=dropped;this.link=link;this.registerShortcut=registerShortcut;timer=new(){Interval=TimeSpan.FromMilliseconds(40)};timer.Tick+=(_,_)=>Tick();
    }
    private void Register(HwndSource window)
    {
        if(disposed)return;source=window;handle=window.Handle;source.AddHook(Hook);UpdateShortcut();
        mouse=new MouseHookThread(handle);if(!mouse.Installed){mouse.Dispose();mouse=null;polling=true;timer.Start();}
    }
    /// Re-reads the Enabled preference; called whenever settings change.
    internal void UpdateShortcut(){if(handle==IntPtr.Zero||!registerShortcut)return;var enabled=preferences.Value.Enabled;if(shortcutEnabled==enabled)return;shortcutEnabled=enabled;if(LinkShortcutRegistered)UnregisterHotKey(handle,7701);LinkShortcutRegistered=enabled&&RegisterHotKey(handle,7701,0x4000|0x2|0x4,0x44);}
    /// Reinstalls the low-level hook in case Windows dropped it during heavy work.
    internal void RefreshHook()=>mouse?.Refresh();
    private IntPtr Hook(IntPtr window,int message,IntPtr w,IntPtr l,ref bool handled)
    {
        switch(message)
        {
            case 0x0312 when w.ToInt32()==7701:if(preferences.Value.Enabled&&OwnerAvailable)link();handled=true;break;
            case MouseHookThread.DownMessage:{var (x,y)=MouseHookThread.Unpack(l.ToInt64());Pressed(x,y);handled=true;break;}
            case MouseHookThread.MoveMessage:mouse?.MoveHandled();if(mouse is not null){var (x,y)=mouse.Latest;Moved(x,y);}handled=true;break;
            case MouseHookThread.UpMessage:Released();handled=true;break;
        }
        return IntPtr.Zero;
    }
    private static bool Held(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    private static string Chord()=>DragGesture.Chord(Held(0x11),Held(0x10),Held(0x12));
    private bool Allowed()
    {
        if(preferences.Value.Enabled&&OwnerAvailable)return true;
        wheel?.Close();wheel=null;hint=null;if(gesture.Tracking)gesture.Release();if(!polling)timer.Stop();return false;
    }
    private void Pressed(int x,int y){if(disposed||!Allowed())return;hint=null;gesture.Press(x,y);}
    private void Moved(int x,int y)
    {
        if(disposed||!gesture.Tracking||!Allowed())return;
        if(gesture.Move(x,y,PrimaryMouseButton.IsPressed)){timer.Start();}
        if(gesture.Armed)Evaluate(x,y);
    }
    private void Released()
    {
        if(!gesture.Tracking)return;if(!polling)timer.Stop();hint=null;
        var opened=gesture.Release();
        if(opened&&wheel is {IsVisible:true}){var old=wheel;var dismiss=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};dismiss.Tick+=(_,_)=>{dismiss.Stop();if(old.IsVisible)old.Close();};dismiss.Start();}
        wheel=null;if(opened)GestureEnded?.Invoke();
    }
    private void Tick()
    {
        if(!Allowed())return;GetCursorPos(out var point);
        if(!PrimaryMouseButton.IsPressed){if(gesture.Tracking)Released();return;}
        if(polling&&!gesture.Tracking){gesture.Press(point.X,point.Y);return;} // fallback: first tick that sees the button
        if(polling)gesture.Move(point.X,point.Y,true);
        if(gesture.Armed)Evaluate(point.X,point.Y);
    }
    private void Evaluate(int x,int y)
    {
        var start=new NativePoint{X=gesture.StartX,Y=gesture.StartY};hint??=Task.Run(()=>LinkAt(start));var p=preferences.Value;var kind=gesture.Evaluate(Chord(),p);if(kind is null)return;
        var sourceHint=hint;wheel=new GlobalDropWheel(kind,p.WheelScale,(data,id)=>dropped(id,data,sourceHint?.IsCompletedSuccessfully==true?sourceHint.Result:null),()=>sourceHint?.IsCompletedSuccessfully==true?sourceHint.Result:null);wheel.Show();DesktopGeometry.PlaceWheel(wheel,x,y,WheelPresentation.Diameter*p.WheelScale);
        // The timer keeps running until the release, as a safety net should
        // Windows ever drop the hook's button-up notification.
    }
    private static string? LinkAt(NativePoint point)
    {
        try
        {
            var element=AutomationElement.FromPoint(new Point(point.X,point.Y));for(var n=0;n<7&&element is not null;n++,element=TreeWalker.ControlViewWalker.GetParent(element))
            {
                if(element.Current.IsPassword||element.Current.ControlType==ControlType.Edit)return null;
                if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&WebMediaPayload.Parse(((ValuePattern)value).Current.Value) is {} result)return result.Url;
            }
            if(AccessibleObjectFromPoint(point,out var accessible,out var child)>=0)
            {
                try{for(var n=0;n<4;n++){var value=accessible.GetType().InvokeMember("accValue",System.Reflection.BindingFlags.GetProperty,null,accessible,[child])?.ToString();if(WebMediaPayload.Parse(value) is {} link)return link.Url;var parent=accessible.GetType().InvokeMember("accParent",System.Reflection.BindingFlags.GetProperty,null,accessible,null);if(parent is null)break;Marshal.FinalReleaseComObject(accessible);accessible=parent;child=0;}}finally{if(Marshal.IsComObject(accessible))Marshal.FinalReleaseComObject(accessible);}
            }
        }catch(ElementNotAvailableException){}catch(InvalidOperationException){}catch(COMException){}catch(System.Reflection.TargetInvocationException){}catch(MissingMethodException){}return null;
    }
    public void Dispose(){if(disposed)return;disposed=true;timer.Stop();mouse?.Dispose();mouse=null;wheel?.Close();if(handle!=IntPtr.Zero&&LinkShortcutRegistered)UnregisterHotKey(handle,7701);source?.RemoveHook(Hook);}
}

internal sealed class GlobalDropWheel:Window
{
    internal string State=>$"entered={entered}, over={over}, dropped={dropCount}, choices={choices.Count}, selection={selection}";private int entered,over,dropCount;
    private readonly string mode;private readonly Action<IDataObject,string> chosen;private readonly Func<string?> hint;private readonly Canvas canvas=new(){Width=540,Height=540,Background=Brushes.Transparent,AllowDrop=true};private string selection="";private IReadOnlyList<ToolChoice> choices=[];
    public GlobalDropWheel(string mode,double scale,Action<IDataObject,string> chosen,Func<string?> hint)
    {
        this.mode=mode;this.chosen=chosen;this.hint=hint;Title="Nashi · "+mode;Width=Height=WheelPresentation.Diameter*scale;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;AllowDrop=true;Content=new Viewbox{Child=canvas};Build([]);Loaded+=(_,_)=>WheelPresentation.Animate(this);
        DragEnter+=(_,e)=>{entered++;Track(e);};
        DragOver+=(_,e)=>{over++;Track(e);};
        Drop+=(_,e)=>{dropCount++;Track(e);var id=selection;Close();if(id.Length>0)chosen(e.Data,id);e.Handled=true;};PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
    }
    private void Track(DragEventArgs e)
    {
        var files=e.Data.GetData(DataFormats.FileDrop) as string[]??[];var updated=Choices(files,Payload(e.Data));
        // Replacing the hit-test target during DragEnter causes OLE to enter
        // again instead of advancing to DragOver. Keep the visual tree stable
        // until the payload actually changes, and select on entry as well.
        if(!updated.SequenceEqual(choices)){choices=updated;Build(choices);}
        var p=e.GetPosition(canvas);var distance=(p-new Point(270,270)).Length;
        if(distance<108||distance>250||choices.Count==0){selection="";Highlight(-1);}
        else{var angle=(Math.Atan2(p.Y-270,p.X-270)*180/Math.PI-WheelPresentation.StartAngle(choices.Count)+360+180d/choices.Count)%360;var index=(int)(angle/(360d/choices.Count));var id=choices[index].Id;if(id!=selection){selection=id;Feedback.Play();}Highlight(index);}
        e.Effects=selection.Length>0?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;
    }
    private WebMediaPayload? Payload(IDataObject data)=>DragPayload.Parse(data,hint());
    private IReadOnlyList<ToolChoice> Choices(string[] files,WebMediaPayload? web)
    {
        if(files.Length>0&&web is null)
        {
            if(mode=="convert")return FeatureCatalog.Formats(files).Select(f=>new ToolChoice("convert:"+f,f.ToUpperInvariant(),"↗")).ToArray();
            if(mode=="actions")return [new("share","Share","◉"),new("float","Float","▣"),new("copy","Copy","▱"),new("move","Move","↗"),new("copyPath","Copy Path","↪"),new("terminal","Terminal",">_")];
            return FeatureCatalog.Tools(files).Select(t=>new ToolChoice(t,FeatureCatalog.ToolNames[t],MainWindow.ToolSymbol(t))).ToArray();
        }
        if(web is null)return [];return web.Kind switch{"youtube"=>[new("download:video","Download Video","↓"),new("download:thumbnail","Download Thumbnail","▣"),new("download:audio","Download Audio","♫")],"instagramPhotos"=>[new("download:photos","Download Images","▣")],"instagramReel"=>[new("download:video","Download Video","↓"),new("download:audio","Download Audio","♫")],"image"=>[new("download:image","Download Image","↓")],_=>[new("download:video","Download Video","↓"),new("download:audio","Download Audio","♫") ]};
    }
    private void Highlight(int index){var paths=canvas.Children.OfType<System.Windows.Shapes.Path>().ToArray();for(var n=0;n<paths.Length;n++){paths[n].Fill=Theme.Brush(n==index?"Sage":"Inset");paths[n].Stroke=Theme.Brush(n==index?"Gold":"Line");}}
    private void Build(IReadOnlyList<ToolChoice> tools)
    {
        canvas.Children.Clear();canvas.Children.Add(new Ellipse{Width=540,Height=540,Fill=WheelPresentation.Backdrop});var count=tools.Count;
        for(var i=0;i<count;i++)
        {
            var angle=(WheelPresentation.StartAngle(count)+360d*i/count)*Math.PI/180;var half=(180d/count-1)*Math.PI/180;Point P(double r,double a)=>new(270+r*Math.Cos(a),270+r*Math.Sin(a));var a=angle-half;var b=angle+half;var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(P(108,a),true,true);g.LineTo(P(250,a),true,false);g.ArcTo(P(250,b),new(250,250),0,b-a>Math.PI,SweepDirection.Clockwise,true,false);g.LineTo(P(108,b),true,false);g.ArcTo(P(108,a),new(108,108),0,b-a>Math.PI,SweepDirection.Counterclockwise,true,false);}canvas.Children.Add(new System.Windows.Shapes.Path{Data=geometry,Fill=Theme.Brush("Inset"),Stroke=Theme.Brush("Line")});
            var (stack,location)=WheelPresentation.Caption(tools[i],geometry,angle,half,count);var label=new Border{Child=stack,Width=stack.DesiredSize.Width,Height=stack.DesiredSize.Height,IsHitTestVisible=false};Canvas.SetLeft(label,location.X-label.Width/2);Canvas.SetTop(label,location.Y-label.Height/2);canvas.Children.Add(label);
        }
        var center=new Border{Width=180,Height=180,IsHitTestVisible=false,Child=L10n.Label(new TextBlock{Text=count==0?"Keep dragging\nDrop a file or link":mode=="convert"?"Convert":mode=="actions"?"File actions":"Tools",TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=20,FontWeight=FontWeights.SemiBold,Foreground=Theme.Brush("Ink")} )};Canvas.SetLeft(center,180);Canvas.SetTop(center,180);canvas.Children.Add(center);
    }
}
